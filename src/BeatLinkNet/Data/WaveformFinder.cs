using System.Collections.Concurrent;
using System.Threading.Channels;
using BeatLink.DbServer;
using Microsoft.Extensions.Logging;

namespace BeatLink.Data;

/// <summary>
/// <para>Watches for new metadata to become available for tracks loaded on players, and queries
/// the appropriate player for track waveforms when that happens. Can be configured to load only
/// the small waveform previews, or both those and the full scrollable waveform details.</para>
///
/// <para>Maintains a hot cache of waveforms for any track currently loaded in a player, either on
/// the main playback deck, or as a hot cue, since those tracks could start playing instantly.</para>
///
/// <para>Implicitly honors the active/passive setting of the <see cref="MetadataFinder"/> (see
/// <see cref="MetadataFinder.IsPassive"/>), because waveforms are loaded in response to metadata
/// updates.</para>
/// </summary>
/// <remarks>
/// Ported from Deep Symmetry's beat-link <c>WaveformFinder</c>, by James Elliott. The
/// <c>WaveformStyle</c> and <c>ThreeBandLayer</c> types the Java class nests are the top-level
/// <see cref="WaveformStyle"/> and <see cref="ThreeBandLayer"/> types in this port.
/// </remarks>
public sealed class WaveformFinder : LifecycleParticipant
{
    private static readonly ILogger Logger = BeatLinkLogging.CreateLogger<WaveformFinder>();

    /// <summary>
    /// Keeps track of the current waveform previews cached for each player. We hot cache data for
    /// any track which is currently on-deck in the player, as well as any that were loaded into a
    /// player's hot-cue slot.
    /// </summary>
    private readonly ConcurrentDictionary<DeckReference, WaveformPreview> _previewHotCache = new();

    /// <summary>
    /// Keeps track of the current waveform details cached for each player. We hot cache data for
    /// any track which is currently on-deck in the player, as well as any that were loaded into a
    /// player's hot-cue slot.
    /// </summary>
    private readonly ConcurrentDictionary<DeckReference, WaveformDetail> _detailHotCache = new();

    /// <summary>
    /// Should we ask for details as well as the previews?
    /// </summary>
    private volatile bool _findDetails = true;

    /// <summary>
    /// Whether we should retrieve the waveform details in addition to the waveform previews: when
    /// <c>true</c>, both types of waveform will be retrieved, when <c>false</c> only previews will
    /// be retrieved. Setting it to <c>true</c> immediately fetches details for any tracks that are
    /// already loaded on players; setting it to <c>false</c> discards any cached details and
    /// informs listeners that they are no longer available.
    /// </summary>
    public bool IsFindingDetails
    {
        get => _findDetails;
        set
        {
            _findDetails = value;
            if (value)
            {
                PrimeCache();  // Get details for any tracks that were already loaded on players.
            }
            else
            {
                // Inform our listeners, on the proper thread, that the detailed waveforms are no
                // longer available.
                var dyingCache = _detailHotCache.Keys.ToArray();
                _detailHotCache.Clear();
                EventDispatcher.Post(() =>
                {
                    foreach (var deck in dyingCache)
                    {
                        EventDispatcher.DeliverNow(WaveformDetailChanged, this,
                            new WaveformDetailUpdate(deck.Player, null), Logger);
                    }
                });
            }
        }
    }

    /// <summary>
    /// Keeps track of the waveform style that the user has specified they prefer to see. We may
    /// need to retrieve other styles as well, either because the preferred style is not available,
    /// or because a listener has requested another specific style, such as the
    /// <see cref="SignatureFinder"/>, which always needs RGB waveforms for track matching.
    /// </summary>
    private WaveformStyle _preferredStyle = WaveformStyle.Rgb;

    /// <summary>
    /// Guards changes to <see cref="PreferredStyle"/>, standing in for the Java
    /// <c>synchronized</c> setter.
    /// </summary>
    private readonly object _styleLock = new();

    /// <summary>
    /// The waveform style that the user prefers to see: waveforms will be retrieved in this style
    /// by default if no listeners for other specific formats have been registered, and it will be
    /// delivered preferentially to listeners that do not specify a format. Changing the value
    /// discards all cached waveforms and re-fetches them in the newly preferred style.
    /// (Experimental, like the Java <c>setPreferredStyle</c>.)
    /// </summary>
    public WaveformStyle PreferredStyle
    {
        get
        {
            lock (_styleLock)
            {
                return _preferredStyle;
            }
        }
        set
        {
            lock (_styleLock)
            {
                var oldStyle = _preferredStyle;
                _preferredStyle = value;
                if (oldStyle != value)
                {
                    ClearAllWaveforms();
                    PrimeCache();
                }
            }
        }
    }

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
    /// Re-delivers a metadata update to our queue when it is time to retry a request for a track
    /// that was still being analyzed.
    /// </summary>
    /// <param name="update">the metadata update to re-process</param>
    private void RetryHandleUpdate(TrackMetadataUpdate update) => _pendingUpdates?.Writer.TryWrite(update);

    /// <summary>
    /// Our mount listener evicts any cached waveforms that belong to media databases which have
    /// been unmounted, since they are no longer valid.
    /// </summary>
    private void OnMediaUnmounted(object? sender, SlotReference slot)
    {
        // Iterate over a copy to avoid concurrent modification issues.
        foreach (var entry in _previewHotCache.ToArray())
        {
            if (ReferenceEquals(slot, SlotReference.GetSlotReference(entry.Value.DataReference)))
            {
                Logger.LogDebug("Evicting cached waveform preview in response to unmount report {Preview}", entry.Value);
                _previewHotCache.TryRemove(entry.Key, out _);
            }
        }

        // Again iterate over a copy to avoid concurrent modification issues.
        foreach (var entry in _detailHotCache.ToArray())
        {
            if (ReferenceEquals(slot, SlotReference.GetSlotReference(entry.Value.DataReference)))
            {
                Logger.LogDebug("Evicting cached waveform detail in response to unmount report {Detail}", entry.Value);
                _detailHotCache.TryRemove(entry.Key, out _);
            }
        }
    }

