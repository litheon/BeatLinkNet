using System.Collections.Concurrent;
using System.Threading.Channels;
using BeatLink.CrateDigger.Pdb;
using BeatLink.DbServer;
using Kaitai;
using Microsoft.Extensions.Logging;

namespace BeatLink.Data;

/// <summary>
/// <para>Watches for new metadata to become available for tracks loaded on players, and queries
/// the appropriate player for any desired track analysis sections (identified by the analysis file
/// extension and four-character tag type code) when that happens. Clients can express interest in
/// any number of different tag types, and each unique combination will be retrieved once, and sent
/// to any listeners who registered interest in that type. This allows new tags to be supported, as
/// they are discovered and parsed by
/// <a href="https://github.com/Deep-Symmetry/crate-digger">Crate Digger</a> without any code
/// changes to Beat Link.</para>
///
/// <para>Maintains a hot cache of analysis tag information for any track currently loaded in a
/// player, either on the main playback deck, or as a hot cue, since those tracks could start
/// playing instantly.</para>
///
/// <para>Implicitly honors the active/passive setting of the <see cref="MetadataFinder"/> (see
/// <see cref="MetadataFinder.IsPassive"/>), because tags are loaded in response to metadata
/// updates.</para>
/// </summary>
/// <remarks>
/// Ported from Deep Symmetry's beat-link <c>AnalysisTagFinder</c>, by James Elliott.
/// </remarks>
public sealed class AnalysisTagFinder : LifecycleParticipant
{
    private static readonly ILogger Logger = BeatLinkLogging.CreateLogger<AnalysisTagFinder>();

    /// <summary>
    /// Wraps values we store in our hot cache, so we can keep track of the track, file extension,
    /// and type tag the analysis section was associated with.
    /// </summary>
    public sealed class CacheEntry
    {
        /// <summary>
        /// Identifies the track to which the analysis section belongs.
        /// </summary>
        public DataReference DataReference { get; }

        /// <summary>
        /// Identifies the specific analysis file from which the cached section was loaded.
        /// </summary>
        public string FileExtension { get; }

        /// <summary>
        /// The four-character type code identifying the specific section of the analysis file that
        /// was cached.
        /// </summary>
        public string TypeTag { get; }

        /// <summary>
        /// The parsed analysis file section itself.
        /// </summary>
        public RekordboxAnlz.TaggedSection TaggedSection { get; }

        /// <summary>
        /// Constructor simply sets the immutable properties.
        /// </summary>
        /// <param name="dataReference">the track to which the analysis section belongs</param>
        /// <param name="fileExtension">identifies the specific analysis file from which the cached
        /// section was loaded</param>
        /// <param name="typeTag">the four-character type code identifying the specific section of
        /// the analysis file that was cached</param>
        /// <param name="taggedSection">the parsed analysis file section itself</param>
        internal CacheEntry(DataReference dataReference, string fileExtension, string typeTag,
            RekordboxAnlz.TaggedSection taggedSection)
        {
            DataReference = dataReference;
            FileExtension = fileExtension;
            TypeTag = typeTag;
            TaggedSection = taggedSection;
        }
    }

    /// <summary>
    /// Keeps track of the current analysis tags cached for each player, sub-indexed by type
    /// (typeTag + fileExtension). We hot cache data for any track which is currently on-deck in
    /// the player, as well as any that were loaded into a player's hot-cue slot.
    /// </summary>
    private readonly ConcurrentDictionary<DeckReference, ConcurrentDictionary<string, CacheEntry>> _hotCache = new();

    /// <summary>
    /// A queue used to hold metadata updates we receive from the <see cref="MetadataFinder"/> so
    /// we can process them on a lower priority thread, and not hold up delivery to more
    /// time-sensitive listeners. Present only while we are running.
    /// </summary>
    private volatile Channel<TrackMetadataUpdate>? _pendingUpdates;

    /// <summary>
    /// Our metadata listener just puts metadata updates on our queue, so we can process them on a
    /// lower priority thread, and not hold up delivery to more time-sensitive listeners.
    /// </summary>
    private void OnTrackMetadataChanged(object? sender, TrackMetadataUpdate update)
    {
        Logger.LogDebug("Received metadata update {Update}", update);
        _pendingUpdates?.Writer.TryWrite(update);
    }