    /// <summary>
    /// Our device-lost handler watches for devices to disappear from the network so we can discard
    /// all information about them.
    /// </summary>
    private void OnDeviceLost(object? sender, DeviceAnnouncement announcement)
    {
        if (announcement.DeviceNumber == 25 && announcement.DeviceName == "NXS-GW")
        {
            Logger.LogDebug("Ignoring departure of Kuvo gateway, which fight each other and come and go constantly, especially in CDJ-3000s.");
            return;
        }
        Logger.LogInformation("Clearing waveforms in response to the loss of a device, {Announcement}", announcement);
        ClearWaveforms(announcement);
    }

    /// <summary>
    /// Keep track of whether we are running.
    /// </summary>
    private volatile bool _running;

    /// <summary>
    /// Check whether we are currently running. Unless the <see cref="MetadataFinder"/> is in
    /// passive mode, we will automatically request waveforms from the appropriate player when a
    /// new track is loaded that is not found in the hot cache.
    /// </summary>
    /// <seealso cref="MetadataFinder.IsPassive"/>
    public override bool IsRunning => _running;

    /// <summary>
    /// The task processing our queued metadata updates on a separate thread so as not to slow down
    /// the high-priority update delivery thread; we perform potentially slow I/O.
    /// </summary>
    private Task? _queueHandler;

    /// <summary>
    /// We have received an update that invalidates the waveform preview for a player, so clear it
    /// and alert any listeners if this represents a change. This does not affect the hot cues;
    /// they will stick around until the player loads a new track that overwrites one or more of
    /// them.
    /// </summary>
    /// <param name="update">the update which means we have no waveform preview for the associated player</param>
    private void ClearDeckPreview(TrackMetadataUpdate update)
    {
        if (_previewHotCache.TryRemove(DeckReference.GetDeckReference(update.Player, 0), out _))
        {
            DeliverWaveformPreviewUpdate(update.Player, null);
        }
    }

    /// <summary>
    /// We have received an update that invalidates the waveform detail for a player, so clear it
    /// and alert any listeners if this represents a change. This does not affect the hot cues;
    /// they will stick around until the player loads a new track that overwrites one or more of
    /// them.
    /// </summary>
    /// <param name="update">the update which means we have no waveform detail for the associated player</param>
    private void ClearDeckDetail(TrackMetadataUpdate update)
    {
        if (_detailHotCache.TryRemove(DeckReference.GetDeckReference(update.Player, 0), out _))
        {
            DeliverWaveformDetailUpdate(update.Player, null);
        }
    }

    /// <summary>
    /// We have received an update that invalidates any previous metadata for a player, so clear
    /// its waveforms, and alert any listeners if this represents a change. This does not affect
    /// the hot cues; they will stick around until the player loads a new track that overwrites one
    /// or more of them.
    /// </summary>
    /// <param name="update">the update which means we have no metadata for the associated player</param>
    private void ClearDeck(TrackMetadataUpdate update)
    {
        ClearDeckPreview(update);
        ClearDeckDetail(update);
    }

    /// <summary>
    /// We have received notification that a device is no longer on the network, so clear out all
    /// its waveforms.
    /// </summary>
    /// <param name="announcement">the packet which reported the device's disappearance</param>
    private void ClearWaveforms(DeviceAnnouncement announcement)
    {
        var player = announcement.DeviceNumber;
        // Iterate over a copy to avoid concurrent modification issues.
        foreach (var deck in _previewHotCache.Keys.ToArray())
        {
            if (deck.Player == player && _previewHotCache.TryRemove(deck, out _) && deck.HotCue == 0)
            {
                DeliverWaveformPreviewUpdate(player, null);  // Inform listeners that preview is gone.
            }
        }
        // Again iterate over a copy to avoid concurrent modification issues.
        foreach (var deck in _detailHotCache.Keys.ToArray())
        {
            if (deck.Player == player && _detailHotCache.TryRemove(deck, out _) && deck.HotCue == 0)
            {
                DeliverWaveformDetailUpdate(player, null);  // Inform listeners that detail is gone.
            }
        }
    }

    /// <summary>
    /// We have obtained a waveform preview for a device, so store it and alert any listeners.
    /// </summary>
    /// <param name="update">the update which caused us to retrieve this waveform preview</param>
    /// <param name="preview">the waveform preview which we retrieved</param>
    private void UpdatePreview(TrackMetadataUpdate update, WaveformPreview preview)
    {
        _previewHotCache[DeckReference.GetDeckReference(update.Player, 0)] = preview;  // Main deck
        if (update.Metadata?.CueList != null)
        {
            // Update the cache with any hot cues in this track as well.
            foreach (var entry in update.Metadata.CueList.Entries)
            {
                if (entry.HotCueNumber != 0)
                {
                    _previewHotCache[DeckReference.GetDeckReference(update.Player, entry.HotCueNumber)] = preview;
                }
            }
        }
        DeliverWaveformPreviewUpdate(update.Player, preview);
    }

    /// <summary>
    /// We have obtained waveform detail for a device, so store it and alert any listeners.
    /// </summary>
    /// <param name="update">the update which caused us to retrieve this waveform detail</param>
    /// <param name="detail">the waveform detail which we retrieved</param>
    private void UpdateDetail(TrackMetadataUpdate update, WaveformDetail detail)
    {
        _detailHotCache[DeckReference.GetDeckReference(update.Player, 0)] = detail;  // Main deck
        if (update.Metadata?.CueList != null)
        {
            // Update the cache with any hot cues in this track as well.
            foreach (var entry in update.Metadata.CueList.Entries)
            {
                if (entry.HotCueNumber != 0)
                {
                    _detailHotCache[DeckReference.GetDeckReference(update.Player, entry.HotCueNumber)] = detail;
                }
            }
        }
        DeliverWaveformDetailUpdate(update.Player, detail);
    }

    /// <summary>
    /// Get the waveform previews available for all tracks currently loaded in any player, either
    /// on the play deck, or in a hot cue.
    /// </summary>
    /// <exception cref="InvalidOperationException">if the WaveformFinder is not running</exception>
    public IReadOnlyDictionary<DeckReference, WaveformPreview> LoadedPreviews
    {
        get
        {
            EnsureRunning();
            // Make a copy so callers get an immutable snapshot of the current state.
            return new Dictionary<DeckReference, WaveformPreview>(_previewHotCache);
        }
    }

    /// <summary>
    /// Get the waveform details available for all tracks currently loaded in any player, either on
    /// the play deck, or in a hot cue.
    /// </summary>
    /// <exception cref="InvalidOperationException">if the WaveformFinder is not running or is not
    /// requesting waveform details</exception>
    public IReadOnlyDictionary<DeckReference, WaveformDetail> LoadedDetails
    {
        get
        {
            EnsureRunning();
            if (!IsFindingDetails)
            {
                throw new InvalidOperationException("WaveformFinder is not configured to find waveform details.");
            }
            // Make a copy so callers get an immutable snapshot of the current state.
            return new Dictionary<DeckReference, WaveformDetail>(_detailHotCache);
        }
    }

    /// <summary>
    /// Look up the waveform preview we have for the track loaded in the main deck of a given
    /// player number.
    /// </summary>
    /// <param name="player">the device number whose waveform preview for the playing track is desired</param>
    /// <returns>the waveform preview for the track loaded on that player, if available</returns>
    /// <exception cref="InvalidOperationException">if the WaveformFinder is not running</exception>
    public WaveformPreview? GetLatestPreviewFor(int player)
    {
        EnsureRunning();
        return _previewHotCache.GetValueOrDefault(DeckReference.GetDeckReference(player, 0));
    }

    /// <summary>
    /// Look up the waveform preview we have for a given player, identified by a status update
    /// received from that player.
    /// </summary>
    /// <param name="update">a status update from the player for which a waveform preview is desired</param>
    /// <returns>the waveform preview for the track loaded on that player, if available</returns>
    /// <exception cref="InvalidOperationException">if the WaveformFinder is not running</exception>
    public WaveformPreview? GetLatestPreviewFor(DeviceUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        return GetLatestPreviewFor(update.DeviceNumber);
    }

    /// <summary>
    /// Look up the waveform detail we have for the track loaded in the main deck of a given player
    /// number.
    /// </summary>
    /// <param name="player">the device number whose waveform detail for the playing track is desired</param>
    /// <returns>the waveform detail for the track loaded on that player, if available</returns>
    /// <exception cref="InvalidOperationException">if the WaveformFinder is not running</exception>
    public WaveformDetail? GetLatestDetailFor(int player)
    {
        EnsureRunning();
        return _detailHotCache.GetValueOrDefault(DeckReference.GetDeckReference(player, 0));
    }

    /// <summary>
    /// Look up the waveform detail we have for a given player, identified by a status update
    /// received from that player.
    /// </summary>
    /// <param name="update">a status update from the player for which waveform detail is desired</param>
    /// <returns>the waveform detail for the track loaded on that player, if available</returns>
    /// <exception cref="InvalidOperationException">if the WaveformFinder is not running</exception>
    public WaveformDetail? GetLatestDetailFor(DeviceUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        return GetLatestDetailFor(update.DeviceNumber);
    }

    /// <summary>
    /// Ask the specified player for the waveform preview in the specified slot with the specified
    /// rekordbox ID, using registered metadata providers instead if possible, and possibly giving
    /// up if we are in passive mode.
    /// </summary>
    /// <param name="trackReference">uniquely identifies the desired waveform preview</param>
    /// <param name="fromUpdate">if not <c>null</c> this is an automatic update in response to a
    /// track change, so we should only try a dbserver query if we are not in passive mode</param>
    /// <returns>the waveform preview found, if any</returns>
    private WaveformPreview? RequestPreviewInternal(DataReference trackReference, TrackMetadataUpdate? fromUpdate)
    {
        // First see if any registered metadata providers can offer it for us.
        var sourceDetails = MetadataFinder.Instance.GetMediaDetailsFor(trackReference.GetSlotReference());
        if (sourceDetails != null)
        {
            var provided = MetadataFinder.Instance.AllMetadataProviders.GetWaveformPreview(sourceDetails, trackReference);
            if (provided != null)
            {
                return provided;
            }
        }

        // At this point, unless we are allowed to actively request the data, we are done. We can
        // always actively request tracks from rekordbox.
        if (MetadataFinder.Instance.IsPassive && fromUpdate != null &&
            trackReference.Slot != CdjStatus.TrackSourceSlot.Collection)
        {
            return null;
        }

        // We have to actually request the preview using the dbserver protocol.
        try
        {
            return ConnectionManager.Instance.InvokeWithClientSession(trackReference.Player,
                client => GetWaveformPreview(trackReference.RekordboxId, SlotReference.GetSlotReference(trackReference),
                    trackReference.TrackType, fromUpdate, client),
                "requesting waveform preview");
        }
        catch (Exception e)
        {
            Logger.LogError(e, "Problem requesting waveform preview, returning null");
        }
        return null;
    }