    /// <summary>
    /// Our mount listener evicts any cached tags that belong to media databases which have been
    /// unmounted, since they are no longer valid.
    /// </summary>
    private void OnMediaUnmounted(object? sender, SlotReference slot)
    {
        // Iterate over copies to avoid concurrent modification issues.
        foreach (var deckEntry in _hotCache.ToArray())
        {
            foreach (var typeEntry in deckEntry.Value.ToArray())
            {
                if (ReferenceEquals(slot, SlotReference.GetSlotReference(typeEntry.Value.DataReference)))
                {
                    Logger.LogDebug("Evicting cached track analysis sections in response to unmount report {Entry}",
                        typeEntry.Value);
                    CacheEntry? removed = null;
                    if (_hotCache.TryGetValue(deckEntry.Key, out var deckCache) &&
                        deckCache.TryRemove(typeEntry.Key, out var removedEntry))
                    {
                        removed = removedEntry;
                    }
                    if (deckEntry.Key.HotCue == 0 && removed != null)
                    {
                        // We removed something that listeners cared about.
                        DeliverAnalysisTagUpdate(deckEntry.Key.Player, removed.FileExtension, removed.TypeTag, null);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Our device-lost handler watches for devices to disappear from the network, so we can
    /// discard all information about them.
    /// </summary>
    private void OnDeviceLost(object? sender, DeviceAnnouncement announcement)
    {
        if (announcement.DeviceNumber == 25 && announcement.DeviceName == "NXS-GW")
        {
            Logger.LogDebug("Ignoring noise from Kuvo gateways, especially in CDJ-3000s, which fight each other and come and go constantly.");
            return;
        }
        Logger.LogInformation("Clearing track analysis tags in response to the loss of a device, {Announcement}", announcement);
        ClearTags(announcement);
    }

    /// <summary>
    /// Keep track of whether we are running.
    /// </summary>
    private volatile bool _running;

    /// <summary>
    /// Check whether we are currently running. Unless the <see cref="MetadataFinder"/> is in
    /// passive mode, we will automatically request analysis tags from the appropriate player when
    /// a new track is loaded that is not found in the hot cache.
    /// </summary>
    /// <seealso cref="MetadataFinder.IsPassive"/>
    public override bool IsRunning => _running;

    /// <summary>
    /// The task processing our queued metadata updates on a separate thread so as not to slow down
    /// the high-priority update delivery thread; we perform potentially slow I/O.
    /// </summary>
    private Task? _queueHandler;

    /// <summary>
    /// We have received an update that invalidates the tags for a player, so clear them and alert
    /// any listeners if this represents a change. This does not affect the hot cues; they will
    /// stick around until the player loads a new track that overwrites one or more of them.
    /// </summary>
    /// <param name="update">the update which means we have no tags for the associated player</param>
    private void ClearDeckTags(TrackMetadataUpdate update)
    {
        if (_hotCache.TryRemove(DeckReference.GetDeckReference(update.Player, 0), out var oldTags))
        {
            DeliverTagLossUpdate(update.Player, oldTags);
        }
    }

    /// <summary>
    /// We have received notification that a device is no longer on the network, so clear out all
    /// its tags.
    /// </summary>
    /// <param name="announcement">the packet which reported the device's disappearance</param>
    private void ClearTags(DeviceAnnouncement announcement)
    {
        var player = announcement.DeviceNumber;
        // Iterate over a copy to avoid concurrent modification issues.
        foreach (var deck in _hotCache.Keys.ToArray())
        {
            if (deck.Player == player && _hotCache.TryRemove(deck, out var oldTags) && deck.HotCue == 0)
            {
                DeliverTagLossUpdate(player, oldTags);  // Inform listeners the tags are gone.
            }
        }
    }

    /// <summary>
    /// We have obtained a track analysis section for a device, so store it and alert any listeners.
    /// </summary>
    /// <param name="update">the update which caused us to retrieve this track analysis tag</param>
    /// <param name="fileExtension">identifies the specific analysis file for which an update is available</param>
    /// <param name="typeTag">the four-character type code identifying the specific section of the
    /// analysis file that has changed</param>
    /// <param name="analysisTag">the parsed track analysis section which we retrieved</param>
    private void UpdateAnalysisTag(TrackMetadataUpdate update, string fileExtension, string typeTag,
        RekordboxAnlz.TaggedSection analysisTag)
    {
        var cacheEntry = new CacheEntry(update.Metadata!.TrackReference, fileExtension, typeTag, analysisTag);
        var tagKey = typeTag + fileExtension;
        var deckMap = _hotCache.GetOrAdd(DeckReference.GetDeckReference(update.Player, 0),
            static _ => new ConcurrentDictionary<string, CacheEntry>());
        deckMap[tagKey] = cacheEntry;
        if (update.Metadata.CueList != null)
        {
            // Update the cache with any hot cues in this track as well.
            foreach (var entry in update.Metadata.CueList.Entries)
            {
                if (entry.HotCueNumber != 0)
                {
                    var cueMap = _hotCache.GetOrAdd(DeckReference.GetDeckReference(update.Player, entry.HotCueNumber),
                        static _ => new ConcurrentDictionary<string, CacheEntry>());
                    cueMap[tagKey] = cacheEntry;
                }
            }
        }
        DeliverAnalysisTagUpdate(update.Player, fileExtension, typeTag, analysisTag);
    }

    /// <summary>
    /// Get the analysis tags available for all tracks currently loaded in any player, either on
    /// the play deck, or in a hot cue. Returns a map of deck references to maps of typeTag +
    /// fileExtension to the actual cache entries.
    /// </summary>
    /// <exception cref="InvalidOperationException">if the AnalysisTagFinder is not running</exception>
    public IReadOnlyDictionary<DeckReference, IReadOnlyDictionary<string, CacheEntry>> LoadedAnalysisTags
    {
        get
        {
            EnsureRunning();
            // Make a copy so callers get an immutable snapshot of the current state.
            var result = new Dictionary<DeckReference, IReadOnlyDictionary<string, CacheEntry>>();
            foreach (var entry in _hotCache.ToArray())
            {
                result[entry.Key] = new Dictionary<string, CacheEntry>(entry.Value);
            }
            return result;
        }
    }

    /// <summary>
    /// Look up the analysis tag of a specific type we have for the track loaded in the main deck
    /// of a given player number.
    /// </summary>
    /// <param name="player">the device number whose analysis information for the playing track is desired</param>
    /// <param name="fileExtension">identifies the specific analysis file we are interested in a tag
    /// from (such as ".DAT" or ".EXT")</param>
    /// <param name="typeTag">the four-character type code identifying the specific section of the
    /// analysis file desired</param>
    /// <returns>the specified parsed track analysis tag for the track loaded on that player, if available</returns>
    /// <exception cref="InvalidOperationException">if the AnalysisTagFinder is not running</exception>
    public RekordboxAnlz.TaggedSection? GetLatestTrackAnalysisFor(int player, string fileExtension, string typeTag)
    {
        EnsureRunning();
        if (!_hotCache.TryGetValue(DeckReference.GetDeckReference(player, 0), out var deckTags))
        {
            return null;
        }
        return deckTags.TryGetValue(typeTag + fileExtension, out var entry) ? entry.TaggedSection : null;
    }

    /// <summary>
    /// Look up the track analysis tag of a specified type we have for a given player, identified
    /// by a status update received from that player.
    /// </summary>
    /// <param name="update">a status update from the player for which track analysis information is desired</param>
    /// <param name="fileExtension">identifies the specific analysis file we are interested in a tag from</param>
    /// <param name="typeTag">the four-character type code identifying the specific section of the
    /// analysis file desired</param>
    /// <returns>the desired parsed track analysis section for the track loaded on that player, if available</returns>
    /// <exception cref="InvalidOperationException">if the AnalysisTagFinder is not running</exception>
    public RekordboxAnlz.TaggedSection? GetLatestTrackAnalysisFor(DeviceUpdate update, string fileExtension, string typeTag)
    {
        ArgumentNullException.ThrowIfNull(update);
        return GetLatestTrackAnalysisFor(update.DeviceNumber, fileExtension, typeTag);
    }

    /// <summary>
    /// Ask the specified player for the analysis tag in the specified slot with the specified
    /// rekordbox ID, file extension, and tag type, using registered metadata providers instead if
    /// possible, and possibly giving up if we are in passive mode.
    /// </summary>
    /// <param name="trackReference">uniquely identifies the track whose analysis tag is desired</param>
    /// <param name="fileExtension">identifies the specific analysis file we are interested in a tag from</param>
    /// <param name="typeTag">the four-character type code identifying the specific section of the
    /// analysis file desired</param>
    /// <param name="failIfPassive">will prevent the request from taking place if we are in passive
    /// mode, so that automatic analysis updates will use registered providers only</param>
    /// <returns>the analysis section found, if any</returns>
    private RekordboxAnlz.TaggedSection? RequestAnalysisTagInternal(DataReference trackReference,
        string fileExtension, string typeTag, bool failIfPassive)
    {
        Logger.LogDebug("Trying to obtain: {Track} {TypeTag}{Extension}", trackReference, typeTag, fileExtension);

        // First see if any registered metadata providers can offer it for us (i.e. Crate Digger,
        // probably).
        var sourceDetails = MetadataFinder.Instance.GetMediaDetailsFor(trackReference.GetSlotReference());
        if (sourceDetails != null)
        {
            var provided = MetadataFinder.Instance.AllMetadataProviders.GetAnalysisSection(
                sourceDetails, trackReference, fileExtension, typeTag);
            if (provided != null)
            {
                return provided;
            }
        }

        Logger.LogDebug("Did not obtain via registered metadata providers; sourceDetails: {Details}", sourceDetails);

        // At this point, unless we are allowed to actively request the data, we are done. We can
        // always actively request tracks from rekordbox.
        if (MetadataFinder.Instance.IsPassive && failIfPassive &&
            trackReference.Slot != CdjStatus.TrackSourceSlot.Collection)
        {
            Logger.LogDebug("Giving up on trying to obtain: {Track} {TypeTag}{Extension}",
                trackReference, typeTag, fileExtension);
            return null;
        }

        // Even if we are supposed to be able to make metadata queries, the Opus Quad does not
        // have a dbserver port. (As with the MetadataFinder, we also give up quietly when the
        // player cannot be found at all, where the Java implementation would have thrown a
        // NullPointerException.)
        var announcement = DeviceFinder.Instance.GetLatestAnnouncementFrom(trackReference.Player);
        if (announcement == null || announcement.IsOpusQuad)
        {
            return null;
        }

        // We have to actually request the analysis using the dbserver protocol.
        try
        {
            Logger.LogDebug("Submitting tag task");
            return ConnectionManager.Instance.InvokeWithClientSession(trackReference.Player,
                client =>
                {
                    Logger.LogDebug("Tag task running");
                    return GetTagViaDbServer(trackReference.RekordboxId,
                        SlotReference.GetSlotReference(trackReference), fileExtension, typeTag, client);
                },
                $"requesting analysis tag of type {typeTag} from file with extension {fileExtension}");
        }
        catch (Exception e)
        {
            Logger.LogError(e, "Problem requesting analysis tag, returning null");
        }
        return null;
    }

    /// <summary>
    /// Ask the specified player for the specified track analysis information from the specified
    /// media slot, first checking if we have a cached copy.
    /// </summary>
    /// <param name="dataReference">uniquely identifies the track from which we want analysis information</param>
    /// <param name="fileExtension">identifies the specific analysis file we are interested in a tag from</param>
    /// <param name="typeTag">the four-character type code identifying the specific section of the
    /// analysis file desired</param>
    /// <returns>the parsed track analysis information, if it was found, or <c>null</c></returns>
    /// <exception cref="InvalidOperationException">if the AnalysisTagFinder is not running</exception>
    public RekordboxAnlz.TaggedSection? RequestAnalysisTagFrom(DataReference dataReference,
        string fileExtension, string typeTag)
    {
        ArgumentNullException.ThrowIfNull(dataReference);
        EnsureRunning();
        var tagKey = typeTag + fileExtension;
        foreach (var deckCache in _hotCache.Values)
        {
            if (deckCache.TryGetValue(tagKey, out var cached) && cached.DataReference.Equals(dataReference))
            {
                // Found a hot cue hit, use it.
                return cached.TaggedSection;
            }
        }
        return RequestAnalysisTagInternal(dataReference, fileExtension, typeTag, failIfPassive: false);
    }

    /// <summary>
    /// Converts a short (up to four character) string to the byte-reversed integer value used to
    /// represent that string in dbserver protocol requests for track analysis tags.
    /// </summary>
    /// <param name="s">the string to be converted</param>
    /// <returns>the numeric field the protocol uses to represent that string</returns>
    public NumberField StringToProtocolNumber(string s)
    {
        ArgumentNullException.ThrowIfNull(s);
        long fourcc = 0;  // Convert the type tag to a byte-reversed integer as used in the protocol.
        for (var i = 3; i >= 0; i--)
        {
            fourcc *= 256;
            if (i < s.Length)
            {
                fourcc += s[i];
            }
        }
        return new NumberField(fourcc);
    }

    /// <summary>
    /// Requests a specific analysis file section for a specific track ID, given a connection to a
    /// player that has already been set up.
    /// </summary>
    /// <param name="rekordboxId">the track whose analysis section is desired</param>
    /// <param name="slot">identifies the media slot we are querying</param>
    /// <param name="fileExtension">identifies the specific analysis file we are interested in a tag from</param>
    /// <param name="typeTag">the four-character type code identifying the specific section of the
    /// analysis file desired</param>
    /// <param name="client">the dbserver client that is communicating with the appropriate player</param>
    /// <returns>the retrieved track analysis section, or <c>null</c> if none was available</returns>
    internal RekordboxAnlz.TaggedSection? GetTagViaDbServer(int rekordboxId, SlotReference slot,
        string fileExtension, string typeTag, Client client)
    {
        var idField = new NumberField(rekordboxId);
        var fileField = StringToProtocolNumber(fileExtension[1..]);  // Omit the "."
        var tagField = StringToProtocolNumber(typeTag);

        try
        {
            Logger.LogDebug("Sending tag request to db server, tag: {Tag}, file: {File}",
                tagField.Value, fileField.Value);
            var response = client.SimpleRequest(Message.KnownType.AnlzTagReq, Message.KnownType.AnlzTag,
                client.BuildRmst(Message.MenuIdentifier.MainMenu, slot.Slot), idField, tagField, fileField);
            if (response.RecognizedType != Message.KnownType.Unavailable && response.Arguments[3].Size > 0)
            {
                var data = ((BinaryField)response.Arguments[3]).Value;
                // Skip the length, so we are at the tag four-character identifier.
                var sectionBytes = data[4..].ToArray();
                Logger.LogDebug("Received usable tag response from db server: {Response}", response);
                return new RekordboxAnlz.TaggedSection(new KaitaiStream(sectionBytes));
            }
            Logger.LogWarning("Did not receive usable tag response from db server: {Response}", response);
        }
        catch (Exception e)
        {
            Logger.LogWarning(e, "Problem requesting analysis tag information for slot {Slot}, id {Id}",
                slot, rekordboxId);
        }
        return null;
    }

    /// <summary>
    /// Keep track of the devices and specific tags we are currently trying to get analysis
    /// sections from in response to metadata updates. Keys are the device number concatenated
    /// with a colon, typeTag, and fileExtension.
    /// </summary>
    private readonly ConcurrentDictionary<string, byte> _activeRequests = new();

    /// <summary>
    /// Keeps track of the registered tag listeners, indexed by the type of tag they are listening
    /// for (typeTag + fileExtension).
    /// </summary>
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<EventHandler<AnalysisTagUpdate>, byte>> _analysisTagListeners = new();

    /// <summary>
    /// Serializes listener registration changes, standing in for the Java <c>synchronized</c>
    /// add/remove methods.
    /// </summary>
    private readonly object _listenerLock = new();

    /// <summary>
    /// <para>Adds the specified handler to receive updates when track analysis information of a
    /// specific type for a player changes. If <paramref name="handler"/> is already present in
    /// the set of registered listeners for the specified file extension and tag type, no action is
    /// performed.</para>
    ///
    /// <para>Updates are delivered through the <see cref="EventDispatcher"/>, so a UI application
    /// can arrange to receive them on its dispatch thread. (This keeps the keyed registration form
    /// of the Java <c>addAnalysisTagListener</c>, since a plain event cannot carry the file
    /// extension and tag type the registration selects.)</para>
    /// </summary>
    /// <param name="handler">the track analysis update handler to add</param>
    /// <param name="fileExtension">identifies the specific analysis file for which the listener
    /// wants tag updates (such as ".DAT" and ".EXT")</param>
    /// <param name="typeTag">the four-character type code identifying the specific section of the
    /// analysis file desired</param>
    public void AddAnalysisTagListener(EventHandler<AnalysisTagUpdate> handler, string fileExtension, string typeTag)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(fileExtension);
        ArgumentNullException.ThrowIfNull(typeTag);
        lock (_listenerLock)
        {
            var tagKey = typeTag + fileExtension;
            var trackingNewTag = false;
            if (!_analysisTagListeners.TryGetValue(tagKey, out var specificTagListeners))
            {
                trackingNewTag = true;
                specificTagListeners = new ConcurrentDictionary<EventHandler<AnalysisTagUpdate>, byte>();
                _analysisTagListeners[tagKey] = specificTagListeners;
            }
            specificTagListeners.TryAdd(handler, 0);
            if (trackingNewTag)
            {
                PrimeCache();  // Someone is interested in something new, so go get it.
            }
        }
    }

    /// <summary>
    /// Removes the specified handler so that it no longer receives updates when track analysis
    /// information of a specific type for a player changes. If <paramref name="handler"/> is not
    /// present in the set of registered listeners for that tag type, no action is performed.
    /// </summary>
    /// <param name="handler">the track analysis update handler to remove</param>
    /// <param name="fileExtension">identifies the specific analysis file for which the listener
    /// no longer wants tag updates</param>
    /// <param name="typeTag">the four-character type code identifying the specific section of the
    /// analysis file no longer desired</param>
    public void RemoveAnalysisTagListener(EventHandler<AnalysisTagUpdate> handler, string fileExtension, string typeTag)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(fileExtension);
        ArgumentNullException.ThrowIfNull(typeTag);
        lock (_listenerLock)
        {
            var tagKey = typeTag + fileExtension;
            if (_analysisTagListeners.TryGetValue(tagKey, out var specificTagListeners))
            {
                specificTagListeners.TryRemove(handler, out _);
                if (specificTagListeners.IsEmpty)
                {
                    // No listeners left of this type, remove the parent entry.
                    _analysisTagListeners.TryRemove(tagKey, out _);
                }
            }
        }
    }

    /// <summary>
    /// Get the sets of currently-registered analysis tag listeners. Returns a map whose keys are
    /// strings formed by concatenating each pair of typeTag and fileExtension strings that a
    /// registered listener has requested, and whose values are the sets of handlers interested in
    /// that type of analysis tag.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlySet<EventHandler<AnalysisTagUpdate>>> TagListeners
    {
        get
        {
            // Make a copy so callers get an immutable snapshot of the current state.
            var result = new Dictionary<string, IReadOnlySet<EventHandler<AnalysisTagUpdate>>>();
            foreach (var entry in _analysisTagListeners.ToArray())
            {
                result[entry.Key] = new HashSet<EventHandler<AnalysisTagUpdate>>(entry.Value.Keys);
            }
            return result;
        }
    }

    /// <summary>
    /// Send a track analysis tag update announcement to all registered listeners interested in the
    /// tag type.
    /// </summary>
    /// <param name="player">the player whose analysis information has changed</param>
    /// <param name="fileExtension">identifies the specific analysis file for which an update is available</param>
    /// <param name="typeTag">the four-character type code identifying the specific section of the
    /// analysis file that has changed</param>
    /// <param name="taggedSection">the new parsed track analysis information, if any</param>
    private void DeliverAnalysisTagUpdate(int player, string fileExtension, string typeTag,
        RekordboxAnlz.TaggedSection? taggedSection)
    {
        if (_analysisTagListeners.TryGetValue(typeTag + fileExtension, out var currentListeners))
        {
            // Iterate over a copy to avoid concurrent modification issues.
            var listeners = currentListeners.Keys.ToArray();
            if (listeners.Length > 0)
            {
                EventDispatcher.Post(() =>
                {
                    var update = new AnalysisTagUpdate(player, fileExtension, typeTag, taggedSection);
                    foreach (var listener in listeners)
                    {
                        try
                        {
                            listener(this, update);
                        }
                        catch (Exception t)
                        {
                            Logger.LogWarning(t, "Problem delivering track analysis tag update to listener");
                        }
                    }
                });
            }
        }
    }

    /// <summary>
    /// Report that a set of analysis tags is no longer available.
    /// </summary>
    /// <param name="player">the player for which we have lost information</param>
    /// <param name="tags">the tags which are no longer relevant</param>
    private void DeliverTagLossUpdate(int player, ConcurrentDictionary<string, CacheEntry> tags)
    {
        // We don't need to copy the map because it has already been removed from the hot cache, so
        // there is no risk of concurrent modification.
        foreach (var entry in tags.Values)
        {
            DeliverAnalysisTagUpdate(player, entry.FileExtension, entry.TypeTag, null);
        }
    }

    /// <summary>
    /// Process a metadata update from the <see cref="MetadataFinder"/>, and see if it means the
    /// track analysis information associated with any player has changed.
    /// </summary>
    /// <param name="update">describes the new metadata we have for a player, if any</param>
    private void HandleUpdate(TrackMetadataUpdate update)
    {
        Logger.LogDebug("HandleUpdate: {Update}", update);
        if (update.Metadata == null || update.Metadata.TrackType != CdjStatus.TrackType.Rekordbox)
        {
            ClearDeckTags(update);
        }
        else
        {
            // We can track analysis information for this device; check if we've already got it in
            // our cache.
            foreach (var trackedTag in _analysisTagListeners.Keys)
            {
                var fileExtension = trackedTag[trackedTag.IndexOf('.')..];
                var typeTag = trackedTag[..trackedTag.IndexOf('.')];
                var deckCache = _hotCache.GetValueOrDefault(DeckReference.GetDeckReference(update.Player, 0));
                CacheEntry? lastStructure = null;
                if (deckCache != null && deckCache.TryGetValue(trackedTag, out var found))
                {
                    lastStructure = found;
                }
                if (lastStructure == null || !lastStructure.DataReference.Equals(update.Metadata.TrackReference))
                {
                    // We have something new! First see if we can find the new information in the
                    // hot cache.
                    var foundInCache = false;
                    foreach (var tagCache in _hotCache.Values)
                    {
                        if (tagCache.TryGetValue(trackedTag, out var cached) &&
                            cached.DataReference.Equals(update.Metadata.TrackReference))
                        {
                            // Found a hot cue hit, use it.
                            UpdateAnalysisTag(update, fileExtension, typeTag, cached.TaggedSection);
                            foundInCache = true;
                            break;
                        }
                    }

                    // If not found in the cache try actually retrieving it. (Note: the Java
                    // implementation contains an indentation bug here which started the request
                    // thread even when the tag had been found in the cache or a request was
                    // already active; this port guards the thread with the same condition as the
                    // deck-clearing statement, which was clearly the intent.)
                    var activeKey = update.Player + ":" + trackedTag;
                    if (!foundInCache && _activeRequests.TryAdd(activeKey, 0))
                    {
                        ClearDeckTags(update);  // We won't know what it is until our request completes.
                        var thread = new Thread(() =>
                        {
                            try
                            {
                                var structure = RequestAnalysisTagInternal(
                                    update.Metadata.TrackReference, fileExtension, typeTag, failIfPassive: true);
                                if (structure != null)
                                {
                                    UpdateAnalysisTag(update, fileExtension, typeTag, structure);
                                }
                            }
                            catch (Exception e)
                            {
                                Logger.LogWarning(e, "Problem requesting analysis tag of type {TypeTag} in file with extension {Extension} from update {Update}",
                                    typeTag, fileExtension, update);
                            }
                            finally
                            {
                                _activeRequests.TryRemove(activeKey, out _);
                            }
                        })
                        {
                            IsBackground = true
                        };
                        thread.Start();
                    }
                }
            }
        }
    }

    /// <summary>
    /// Set once we have hooked up the handlers which allow us to automatically shut down when the
    /// services we depend on do, so we never subscribe more than once.
    /// </summary>
    private bool _dependencyListenersRegistered;

    /// <summary>
    /// Set up to automatically stop if anything we depend on stops.
    /// </summary>
    private void OnDependencyStopped(object? sender, EventArgs e)
    {
        if (IsRunning)
        {
            Logger.LogInformation("AnalysisTagFinder stopping because {Sender} has.", sender);
            Stop();
        }
    }

    /// <summary>
    /// Send ourselves "updates" about any tracks that were loaded before we started, or before we
    /// were requesting a newly-interesting tag type, since we missed them. Do nothing if someone
    /// registered interest in a particular tag before we were actually running, though.
    /// </summary>
    private void PrimeCache()
    {
        Logger.LogDebug("PrimeCache() running");
        if (IsRunning)
        {
            EventDispatcher.Post(() =>
            {
                if (!IsRunning)
                {
                    return;
                }
                foreach (var entry in MetadataFinder.Instance.LoadedTracks)
                {
                    if (entry.Key.HotCue == 0)
                    {
                        // The track is currently loaded in a main player deck.
                        _pendingUpdates?.Writer.TryWrite(new TrackMetadataUpdate(entry.Key.Player, entry.Value));
                    }
                }
            });
        }
    }

    /// <summary>
    /// Serializes lifecycle transitions, standing in for the Java <c>synchronized</c> start and
    /// stop methods.
    /// </summary>
    private readonly object _lifecycleLock = new();

    /// <summary>
    /// The body of the queue-handling task: processes queued metadata updates until the channel is
    /// completed (by <see cref="Stop"/>) or we are no longer running.
    /// </summary>
    /// <param name="reader">supplies the queued metadata updates</param>
    private void RunQueueHandler(ChannelReader<TrackMetadataUpdate> reader)
    {
        while (IsRunning)
        {
            TrackMetadataUpdate update;
            try
            {
                if (!reader.TryRead(out update!))
                {
                    if (!reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
                    {
                        break;  // The channel was completed, presumably due to shutdown.
                    }
                    continue;
                }
            }
            catch (Exception e)
            {
                Logger.LogDebug(e, "Queue read interrupted, presumably due to AnalysisTagFinder shutdown.");
                break;
            }
            if (!IsRunning)
            {
                break;
            }
            try
            {
                HandleUpdate(update);
            }
            catch (Exception e)
            {
                Logger.LogError(e, "Problem processing metadata update");
            }
        }
    }

    /// <summary>
    /// Start finding analysis tags for all active players. Starts the <see cref="MetadataFinder"/>
    /// if it is not already running, because we need it to send us metadata updates to notice when
    /// new tracks are loaded. This in turn starts the <see cref="DeviceFinder"/>, so we can keep
    /// track of the comings and goings of players themselves. We also start the
    /// <see cref="ConnectionManager"/> in order to make queries to obtain analysis information.
    /// </summary>
    /// <exception cref="Exception">if there is a problem starting the required components</exception>
    public void Start()
    {
        lock (_lifecycleLock)
        {
            if (IsRunning)
            {
                return;
            }
            if (!_dependencyListenersRegistered)
            {
                ConnectionManager.Instance.Stopped += OnDependencyStopped;
                MetadataFinder.Instance.Stopped += OnDependencyStopped;
                DeviceFinder.Instance.DeviceLost += OnDeviceLost;
                MetadataFinder.Instance.MediaUnmounted += OnMediaUnmounted;
                _dependencyListenersRegistered = true;
            }
            ConnectionManager.Instance.Start();
            MetadataFinder.Instance.Start();
            MetadataFinder.Instance.TrackMetadataChanged += OnTrackMetadataChanged;
            var channel = Channel.CreateBounded<TrackMetadataUpdate>(new BoundedChannelOptions(100)
            {
                FullMode = BoundedChannelFullMode.DropWrite,
                SingleReader = true
            });
            _pendingUpdates = channel;
            _running = true;
            _queueHandler = Task.Factory.StartNew(() => RunQueueHandler(channel.Reader), CancellationToken.None,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);
            DeliverLifecycleAnnouncement(Logger, true);
            PrimeCache();
        }
    }

    /// <summary>
    /// Stop finding analysis tag information for all active players.
    /// </summary>
    public void Stop()
    {
        lock (_lifecycleLock)
        {
            if (!IsRunning)
            {
                return;
            }
            MetadataFinder.Instance.TrackMetadataChanged -= OnTrackMetadataChanged;
            _running = false;
            _pendingUpdates?.Writer.TryComplete();
            _pendingUpdates = null;
            _queueHandler = null;

            // Report the loss of our analysis information, on the proper thread, outside our lock.
            var dyingCache = _hotCache.ToArray();
            _hotCache.Clear();
            EventDispatcher.Post(() =>
            {
                foreach (var entry in dyingCache)
                {
                    // Report the loss of our tags.
                    if (entry.Key.HotCue == 0)
                    {
                        DeliverTagLossUpdate(entry.Key.Player, entry.Value);
                    }
                }
            });
            DeliverLifecycleAnnouncement(Logger, false);
        }
    }

    /// <summary>
    /// The single instance of this class which exists in the program.
    /// </summary>
    public static AnalysisTagFinder Instance { get; } = new();

    /// <summary>
    /// Prevent direct instantiation.
    /// </summary>
    private AnalysisTagFinder()
    {
    }

    /// <inheritdoc/>
    public override string ToString()
    {
        var sb = new System.Text.StringBuilder("AnalysisTagFinder[running:").Append(IsRunning)
            .Append(", passive:").Append(MetadataFinder.Instance.IsPassive);
        if (IsRunning)
        {
            sb.Append(", loadedAnalysisTags:[").Append(string.Join(", ",
                LoadedAnalysisTags.Select(e => $"{e.Key}=[{string.Join(", ", e.Value.Keys)}]"))).Append(']');
        }
        return sb.Append(']').ToString();
    }
}