    /// <summary>
    /// Ask the specified player for the specified waveform preview from the specified media slot,
    /// first checking if we have a cached copy.
    /// </summary>
    /// <param name="dataReference">uniquely identifies the desired waveform preview</param>
    /// <returns>the preview, if it was found, or <c>null</c></returns>
    /// <exception cref="InvalidOperationException">if the WaveformFinder is not running</exception>
    public WaveformPreview? RequestWaveformPreviewFrom(DataReference dataReference)
    {
        ArgumentNullException.ThrowIfNull(dataReference);
        EnsureRunning();
        foreach (var cached in _previewHotCache.Values)
        {
            if (cached.DataReference.Equals(dataReference))
            {
                // Found a hot cue hit, use it.
                return cached;
            }
        }
        return RequestPreviewInternal(dataReference, null);
    }

    /// <summary>
    /// How long we are willing to wait, in nanoseconds, for a CDJ-3000 to analyze a non-rekordbox
    /// track before we give up on retrying.
    /// </summary>
    internal const long MaximumAnalysisWaitNanos = 90L * 1_000_000_000;

    /// <summary>
    /// How long we should wait, in milliseconds, after a failed/partial response during CDJ-3000
    /// analysis to try again.
    /// </summary>
    internal const long AnalysisUpdateIntervalMillis = 10_000;

    /// <summary>
    /// A single-admission gate used to ensure only one retry thread is in progress at a time for a
    /// finder, standing in for the Java <c>AtomicBoolean</c> retry flags.
    /// </summary>
    internal sealed class RetryGate
    {
        private int _flag;

        /// <summary>
        /// Attempts to claim the gate; only one claim succeeds until <see cref="Clear"/> is called.
        /// </summary>
        /// <returns><c>true</c> if the gate was claimed by this call</returns>
        public bool TrySet() => Interlocked.CompareExchange(ref _flag, 1, 0) == 0;

        /// <summary>
        /// Releases the gate, so a future retry can be scheduled.
        /// </summary>
        public void Clear() => Interlocked.Exchange(ref _flag, 0);
    }

    /// <summary>
    /// Keeps track of whether we have a retry thread already in progress, so we never have more
    /// than one.
    /// </summary>
    private readonly RetryGate _retrying = new();

    /// <summary>
    /// Helper method to check if we should retry a request because it seems the player may still
    /// be in the process of analyzing an unanalyzed track, and to produce informative log messages
    /// about the decision.
    /// </summary>
    /// <param name="fromUpdate">the metadata update that led to this attempt, or <c>null</c> if it
    /// was an explicit request via our API</param>
    /// <param name="retryFlag">the gate that is being used to record that the finder class has
    /// already scheduled a retry attempt</param>
    /// <param name="retryListener">the delegate to which the metadata update should be
    /// re-delivered when it is time to retry</param>
    /// <param name="description">the text to use in log messages describing the data we are
    /// retrying to obtain</param>
    /// <returns><c>true</c> if we have kicked off the process of waiting and trying again</returns>
    internal static bool RetryUnanalyzedTrack(TrackMetadataUpdate? fromUpdate, RetryGate retryFlag,
        Action<TrackMetadataUpdate> retryListener, string description)
    {
        if (fromUpdate?.Metadata == null || fromUpdate.Metadata.TrackType != CdjStatus.TrackType.Unanalyzed)
        {
            Logger.LogDebug("RetryUnanalyzedTrack bailing for {Description}, fromUpdate: {Update}", description, fromUpdate);
            return false;
        }

        var currentMetadata = MetadataFinder.Instance.GetLatestMetadataFor(fromUpdate.Player);

        if (currentMetadata == null || !currentMetadata.Equals(fromUpdate.Metadata))
        {
            Logger.LogInformation("Track changed while waiting for player {Player} to analyze {Description} for track {Track} in slot {Slot}, current metadata: {Metadata}, giving up.",
                fromUpdate.Player, description, fromUpdate.Metadata.TrackReference.RekordboxId,
                fromUpdate.Metadata.TrackReference.Slot, currentMetadata);
            return false;
        }

        if (Util.CurrentTimestampNanos() - fromUpdate.Metadata.Timestamp > MaximumAnalysisWaitNanos)
        {
            Logger.LogWarning("Waited too long for player {Player} to analyze {Description} for track {Track} in slot {Slot}, giving up.",
                fromUpdate.Player, description, fromUpdate.Metadata.TrackReference.RekordboxId,
                fromUpdate.Metadata.TrackReference.Slot);
            return false;
        }

        Logger.LogInformation("Did not find full {Description} data yet, still waiting for player {Player} to analyze track {Track} in slot {Slot}.",
            description, fromUpdate.Player, fromUpdate.Metadata.TrackReference.RekordboxId,
            fromUpdate.Metadata.TrackReference.Slot);
        if (retryFlag.TrySet())
        {
            var thread = new Thread(() =>
            {
                try
                {
                    Thread.Sleep((int)AnalysisUpdateIntervalMillis);
                }
                catch (ThreadInterruptedException e)
                {
                    Logger.LogInformation(e, "Interrupted while waiting to retry loading {Description} data for unanalyzed track {Metadata}",
                        description, currentMetadata);
                }
                retryFlag.Clear();
                if (currentMetadata.Equals(MetadataFinder.Instance.IsRunning ?
                        MetadataFinder.Instance.GetLatestMetadataFor(fromUpdate.Player) : null))
                {
                    try
                    {
                        Logger.LogInformation("Retrying {Description} requests for unanalyzed track in player {Player}.",
                            description, fromUpdate.Player);
                        retryListener(fromUpdate);
                    }
                    catch (Exception t)
                    {
                        Logger.LogError(t, "Problem processing retry for {Description} data of unanalyzed track {Update}",
                            description, fromUpdate);
                    }
                }
            })
            {
                Name = "Unanalyzed " + description + " data request retry",
                IsBackground = true
            };
            thread.Start();
        }
        return true;
    }

    /// <summary>
    /// Requests the waveform preview for a specific track ID, given a connection to a player that
    /// has already been set up.
    /// </summary>
    /// <param name="rekordboxId">the track whose waveform preview is desired</param>
    /// <param name="slot">identifies the media slot we are querying</param>
    /// <param name="trackType">the type of track involved (since we can request metadata for
    /// unanalyzed tracks from CDJ-3000s)</param>
    /// <param name="fromUpdate">if not <c>null</c> this is an automatic update in response to a
    /// track change, so we should only try a dbserver query if we are not in passive mode</param>
    /// <param name="client">the dbserver client that is communicating with the appropriate player</param>
    /// <returns>the retrieved waveform preview, or <c>null</c> if none was available</returns>
    /// <exception cref="IOException">if there is a communication problem</exception>
    internal WaveformPreview? GetWaveformPreview(int rekordboxId, SlotReference slot,
        CdjStatus.TrackType trackType, TrackMetadataUpdate? fromUpdate, Client client)
    {
        var idField = new NumberField(rekordboxId);

        // First try to get the NXS2-style color waveform if we are supposed to.
        if (PreferredStyle == WaveformStyle.Rgb)
        {
            try
            {
                var response = client.SimpleRequest(Message.KnownType.AnlzTagReq, Message.KnownType.AnlzTag,
                    client.BuildRmst(Message.MenuIdentifier.MainMenu, slot.Slot, trackType), idField,
                    new NumberField(Message.AnlzFileTagColorWaveformPreview), new NumberField(Message.AlnzFileTypeExt));
                if (response.RecognizedType != Message.KnownType.Unavailable && response.Arguments[3].Size > 0)
                {
                    return new WaveformPreview(new DataReference(slot, rekordboxId, trackType), response, WaveformStyle.Rgb);
                }
                if (RetryUnanalyzedTrack(fromUpdate, _retrying, RetryHandleUpdate, "waveform"))
                {
                    return null;  // We will hopefully get the data from the retry that has been queued.
                }
                Logger.LogInformation("No color waveform preview available for slot {Slot}, id {Id}; requesting blue version.",
                    slot, rekordboxId);
            }
            catch (Exception e)
            {
                if (RetryUnanalyzedTrack(fromUpdate, _retrying, RetryHandleUpdate, "waveform"))
                {
                    return null;  // We will hopefully get the data from the retry that has been queued.
                }
                Logger.LogInformation(e, "No color waveform preview available for slot {Slot}, id {Id}; requesting blue version.",
                    slot, rekordboxId);
            }
        }
        else if (PreferredStyle == WaveformStyle.ThreeBand)
        {
            // Or try to get the CDJ-3000-style 3-band waveform if we are supposed to.
            try
            {
                var response = client.SimpleRequest(Message.KnownType.AnlzTagReq, Message.KnownType.AnlzTag,
                    client.BuildRmst(Message.MenuIdentifier.MainMenu, slot.Slot, trackType), idField,
                    new NumberField(Message.AnlzFileTag3BandWaveformPreview), new NumberField(Message.AlnzFileType2Ex));
                if (response.RecognizedType != Message.KnownType.Unavailable && response.Arguments[3].Size > 0)
                {
                    return new WaveformPreview(new DataReference(slot, rekordboxId, trackType), response, WaveformStyle.ThreeBand);
                }
                if (RetryUnanalyzedTrack(fromUpdate, _retrying, RetryHandleUpdate, "waveform"))
                {
                    return null;  // We will hopefully get the data from the retry that has been queued.
                }
                Logger.LogInformation("No 3-band waveform preview available for slot {Slot}, id {Id}; requesting blue version.",
                    slot, rekordboxId);
            }
            catch (Exception e)
            {
                if (RetryUnanalyzedTrack(fromUpdate, _retrying, RetryHandleUpdate, "waveform"))
                {
                    return null;  // We will hopefully get the data from the retry that has been queued.
                }
                Logger.LogInformation(e, "No 3-band waveform preview available for slot {Slot}, id {Id}; requesting blue version.",
                    slot, rekordboxId);
            }
        }

        var blueResponse = client.SimpleRequest(Message.KnownType.WavePreviewReq, Message.KnownType.WavePreview,
            client.BuildRmst(Message.MenuIdentifier.Data, slot.Slot, trackType), NumberField.Word1,
            idField, NumberField.Word0);
        if (blueResponse.RecognizedType != Message.KnownType.Unavailable && blueResponse.Arguments[3].Size > 0)
        {
            return new WaveformPreview(new DataReference(slot, rekordboxId, trackType), blueResponse, WaveformStyle.Blue);
        }
        RetryUnanalyzedTrack(fromUpdate, _retrying, RetryHandleUpdate, "waveform");
        return null;
    }

    /// <summary>
    /// Ask the specified player for the waveform detail in the specified slot with the specified
    /// rekordbox ID, using registered metadata providers instead if possible, and possibly giving
    /// up if we are in passive mode.
    /// </summary>
    /// <param name="trackReference">uniquely identifies the desired waveform detail</param>
    /// <param name="fromUpdate">if not <c>null</c> this is an automatic update in response to a
    /// track change, so we should only try a dbserver query if we are not in passive mode</param>
    /// <returns>the waveform detail found, if any</returns>
    private WaveformDetail? RequestDetailInternal(DataReference trackReference, TrackMetadataUpdate? fromUpdate)
    {
        // First see if any registered metadata providers can offer it to us.
        var sourceDetails = MetadataFinder.Instance.GetMediaDetailsFor(trackReference.GetSlotReference());
        if (sourceDetails != null)
        {
            var provided = MetadataFinder.Instance.AllMetadataProviders.GetWaveformDetail(sourceDetails, trackReference);
            if (provided != null)
            {
                return provided;
            }
        }

        // At this point, unless we are allowed to actively request the data, we are done. We can
        // always actively request tracks from rekordbox.
        if (MetadataFinder.Instance.IsPassive && fromUpdate != null &&
            trackReference.Slot != CdjStatus.TrackSourceSlot.Collection)
        {
            return null;
        }

        // We have to actually request the detail using the dbserver protocol.
        try
        {
            return ConnectionManager.Instance.InvokeWithClientSession(trackReference.Player,
                client => GetWaveformDetail(trackReference.RekordboxId, SlotReference.GetSlotReference(trackReference),
                    trackReference.TrackType, fromUpdate, client),
                "requesting waveform detail");
        }
        catch (Exception e)
        {
            Logger.LogError(e, "Problem requesting waveform detail, returning null");
        }
        return null;
    }

    /// <summary>
    /// Ask the specified player for the specified waveform detail from the specified media slot,
    /// first checking if we have a cached copy.
    /// </summary>
    /// <param name="dataReference">uniquely identifies the desired waveform detail</param>
    /// <returns>the waveform detail, if it was found, or <c>null</c></returns>
    /// <exception cref="InvalidOperationException">if the WaveformFinder is not running</exception>
    public WaveformDetail? RequestWaveformDetailFrom(DataReference dataReference)
    {
        ArgumentNullException.ThrowIfNull(dataReference);
        EnsureRunning();
        foreach (var cached in _detailHotCache.Values)
        {
            if (cached.DataReference.Equals(dataReference))
            {
                // Found a hot cue hit, use it.
                return cached;
            }
        }
        return RequestDetailInternal(dataReference, null);
    }

    /// <summary>
    /// Requests the waveform detail for a specific track ID, given a connection to a player that
    /// has already been set up.
    /// </summary>
    /// <param name="rekordboxId">the track whose waveform detail is desired</param>
    /// <param name="slot">identifies the media slot we are querying</param>
    /// <param name="trackType">the type of track involved (since we can request metadata for
    /// unanalyzed tracks from CDJ-3000s)</param>
    /// <param name="fromUpdate">if not <c>null</c> this is an automatic update in response to a
    /// track change, so we should only try a dbserver query if we are not in passive mode</param>
    /// <param name="client">the dbserver client that is communicating with the appropriate player</param>
    /// <returns>the retrieved waveform detail, or <c>null</c> if none was available</returns>
    /// <exception cref="IOException">if there is a communication problem</exception>
    internal WaveformDetail? GetWaveformDetail(int rekordboxId, SlotReference slot,
        CdjStatus.TrackType trackType, TrackMetadataUpdate? fromUpdate, Client client)
    {
        var idField = new NumberField(rekordboxId);

        // First try to get the NXS2-style color waveform if we are supposed to.
        if (PreferredStyle == WaveformStyle.Rgb)
        {
            try
            {
                var response = client.SimpleRequest(Message.KnownType.AnlzTagReq, Message.KnownType.AnlzTag,
                    client.BuildRmst(Message.MenuIdentifier.MainMenu, slot.Slot, trackType), idField,
                    new NumberField(Message.AnlzFileTagColorWaveformDetail), new NumberField(Message.AlnzFileTypeExt));
                if (response.RecognizedType != Message.KnownType.Unavailable && response.Arguments[3].Size > 0)
                {
                    return new WaveformDetail(new DataReference(slot, rekordboxId, trackType), response, WaveformStyle.Rgb);
                }
                if (RetryUnanalyzedTrack(fromUpdate, _retrying, RetryHandleUpdate, "waveform"))
                {
                    return null;  // We will hopefully get the data from the retry that has been queued.
                }
                Logger.LogInformation("No color waveform detail available for slot {Slot}, id {Id}; requesting blue version.",
                    slot, rekordboxId);
            }
            catch (Exception e)
            {
                if (RetryUnanalyzedTrack(fromUpdate, _retrying, RetryHandleUpdate, "waveform"))
                {
                    return null;  // We will hopefully get the data from the retry that has been queued.
                }
                Logger.LogInformation(e, "Problem requesting color waveform detail for slot {Slot}, id {Id}; requesting blue version.",
                    slot, rekordboxId);
            }
        }
        else if (PreferredStyle == WaveformStyle.ThreeBand)
        {
            // Or try to get the CDJ-3000-style 3-band waveform if we are supposed to.
            try
            {
                var response = client.SimpleRequest(Message.KnownType.AnlzTagReq, Message.KnownType.AnlzTag,
                    client.BuildRmst(Message.MenuIdentifier.MainMenu, slot.Slot, trackType), idField,
                    new NumberField(Message.AnlzFileTag3BandWaveformDetail), new NumberField(Message.AlnzFileType2Ex));
                if (response.RecognizedType != Message.KnownType.Unavailable && response.Arguments[3].Size > 0)
                {
                    return new WaveformDetail(new DataReference(slot, rekordboxId, trackType), response, WaveformStyle.ThreeBand);
                }
                if (RetryUnanalyzedTrack(fromUpdate, _retrying, RetryHandleUpdate, "waveform"))
                {
                    return null;  // We will hopefully get the data from the retry that has been queued.
                }
                Logger.LogInformation("No 3-band waveform detail available for slot {Slot}, id {Id}; requesting blue version.",
                    slot, rekordboxId);
            }
            catch (Exception e)
            {
                if (RetryUnanalyzedTrack(fromUpdate, _retrying, RetryHandleUpdate, "waveform"))
                {
                    return null;  // We will hopefully get the data from the retry that has been queued.
                }
                Logger.LogInformation(e, "Problem requesting 3-band waveform detail for slot {Slot}, id {Id}; requesting blue version.",
                    slot, rekordboxId);
            }
        }

        var blueResponse = client.SimpleRequest(Message.KnownType.WaveDetailReq, Message.KnownType.WaveDetail,
            client.BuildRmst(Message.MenuIdentifier.MainMenu, slot.Slot, trackType), idField, NumberField.Word0);
        if (blueResponse.RecognizedType != Message.KnownType.Unavailable && blueResponse.Arguments[3].Size > 0)
        {
            return new WaveformDetail(new DataReference(slot, rekordboxId, trackType), blueResponse, WaveformStyle.Blue);
        }
        RetryUnanalyzedTrack(fromUpdate, _retrying, RetryHandleUpdate, "waveform");
        return null;
    }

    /// <summary>
    /// Keep track of the devices we are currently trying to get previews from in response to
    /// metadata updates.
    /// </summary>
    private readonly ConcurrentDictionary<int, byte> _activePreviewRequests = new();

    /// <summary>
    /// Keep track of the devices we are currently trying to get details from in response to
    /// metadata updates.
    /// </summary>
    private readonly ConcurrentDictionary<int, byte> _activeDetailRequests = new();

    /// <summary>
    /// Raised when the waveform preview available for a player changes. Previews are delivered in
    /// the format specified by <see cref="PreferredStyle"/>, and if that is changed, updated
    /// versions in the new preferred style are sent to the listeners. Delivered through the
    /// <see cref="EventDispatcher"/>, so a UI application can arrange to receive it on its
    /// dispatch thread.
    /// </summary>
    public event EventHandler<WaveformPreviewUpdate>? WaveformPreviewChanged;

    /// <summary>
    /// Raised when the waveform detail available for a player changes. Details are delivered in
    /// the format specified by <see cref="PreferredStyle"/>, and if that is changed, updated
    /// versions in the new preferred style are sent to the listeners. Delivered through the
    /// <see cref="EventDispatcher"/>, so a UI application can arrange to receive it on its
    /// dispatch thread.
    /// </summary>
    public event EventHandler<WaveformDetailUpdate>? WaveformDetailChanged;

    /// <summary>
    /// Send a waveform preview update announcement to all registered listeners.
    /// </summary>
    /// <param name="player">the player whose waveform preview has changed</param>
    /// <param name="preview">the new waveform preview, if any</param>
    private void DeliverWaveformPreviewUpdate(int player, WaveformPreview? preview) =>
        EventDispatcher.Deliver(WaveformPreviewChanged, this, new WaveformPreviewUpdate(player, preview), Logger);

    /// <summary>
    /// Send a waveform detail update announcement to all registered listeners.
    /// </summary>
    /// <param name="player">the player whose waveform detail has changed</param>
    /// <param name="detail">the new waveform detail, if any</param>
    private void DeliverWaveformDetailUpdate(int player, WaveformDetail? detail) =>
        EventDispatcher.Deliver(WaveformDetailChanged, this, new WaveformDetailUpdate(player, detail), Logger);

    /// <summary>
    /// Process a metadata update from the <see cref="MetadataFinder"/>, and see if it means the
    /// waveform information associated with any player has changed.
    /// </summary>
    /// <param name="update">describes the new metadata we have for a player, if any</param>
    private void HandleUpdate(TrackMetadataUpdate update)
    {
        var foundInCache = false;
        if (update.Metadata == null)
        {
            ClearDeck(update);
        }
        else
        {
            // We can offer waveform information for this device; check if we've already looked it
            // up. First, preview:
            var lastPreview = _previewHotCache.GetValueOrDefault(DeckReference.GetDeckReference(update.Player, 0));
            if (lastPreview == null || !lastPreview.DataReference.Equals(update.Metadata.TrackReference) ||
                update.Metadata.TrackType == CdjStatus.TrackType.Unanalyzed)
            {
                // We may have something new! First see if we can find the new preview in the hot cache.
                foreach (var cached in _previewHotCache)
                {
                    if (cached.Key.HotCue != 0 && cached.Value.DataReference.Equals(update.Metadata.TrackReference))
                    {
                        // Found a hot cue hit, use it.
                        UpdatePreview(update, cached.Value);
                        foundInCache = true;
                        break;
                    }
                }

                // If not found in the cache, try actually retrieving it unless that is already in
                // progress.
                if (!foundInCache && _activePreviewRequests.TryAdd(update.Player, 0))
                {
                    ClearDeckPreview(update);  // We won't know what it is until our request completes.
                    var thread = new Thread(() =>
                    {
                        try
                        {
                            var preview = RequestPreviewInternal(update.Metadata.TrackReference, update);
                            if (preview != null)
                            {
                                UpdatePreview(update, preview);
                                if (!preview.Equals(lastPreview))
                                {
                                    // The preview is still changing, so retry for more.
                                    RetryUnanalyzedTrack(update, _retrying, RetryHandleUpdate, "waveform");
                                }
                            }
                        }
                        catch (Exception e)
                        {
                            Logger.LogWarning(e, "Problem requesting waveform preview from update {Update}", update);
                        }
                        finally
                        {
                            _activePreviewRequests.TryRemove(update.Player, out _);
                        }
                    })
                    {
                        IsBackground = true
                    };
                    thread.Start();
                }
            }

            // Secondly, the detail.
            foundInCache = false;
            var lastDetail = _detailHotCache.GetValueOrDefault(DeckReference.GetDeckReference(update.Player, 0));
            if (IsFindingDetails && (lastDetail == null || !lastDetail.DataReference.Equals(update.Metadata.TrackReference) ||
                update.Metadata.TrackType == CdjStatus.TrackType.Unanalyzed))
            {
                // We may have something new! First see if we can find the new detailed waveform
                // in the hot cache.
                foreach (var cached in _detailHotCache)
                {
                    if (cached.Key.HotCue != 0 && cached.Value.DataReference.Equals(update.Metadata.TrackReference))
                    {
                        // Found a hot cue hit, use it.
                        UpdateDetail(update, cached.Value);
                        foundInCache = true;
                        break;
                    }
                }

                // If not found in the cache try actually retrieving it, unless that is already in
                // progress.
                if (!foundInCache && _activeDetailRequests.TryAdd(update.Player, 0))
                {
                    ClearDeckDetail(update);  // We won't know what it is until our request completes.
                    var thread = new Thread(() =>
                    {
                        try
                        {
                            var detail = RequestDetailInternal(update.Metadata.TrackReference, update);
                            if (detail != null)
                            {
                                UpdateDetail(update, detail);
                                if (!detail.Equals(lastDetail))
                                {
                                    // The detail is still changing, so retry for more.
                                    RetryUnanalyzedTrack(update, _retrying, RetryHandleUpdate, "waveform");
                                }
                            }
                        }
                        catch (Exception e)
                        {
                            Logger.LogWarning(e, "Problem requesting waveform detail from update {Update}", update);
                        }
                        finally
                        {
                            _activeDetailRequests.TryRemove(update.Player, out _);
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
            Logger.LogInformation("WaveformFinder stopping because {Sender} has.", sender);
            Stop();
        }
    }

    /// <summary>
    /// Send ourselves "updates" about any tracks that were loaded before we started, or before we
    /// were requesting details, since we missed them.
    /// </summary>
    private void PrimeCache()
    {
        EventDispatcher.Post(() =>
        {
            if (IsRunning)
            {
                foreach (var entry in MetadataFinder.Instance.LoadedTracks)
                {
                    if (entry.Key.HotCue == 0)
                    {
                        // The track is currently loaded in a main player deck.
                        _pendingUpdates?.Writer.TryWrite(new TrackMetadataUpdate(entry.Key.Player, entry.Value));
                    }
                }
            }
        });
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
                Logger.LogDebug(e, "Queue read interrupted, presumably due to WaveformFinder shutdown.");
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
    /// Start finding waveforms for all active players. Starts the <see cref="MetadataFinder"/> if
    /// it is not already running, because we need it to send us metadata updates to notice when
    /// new tracks are loaded. This in turn starts the <see cref="DeviceFinder"/>, so we can keep
    /// track of the comings and goings of players themselves. We also start the
    /// <see cref="ConnectionManager"/> in order to make queries to obtain waveforms.
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
    /// Stop finding waveforms for all active players.
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

            ClearAllWaveforms();
            DeliverLifecycleAnnouncement(Logger, false);
        }
    }

    /// <summary>
    /// Report the loss of all waveforms, on the proper thread, outside any lock.
    /// </summary>
    private void ClearAllWaveforms()
    {
        var dyingPreviewCache = _previewHotCache.Keys.ToArray();
        _previewHotCache.Clear();
        var dyingDetailCache = _detailHotCache.Keys.ToArray();
        _detailHotCache.Clear();
        EventDispatcher.Post(() =>
        {
            foreach (var deck in dyingPreviewCache)
            {
                // Report the loss of our previews.
                if (deck.HotCue == 0)
                {
                    EventDispatcher.DeliverNow(WaveformPreviewChanged, this,
                        new WaveformPreviewUpdate(deck.Player, null), Logger);
                }
            }
            foreach (var deck in dyingDetailCache)
            {
                // Report the loss of our details.
                if (deck.HotCue == 0)
                {
                    EventDispatcher.DeliverNow(WaveformDetailChanged, this,
                        new WaveformDetailUpdate(deck.Player, null), Logger);
                }
            }
        });
    }

    /// <summary>
    /// The single instance of this class which exists in the program.
    /// </summary>
    public static WaveformFinder Instance { get; } = new();

    /// <summary>
    /// Prevent direct instantiation.
    /// </summary>
    private WaveformFinder()
    {
    }

    /// <inheritdoc/>
    public override string ToString()
    {
        var sb = new System.Text.StringBuilder("WaveformFinder[running:").Append(IsRunning)
            .Append(", passive:").Append(MetadataFinder.Instance.IsPassive)
            .Append(", findingDetails:").Append(IsFindingDetails);
        if (IsRunning)
        {
            sb.Append(", loadedPreviews:[").Append(string.Join(", ", LoadedPreviews.Select(e => $"{e.Key}={e.Value}"))).Append(']');
            if (IsFindingDetails)
            {
                sb.Append(", loadedDetails:[").Append(string.Join(", ", LoadedDetails.Select(e => $"{e.Key}={e.Value}"))).Append(']');
            }
        }
        return sb.Append(']').ToString();
    }
}
