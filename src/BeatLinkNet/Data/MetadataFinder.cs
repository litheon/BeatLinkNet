using System.Collections.Concurrent;
using System.Threading.Channels;
using BeatLink.CrateDigger.Pdb;
using BeatLink.DbServer;
using Microsoft.Extensions.Logging;

namespace BeatLink.Data;

/// <summary>
/// <para>Watches for new tracks to be loaded on players, and queries the appropriate player for
/// the metadata information when that happens.</para>
///
/// <para>Maintains a hot cache of metadata about any track currently loaded in a player, either
/// on the main playback deck, or as a hot cue, since those tracks could start playing
/// instantly.</para>
/// </summary>
/// <remarks>
/// Ported from Deep Symmetry's beat-link <c>MetadataFinder</c>, by James Elliott.
/// </remarks>
public sealed class MetadataFinder : LifecycleParticipant
{
    private static readonly ILogger Logger = BeatLinkLogging.CreateLogger<MetadataFinder>();

    /// <summary>
    /// How long are we willing to wait to lock the database client for menu operations.
    /// </summary>
    public static readonly TimeSpan MenuTimeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Given a status update from a CDJ, find the metadata for the track that it has loaded, if
    /// any. If a registered metadata provider can offer it, that will be used, otherwise a query
    /// is made to the player's dbserver.
    /// </summary>
    /// <param name="status">the CDJ status update that will be used to determine the loaded track
    /// and ask the appropriate player for metadata about it</param>
    /// <returns>the metadata that was obtained, if any</returns>
    public TrackMetadata? RequestMetadataFrom(CdjStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        if (status.SourceSlot == CdjStatus.TrackSourceSlot.NoTrack || status.RekordboxId == 0)
        {
            return null;
        }
        var track = new DataReference(status.TrackSourcePlayer, status.SourceSlot,
            status.RekordboxId, status.LoadedTrackType);
        return RequestMetadataFrom(track);
    }

    /// <summary>
    /// Ask the specified player for metadata about the track in the specified slot with the
    /// specified rekordbox ID, unless a registered metadata provider can offer the information for
    /// the specified media slot, in which case that will be used instead.
    /// </summary>
    /// <param name="track">uniquely identifies the track whose metadata is desired</param>
    /// <returns>the metadata, if any</returns>
    public TrackMetadata? RequestMetadataFrom(DataReference track) =>
        RequestMetadataInternal(track, failIfPassive: false);

    /// <summary>
    /// Ask the specified player for metadata about the track in the specified slot with the
    /// specified rekordbox ID, using registered metadata providers instead if possible, and
    /// possibly giving up if we are in passive mode.
    /// </summary>
    /// <param name="track">uniquely identifies the track whose metadata is desired</param>
    /// <param name="failIfPassive">will prevent the request from taking place if we are in
    /// passive mode, so that automatic metadata updates will use registered providers only</param>
    /// <returns>the metadata found, if any</returns>
    private TrackMetadata? RequestMetadataInternal(DataReference track, bool failIfPassive)
    {
        ArgumentNullException.ThrowIfNull(track);

        // First see if any registered metadata providers can offer it for us.
        var sourceDetails = GetMediaDetailsFor(track.GetSlotReference());
        if (sourceDetails != null)
        {
            var provided = AllMetadataProviders.GetTrackMetadata(sourceDetails, track);
            if (provided != null)
            {
                return provided;
            }
        }

        // At this point, unless we are allowed to actively request the data, we are done. We can
        // always actively request tracks from rekordbox.
        if (IsPassive && failIfPassive && track.Slot != CdjStatus.TrackSourceSlot.Collection)
        {
            return null;
        }

        // Even if we are supposed to be able to make metadata queries, the Opus Quad does not have
        // a dbserver port. (Unlike the Java implementation, which would throw a
        // NullPointerException, we also simply return null when the player cannot be found at all,
        // since no query would be possible either way.)
        var announcement = DeviceFinder.Instance.GetLatestAnnouncementFrom(track.Player);
        if (announcement == null || announcement.IsOpusQuad)
        {
            return null;
        }

        // Use the dbserver protocol implementation to request the metadata.
        try
        {
            return ConnectionManager.Instance.InvokeWithClientSession(track.Player,
                client => QueryMetadata(track, client), "requesting metadata");
        }
        catch (Exception e)
        {
            Logger.LogError(e, "Problem requesting metadata, returning null");
        }
        return null;
    }

    /// <summary>
    /// Request metadata for a specific track ID, given a dbserver connection to a player that has
    /// already been set up. Separated into its own method so it can be used multiple times with
    /// the same connection when gathering all track metadata.
    /// </summary>
    /// <param name="track">uniquely identifies the track whose metadata is desired</param>
    /// <param name="client">the dbserver client that is communicating with the appropriate player</param>
    /// <returns>the retrieved metadata, or <c>null</c> if there is no such track</returns>
    /// <exception cref="IOException">if there is a communication problem</exception>
    /// <exception cref="TimeoutException">if we are unable to lock the client for menu operations</exception>
    internal TrackMetadata? QueryMetadata(DataReference track, Client client)
    {
        // Send the metadata menu request
        if (client.TryLockingForMenuOperations(MenuTimeout))
        {
            try
            {
                var requestType = ReferenceEquals(track.TrackType, CdjStatus.TrackType.Rekordbox) ?
                    Message.KnownType.RekordboxMetadataReq : Message.KnownType.UnanalyzedMetadataReq;
                var response = client.MenuRequestTyped(requestType, Message.MenuIdentifier.MainMenu,
                    track.Slot, track.TrackType, new NumberField(track.RekordboxId));
                var count = response.GetMenuResultsCount();
                if (count == Message.NoMenuResultsAvailable)
                {
                    return null;
                }

                // Gather the cue list and all the metadata menu items
                var items = client.RenderMenuItems(Message.MenuIdentifier.MainMenu, track.Slot, track.TrackType, response);
                var cueList = ReferenceEquals(track.TrackType, CdjStatus.TrackType.Rekordbox) ?
                    GetCueList(track.RekordboxId, track.Slot, client) : null;
                return new TrackMetadata(track, track.TrackType, items, cueList);
            }
            finally
            {
                client.UnlockForMenuOperations();
            }
        }
        throw new TimeoutException("Unable to lock the player for menu operations");
    }

    /// <summary>
    /// Requests the cue list for a specific track ID, given a dbserver connection to a player that
    /// has already been set up.
    /// </summary>
    /// <param name="rekordboxId">the track of interest</param>
    /// <param name="slot">identifies the media slot we are querying</param>
    /// <param name="client">the dbserver client that is communicating with the appropriate player</param>
    /// <returns>the retrieved cue list, or <c>null</c> if none was available</returns>
    /// <exception cref="IOException">if there is a communication problem</exception>
    internal CueList? GetCueList(int rekordboxId, CdjStatus.TrackSourceSlot slot, Client client)
    {
        // First try for an extended cue list, with colors and names, and hot cues above C.
        var response = client.SimpleRequest(Message.KnownType.CueListExtReq, null,
            client.BuildRmst(Message.MenuIdentifier.Data, slot), new NumberField(rekordboxId), NumberField.Word0);
        if (response.RecognizedType == Message.KnownType.CueListExt)
        {
            return new CueList(response);
        }
        // Fall back to an original Nexus cue list.
        response = client.SimpleRequest(Message.KnownType.CueListReq, null,
            client.BuildRmst(Message.MenuIdentifier.Data, slot), new NumberField(rekordboxId));
        if (response.RecognizedType == Message.KnownType.CueList)
        {
            return new CueList(response);
        }
        Logger.LogError("Unexpected response type when requesting cue list: {Response}", response);
        return null;
    }

    /// <summary>
    /// Request the list of all tracks in the specified slot, given a dbserver connection to a
    /// player that has already been set up.
    /// </summary>
    /// <param name="slot">identifies the media slot we are querying</param>
    /// <param name="client">the dbserver client that is communicating with the appropriate player</param>
    /// <param name="sortOrder">the order in which responses should be sorted, 0 for default</param>
    /// <returns>the retrieved track list entry items</returns>
    /// <exception cref="IOException">if there is a communication problem</exception>
    /// <exception cref="TimeoutException">if we are unable to lock the client for menu operations</exception>
    internal IReadOnlyList<Message> GetFullTrackList(CdjStatus.TrackSourceSlot slot, Client client, int sortOrder)
    {
        // Send the metadata menu request
        if (client.TryLockingForMenuOperations(MenuTimeout))
        {
            try
            {
                var response = client.MenuRequest(Message.KnownType.TrackMenuReq, Message.MenuIdentifier.MainMenu,
                    slot, new NumberField(sortOrder));
                var count = response.GetMenuResultsCount();
                if (count == Message.NoMenuResultsAvailable || count == 0)
                {
                    return [];
                }

                // Gather all the metadata menu items
                return client.RenderMenuItems(Message.MenuIdentifier.MainMenu, slot, CdjStatus.TrackType.Rekordbox, response);
            }
            finally
            {
                client.UnlockForMenuOperations();
            }
        }
        throw new TimeoutException("Unable to lock the player for menu operations");
    }

    /// <summary>
    /// Ask the connected dbserver for the playlist entries of the specified playlist (if
    /// <paramref name="folder"/> is <c>false</c>), or the list of playlists and folders inside the
    /// specified playlist folder (if <paramref name="folder"/> is <c>true</c>). Pulled into a
    /// separate method so it can be used from multiple different client transactions.
    /// </summary>
    /// <param name="slot">the slot in which the playlist can be found</param>
    /// <param name="sortOrder">the order in which responses should be sorted, 0 for default, see the
    /// <a href="https://djl-analysis.deepsymmetry.org/djl-analysis/track_metadata.html#alternate-track-sort-orders">Packet Analysis document</a> for details</param>
    /// <param name="playlistOrFolderId">the database ID of the desired playlist or folder</param>
    /// <param name="folder">indicates whether we are asking for the contents of a folder or playlist</param>
    /// <param name="client">the dbserver client that is communicating with the appropriate player</param>
    /// <returns>the items that are found in the specified playlist or folder; they will be tracks
    /// if we are asking for a playlist, or playlists and folders if we are asking for a folder</returns>
    /// <exception cref="IOException">if there is a problem communicating</exception>
    /// <exception cref="TimeoutException">if we are unable to lock the client for menu operations</exception>
    internal IReadOnlyList<Message> GetPlaylistItems(CdjStatus.TrackSourceSlot slot, int sortOrder,
        int playlistOrFolderId, bool folder, Client client)
    {
        if (client.TryLockingForMenuOperations(MenuTimeout))
        {
            try
            {
                var response = client.MenuRequest(Message.KnownType.PlaylistReq, Message.MenuIdentifier.MainMenu, slot,
                    new NumberField(sortOrder), new NumberField(playlistOrFolderId), new NumberField(folder ? 1 : 0));
                var count = response.GetMenuResultsCount();
                if (count == Message.NoMenuResultsAvailable || count == 0)
                {
                    return [];
                }

                // Gather all the metadata menu items
                return client.RenderMenuItems(Message.MenuIdentifier.MainMenu, slot, CdjStatus.TrackType.Rekordbox, response);
            }
            finally
            {
                client.UnlockForMenuOperations();
            }
        }
        throw new TimeoutException("Unable to lock player for menu operations.");
    }

    /// <summary>
    /// Ask the specified player's dbserver for the playlist entries of the specified playlist (if
    /// <paramref name="folder"/> is <c>false</c>), or the list of playlists and folders inside the
    /// specified playlist folder (if <paramref name="folder"/> is <c>true</c>).
    /// </summary>
    /// <param name="player">the player number whose playlist entries are of interest</param>
    /// <param name="slot">the slot in which the playlist can be found</param>
    /// <param name="sortOrder">the order in which responses should be sorted, 0 for default, see the
    /// <a href="https://djl-analysis.deepsymmetry.org/djl-analysis/track_metadata.html#alternate-track-sort-orders">Packet Analysis document</a> for details</param>
    /// <param name="playlistOrFolderId">the database ID of the desired playlist or folder</param>
    /// <param name="folder">indicates whether we are asking for the contents of a folder or playlist</param>
    /// <returns>the items that are found in the specified playlist or folder; they will be tracks
    /// if we are asking for a playlist, or playlists and folders if we are asking for a folder</returns>
    /// <exception cref="IOException">if there is a problem obtaining the playlist information</exception>
    /// <exception cref="TimeoutException">if we are unable to lock the client for menu operations</exception>
    /// <exception cref="InvalidOperationException">if the connection manager is not running or the
    /// player cannot be located</exception>
    public IReadOnlyList<Message> RequestPlaylistItemsFrom(int player, CdjStatus.TrackSourceSlot slot,
        int sortOrder, int playlistOrFolderId, bool folder) =>
        ConnectionManager.Instance.InvokeWithClientSession(player,
            client => GetPlaylistItems(slot, sortOrder, playlistOrFolderId, folder, client),
            "requesting playlist information");

    /// <summary>
    /// Keeps track of the current metadata cached for each player. We cache metadata for any track
    /// which is currently on-deck in the player, as well as any that were loaded into a player's
    /// hot-cue slot.
    /// </summary>
    private readonly ConcurrentDictionary<DeckReference, TrackMetadata> _hotCache = new();

    /// <summary>
    /// A queue used to hold CDJ status updates we receive from the <see cref="VirtualCdj"/> so we
    /// can process them on a lower priority thread, and not hold up delivery to more time-sensitive
    /// listeners. Present only while we are running.
    /// </summary>
    private volatile Channel<CdjStatus>? _pendingUpdates;

    /// <summary>
    /// Our update listener just puts appropriate device updates on our queue, so we can process
    /// them on a lower priority thread, and not hold up delivery to more time-sensitive listeners.
    /// </summary>
    private void OnDeviceUpdateReceived(object? sender, DeviceUpdate update)
    {
        Logger.LogDebug("Received device update {Update}", update);
        if (update is CdjStatus status)
        {
            _pendingUpdates?.Writer.TryWrite(status);
        }
    }

    /// <summary>
    /// Our device-found handler records the arrival of rekordbox collections as rekordbox comes
    /// and goes.
    /// </summary>
    private void OnDeviceFound(object? sender, DeviceAnnouncement announcement) => HandleDeviceFound(announcement);

    /// <summary>
    /// Reacts to a device appearing on the network (whether from a live announcement or the replay
    /// of already-known devices during startup), recording the arrival of rekordbox collections.
    /// </summary>
    /// <param name="announcement">the device announcement that was received</param>
    private void HandleDeviceFound(DeviceAnnouncement announcement)
    {
        if (announcement.DeviceNumber == 25 && announcement.DeviceName == "NXS-GW")
        {
            Logger.LogDebug("Ignoring arrival of Kuvo gateway, which fight each other and come and go constantly, especially in CDJ-3000s.");
            return;
        }
        Logger.LogInformation("Processing device found, number:{Number}, name:\"{Name}\".",
            announcement.DeviceNumber, announcement.DeviceName);
        if ((((announcement.DeviceNumber > 0x0f) && announcement.DeviceNumber < 0x20) || announcement.DeviceNumber > 40) &&
            announcement.DeviceName.StartsWith("rekordbox", StringComparison.Ordinal))
        {
            // Looks like rekordbox.
            Logger.LogInformation("Recording rekordbox collection mount.");
            RecordMount(SlotReference.GetSlotReference(announcement.DeviceNumber,
                CdjStatus.TrackSourceSlot.Collection));  // Report the rekordbox collection as mounted media.
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
        ClearMetadata(announcement);
        if (announcement.DeviceNumber < 0x10)
        {
            // Looks like a player, clear the whole panoply of caches.
            RemoveMount(SlotReference.GetSlotReference(announcement.DeviceNumber, CdjStatus.TrackSourceSlot.CdSlot));
            RemoveMount(SlotReference.GetSlotReference(announcement.DeviceNumber, CdjStatus.TrackSourceSlot.UsbSlot));
            RemoveMount(SlotReference.GetSlotReference(announcement.DeviceNumber, CdjStatus.TrackSourceSlot.SdSlot));
        }
        else if ((((announcement.DeviceNumber > 0x0f) && announcement.DeviceNumber < 0x20) || announcement.DeviceNumber > 40) &&
            announcement.DeviceName.StartsWith("rekordbox", StringComparison.Ordinal))
        {
            // Looks like rekordbox, clear "mounted" database.
            RemoveMount(SlotReference.GetSlotReference(announcement.DeviceNumber, CdjStatus.TrackSourceSlot.Collection));
        }
    }

    /// <summary>
    /// Keep track of whether we are running.
    /// </summary>
    private volatile bool _running;

    /// <summary>
    /// Check whether we are currently running. Unless we are in passive mode, we will also
    /// automatically request metadata from the appropriate player when a new track is loaded that
    /// is not found in the hot cache.
    /// </summary>
    /// <seealso cref="IsPassive"/>
    public override bool IsRunning => _running;

    /// <summary>
    /// Indicates whether we should use metadata only from registered providers, never actively
    /// requesting it from a player. Note that this will implicitly mean all the metadata-related
    /// finders (<see cref="ArtFinder"/>, <see cref="BeatGridFinder"/>, and
    /// <see cref="WaveformFinder"/>) are in passive mode as well, because their activity is
    /// triggered by the availability of new track metadata.
    /// </summary>
    private volatile bool _passive;

    /// <inheritdoc cref="_passive"/>
    public bool IsPassive
    {
        get => _passive;
        set => _passive = value;
    }

    /// <summary>
    /// The task processing our queued player status updates, so as not to slow down the
    /// high-priority update delivery thread; we perform potentially slow I/O.
    /// </summary>
    private Task? _queueHandler;

    /// <summary>
    /// Serializes lifecycle transitions, standing in for the Java <c>synchronized</c> start and
    /// stop methods.
    /// </summary>
    private readonly object _lifecycleLock = new();

    /// <summary>
    /// We have received an update that invalidates any previous metadata for that player, so clear
    /// it out, and alert any listeners if this represents a change. This does not affect the hot
    /// cues; they will stick around until the player loads a new track that overwrites one or more
    /// of them.
    /// </summary>
    /// <param name="update">the update which means we can have no metadata for the associated player</param>
    private void ClearDeck(CdjStatus update)
    {
        if (_hotCache.TryRemove(DeckReference.GetDeckReference(update.DeviceNumber, 0), out _))
        {
            DeliverTrackMetadataUpdate(update.DeviceNumber, null);
        }
    }

    /// <summary>
    /// We have received notification that a device is no longer on the network, so clear out its
    /// metadata.
    /// </summary>
    /// <param name="announcement">the packet which reported the device's disappearance</param>
    private void ClearMetadata(DeviceAnnouncement announcement)
    {
        var player = announcement.DeviceNumber;
        // Iterate over a copy to avoid concurrent modification issues
        foreach (var deck in _hotCache.Keys.ToArray())
        {
            if (deck.Player == player && _hotCache.TryRemove(deck, out _) && deck.HotCue == 0)
            {
                DeliverTrackMetadataUpdate(player, null);  // Inform listeners the metadata is gone.
            }
        }
    }

    /// <summary>
    /// We have obtained metadata for a device, so store it and alert any listeners.
    /// </summary>
    /// <param name="update">the update which caused us to retrieve this metadata</param>
    /// <param name="data">the metadata which we received</param>
    private void UpdateMetadata(CdjStatus update, TrackMetadata data)
    {
        _hotCache[DeckReference.GetDeckReference(update.DeviceNumber, 0)] = data;  // Main deck
        if (data.CueList != null)
        {
            // Update the cache with any hot cues in this track as well
            foreach (var entry in data.CueList.Entries)
            {
                if (entry.HotCueNumber != 0)
                {
                    _hotCache[DeckReference.GetDeckReference(update.DeviceNumber, entry.HotCueNumber)] = data;
                }
            }
        }
        DeliverTrackMetadataUpdate(update.DeviceNumber, data);
    }

    /// <summary>
    /// Get the metadata of all tracks currently loaded in any player, either on the play deck, or
    /// in a hot cue.
    /// </summary>
    /// <exception cref="InvalidOperationException">if the MetadataFinder is not running</exception>
    public IReadOnlyDictionary<DeckReference, TrackMetadata> LoadedTracks
    {
        get
        {
            EnsureRunning();
            // Make a copy so callers get an immutable snapshot of the current state.
            return new Dictionary<DeckReference, TrackMetadata>(_hotCache);
        }
    }

    /// <summary>
    /// Look up the track metadata we have for the track loaded in the main deck of a given player
    /// number.
    /// </summary>
    /// <param name="player">the device number whose track metadata for the playing track is desired</param>
    /// <returns>information about the track loaded on that player, if available</returns>
    /// <exception cref="InvalidOperationException">if the MetadataFinder is not running</exception>
    public TrackMetadata? GetLatestMetadataFor(int player)
    {
        EnsureRunning();
        return _hotCache.GetValueOrDefault(DeckReference.GetDeckReference(player, 0));
    }

    /// <summary>
    /// Look up the track metadata we have for a given player, identified by a status update
    /// received from that player.
    /// </summary>
    /// <param name="update">a status update from the player for which track metadata is desired</param>
    /// <returns>information about the track loaded on that player, if available</returns>
    /// <exception cref="InvalidOperationException">if the MetadataFinder is not running</exception>
    public TrackMetadata? GetLatestMetadataFor(DeviceUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        return GetLatestMetadataFor(update.DeviceNumber);
    }

    /// <summary>
    /// Keep track of the devices we are currently trying to get metadata from in response to
    /// status updates.
    /// </summary>
    private readonly ConcurrentDictionary<int, byte> _activeRequests = new();

    /// <summary>
    /// Discards any tracks from the hot cache that were loaded from a now-unmounted media slot,
    /// because they are no longer valid.
    /// </summary>
    /// <param name="slot">the media slot that has been unmounted</param>
    private void FlushHotCacheSlot(SlotReference slot)
    {
        // Iterate over a copy to avoid concurrent modification issues
        foreach (var entry in _hotCache.ToArray())
        {
            if (ReferenceEquals(slot, SlotReference.GetSlotReference(entry.Value.TrackReference)))
            {
                Logger.LogDebug("Evicting cached metadata in response to unmount report {Metadata}", entry.Value);
                _hotCache.TryRemove(entry.Key, out _);
            }
        }
    }

    /// <summary>
    /// Keeps track of any players with mounted media.
    /// </summary>
    private readonly ConcurrentDictionary<SlotReference, byte> _mediaMounts = new();

    /// <summary>
    /// Records that there is media mounted in a particular media player slot, updating listeners
    /// if this is a change. Also send a query to the player requesting details about the media
    /// mounted in that slot, if we don't already have that information.
    /// </summary>
    /// <param name="slot">the slot in which media is mounted</param>
    private void RecordMount(SlotReference slot)
    {
        if (_mediaMounts.TryAdd(slot, 0))
        {
            DeliverMountUpdate(slot, true);
        }
        if (!_mediaDetails.ContainsKey(slot) && !VirtualCdj.Instance.InOpusQuadCompatibilityMode)
        {
            try
            {
                VirtualCdj.Instance.SendMediaQuery(slot);
            }
            catch (Exception e)
            {
                Logger.LogWarning(e, "Problem trying to request media details for {Slot}", slot);
            }
        }
        // Ask a running OpusProvider to deliver any media details queued for this player when a
        // metadata archive was attached or detached, now that the application is ready for them.
        if (OpusProvider.Instance.IsRunning)
        {
            OpusProvider.Instance.PollAndSendMediaDetails(slot.Player);
        }
    }

    /// <summary>
    /// Records that there is no media mounted in a particular media player slot, updating
    /// listeners if this is a change, and clearing any affected items from our in-memory caches.
    /// </summary>
    /// <param name="slot">the slot in which no media is mounted</param>
    private void RemoveMount(SlotReference slot)
    {
        _mediaDetails.TryRemove(slot, out _);
        if (_mediaMounts.TryRemove(slot, out _))
        {
            DeliverMountUpdate(slot, false);
        }
    }

    /// <summary>
    /// Returns the set of media slots on the network that currently have media mounted in them.
    /// Note that computers running rekordbox are included; their collections are valid media
    /// databases to explore with the <see cref="MenuLoader"/>, and they are valid as
    /// <see cref="SlotReference"/> arguments to tell players to load tracks from.
    /// </summary>
    public IReadOnlySet<SlotReference> MountedMediaSlots =>
        // Make a copy so callers get an immutable snapshot of the current state.
        new HashSet<SlotReference>(_mediaMounts.Keys);

    /// <summary>
    /// Keeps track of the media details the Virtual CDJ has been able to find for us about the
    /// mounted slots.
    /// </summary>
    private readonly ConcurrentDictionary<SlotReference, MediaDetails> _mediaDetails = new();

    /// <summary>
    /// Get the details we know about all mounted media.
    /// </summary>
    public IReadOnlyCollection<MediaDetails> MountedMediaDetails => _mediaDetails.Values.ToArray();

    /// <summary>
    /// Look up the details we know about the media mounted in a particular slot.
    /// </summary>
    /// <param name="slot">the slot whose media is of interest</param>
    /// <returns>the details, or <c>null</c> if we don't have any</returns>
    public MediaDetails? GetMediaDetailsFor(SlotReference slot)
    {
        ArgumentNullException.ThrowIfNull(slot);
        return _mediaDetails.GetValueOrDefault(slot);
    }

    /// <summary>
    /// Raised when media is mounted in a player's media slot. Note that at the time a mount is
    /// detected, we will not yet know any details about the mounted media; subscribe to
    /// <see cref="MediaDetailsAvailable"/> to learn them as soon as the mounting player reports
    /// them. Delivered through the <see cref="EventDispatcher"/>, so a UI application can arrange
    /// to receive it on its dispatch thread.
    /// </summary>
    public event EventHandler<SlotReference>? MediaMounted;

    /// <summary>
    /// Raised when media is unmounted from a player's media slot. Delivered through the
    /// <see cref="EventDispatcher"/>, so a UI application can arrange to receive it on its
    /// dispatch thread.
    /// </summary>
    public event EventHandler<SlotReference>? MediaUnmounted;

    /// <summary>
    /// Raised when the details of media mounted in a player's media slot have been reported by the
    /// player, shortly after the corresponding <see cref="MediaMounted"/> event. Delivered through
    /// the <see cref="EventDispatcher"/>. (In the Java implementation this took the form of mount
    /// listeners which also implemented <c>MediaDetailsListener</c>.)
    /// </summary>
    public event EventHandler<MediaDetails>? MediaDetailsAvailable;

    /// <summary>
    /// Send a mount update announcement to all registered listeners.
    /// </summary>
    /// <param name="slot">the slot in which media has been mounted or unmounted</param>
    /// <param name="mounted">will be <c>true</c> if there is now media mounted in the specified slot</param>
    private void DeliverMountUpdate(SlotReference slot, bool mounted)
    {
        if (mounted)
        {
            Logger.LogInformation("Reporting media mounted in {Slot}", slot);
        }
        else
        {
            Logger.LogInformation("Reporting media removed from {Slot}", slot);
        }
        EventDispatcher.Deliver(mounted ? MediaMounted : MediaUnmounted, this, slot, Logger);
    }

    /// <summary>
    /// Raised when the track metadata associated with a player changes. Delivered through the
    /// <see cref="EventDispatcher"/>, so a UI application can arrange to receive it on its
    /// dispatch thread.
    /// </summary>
    public event EventHandler<TrackMetadataUpdate>? TrackMetadataChanged;

    /// <summary>
    /// Send a track metadata update announcement to all registered listeners.
    /// </summary>
    /// <param name="player">the player whose track metadata has changed</param>
    /// <param name="metadata">the new metadata, if any</param>
    private void DeliverTrackMetadataUpdate(int player, TrackMetadata? metadata) =>
        EventDispatcher.Deliver(TrackMetadataChanged, this, new TrackMetadataUpdate(player, metadata), Logger);

    /// <summary>
    /// Holds the providers of metadata that client applications register. The keys are the
    /// <see cref="MediaDetails.HashKey"/> values of the media that they reported providing
    /// metadata for. Providers that offer metadata for all media are stored with a key of an
    /// empty string.
    /// </summary>
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<IMetadataProvider, byte>> _metadataProviders = new();

    /// <summary>
    /// Adds a metadata provider that will be consulted to see if it can provide metadata for
    /// newly-loaded tracks before we try to retrieve it from the players. This function will
    /// immediately call <see cref="IMetadataProvider.SupportedMedia"/> and will only consult the
    /// provider for tracks loaded from the media mentioned in the response. The function is only
    /// called once, when initially adding the provider, so if the set of supported media changes,
    /// you will need to remove the provider and re-add it. Providers that can provide metadata for
    /// all media can simply return an empty list of supported media, and they will always be
    /// consulted. Only do this if they truly can always provide metadata, or you will slow down
    /// the lookup process by having them frequently called in vain.
    /// </summary>
    /// <param name="provider">the object that can supply metadata about tracks</param>
    public void AddMetadataProvider(IMetadataProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        var supportedMedia = provider.SupportedMedia();
        if (supportedMedia == null || supportedMedia.Count == 0)
        {
            AddMetadataProviderForMedia("", provider);
        }
        else
        {
            foreach (var details in supportedMedia)
            {
                AddMetadataProviderForMedia(details.HashKey(), provider);
            }
        }
    }

    /// <summary>
    /// Internal method that adds a metadata provider to the set associated with a particular hash
    /// key, creating the set if needed.
    /// </summary>
    /// <param name="key">the hash key identifying the media for which this provider can offer
    /// metadata (or the empty string if it can offer metadata for all media)</param>
    /// <param name="provider">the metadata provider to be added to the active set</param>
    private void AddMetadataProviderForMedia(string key, IMetadataProvider provider)
    {
        var providers = _metadataProviders.GetOrAdd(key, static _ => new ConcurrentDictionary<IMetadataProvider, byte>());
        providers.TryAdd(provider, 0);
    }

    /// <summary>
    /// Removes a metadata provider, so it will no longer be consulted to provide metadata for
    /// tracks loaded from any media.
    /// </summary>
    /// <param name="provider">the metadata provider to remove</param>
    public void RemoveMetadataProvider(IMetadataProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        foreach (var providers in _metadataProviders.Values)
        {
            providers.TryRemove(provider, out _);
        }
    }

    /// <summary>
    /// Get the set of metadata providers that can offer metadata for tracks loaded from the
    /// specified media.
    /// </summary>
    /// <param name="sourceMedia">the media whose metadata providers are desired, or <c>null</c> to
    /// get the set of metadata providers that can offer metadata for all media</param>
    /// <returns>any registered metadata providers that reported themselves as supporting tracks
    /// from that media</returns>
    public IReadOnlySet<IMetadataProvider> GetMetadataProviders(MediaDetails? sourceMedia)
    {
        var key = sourceMedia == null ? "" : sourceMedia.HashKey();
        if (!_metadataProviders.TryGetValue(key, out var result))
        {
            return new HashSet<IMetadataProvider>();
        }
        return new HashSet<IMetadataProvider>(result.Keys);
    }

    /// <summary>
    /// A composite metadata provider that runs all registered metadata providers that are
    /// appropriate for a particular source media, returning the first non-<c>null</c> result from
    /// any of them. This can be used wherever metadata is needed before moving on to other methods.
    /// </summary>
    internal IMetadataProvider AllMetadataProviders { get; }

    /// <summary>
    /// Runs a query against all providers registered for the specified media, then all wildcard
    /// providers, returning the first non-<c>null</c> result.
    /// </summary>
    /// <typeparam name="T">the type of metadata being sought</typeparam>
    /// <param name="sourceMedia">the media from which the associated track was loaded</param>
    /// <param name="query">extracts the desired metadata from one provider</param>
    /// <returns>the first result offered by any appropriate provider, or <c>null</c></returns>
    private T? FirstFromProviders<T>(MediaDetails sourceMedia, Func<IMetadataProvider, T?> query) where T : class
    {
        foreach (var provider in GetMetadataProviders(sourceMedia))
        {
            var result = query(provider);
            if (result != null)
            {
                return result;
            }
        }
        foreach (var provider in GetMetadataProviders(null))
        {
            var result = query(provider);
            if (result != null)
            {
                return result;
            }
        }
        return null;
    }

    /// <summary>
    /// The implementation behind <see cref="AllMetadataProviders"/>: consults every provider
    /// registered for the source media, and then every wildcard provider, returning the first
    /// non-<c>null</c> answer for each kind of metadata.
    /// </summary>
    /// <param name="owner">the finder whose provider registry should be consulted</param>
    private sealed class CompositeMetadataProvider(MetadataFinder owner) : IMetadataProvider
    {
        /// <inheritdoc/>
        public IReadOnlyList<MediaDetails> SupportedMedia() => [];  // We support all media, not that anyone will call this.

        /// <inheritdoc/>
        public TrackMetadata? GetTrackMetadata(MediaDetails sourceMedia, DataReference track) =>
            owner.FirstFromProviders(sourceMedia, p => p.GetTrackMetadata(sourceMedia, track));

        /// <inheritdoc/>
        public AlbumArt? GetAlbumArt(MediaDetails sourceMedia, DataReference art) =>
            owner.FirstFromProviders(sourceMedia, p => p.GetAlbumArt(sourceMedia, art));

        /// <inheritdoc/>
        public BeatGrid? GetBeatGrid(MediaDetails sourceMedia, DataReference track) =>
            owner.FirstFromProviders(sourceMedia, p => p.GetBeatGrid(sourceMedia, track));

        /// <inheritdoc/>
        public CueList? GetCueList(MediaDetails sourceMedia, DataReference track) =>
            owner.FirstFromProviders(sourceMedia, p => p.GetCueList(sourceMedia, track));

        /// <inheritdoc/>
        public WaveformPreview? GetWaveformPreview(MediaDetails sourceMedia, DataReference track) =>
            owner.FirstFromProviders(sourceMedia, p => p.GetWaveformPreview(sourceMedia, track));

        /// <inheritdoc/>
        public WaveformDetail? GetWaveformDetail(MediaDetails sourceMedia, DataReference track) =>
            owner.FirstFromProviders(sourceMedia, p => p.GetWaveformDetail(sourceMedia, track));

        /// <inheritdoc/>
        public RekordboxAnlz.TaggedSection? GetAnalysisSection(MediaDetails sourceMedia, DataReference track,
            string fileExtension, string typeTag) =>
            owner.FirstFromProviders(sourceMedia, p => p.GetAnalysisSection(sourceMedia, track, fileExtension, typeTag));
    }

    /// <summary>
    /// <para>Process an update packet from one of the CDJs. See if it has a valid track loaded; if
    /// not, clear any metadata we had stored for that player. If so, see if it is the same track
    /// we already know about; if not, request the metadata associated with that track.</para>
    ///
    /// <para>Also updates the sets of which players have media mounted in which slots.</para>
    ///
    /// <para>If any of these reflect a change in state, any registered listeners will be
    /// informed.</para>
    /// </summary>
    /// <param name="update">an update packet we received from a CDJ</param>
    private void HandleUpdate(CdjStatus update)
    {
        // First see if any mount sets need updating.
        if (update.IsLocalUsbEmpty)
        {
            var slot = SlotReference.GetSlotReference(update.DeviceNumber, CdjStatus.TrackSourceSlot.UsbSlot);
            FlushHotCacheSlot(slot);
            RemoveMount(slot);
        }
        else if (update.IsLocalUsbLoaded)
        {
            RecordMount(SlotReference.GetSlotReference(update.DeviceNumber, CdjStatus.TrackSourceSlot.UsbSlot));
        }

        if (update.IsLocalSdEmpty)
        {
            var slot = SlotReference.GetSlotReference(update.DeviceNumber, CdjStatus.TrackSourceSlot.SdSlot);
            FlushHotCacheSlot(slot);
            RemoveMount(slot);
        }
        else if (update.IsLocalSdLoaded)
        {
            RecordMount(SlotReference.GetSlotReference(update.DeviceNumber, CdjStatus.TrackSourceSlot.SdSlot));
        }

        if (update.IsDiscSlotEmpty)
        {
            RemoveMount(SlotReference.GetSlotReference(update.DeviceNumber, CdjStatus.TrackSourceSlot.CdSlot));
        }
        else
        {
            RecordMount(SlotReference.GetSlotReference(update.DeviceNumber, CdjStatus.TrackSourceSlot.CdSlot));
        }

        // Now see if a track has changed that needs new metadata.
        if (update.LoadedTrackType == CdjStatus.TrackType.Unknown ||
            update.LoadedTrackType == CdjStatus.TrackType.NoTrack ||
            update.SourceSlot == CdjStatus.TrackSourceSlot.NoTrack ||
            update.SourceSlot == CdjStatus.TrackSourceSlot.Unknown ||
            update.RekordboxId == 0)
        {
            // We no longer have metadata for this device.
            ClearDeck(update);
        }
        else
        {
            // We can offer metadata for this device; check if we already looked up this track.
            var lastMetadata = _hotCache.GetValueOrDefault(DeckReference.GetDeckReference(update.DeviceNumber, 0));
            var trackReference = new DataReference(update.TrackSourcePlayer,
                update.SourceSlot, update.RekordboxId, update.LoadedTrackType);
            if (lastMetadata == null || !lastMetadata.TrackReference.Equals(trackReference))
            {
                // We have something new! First see if we can find the new track in the hot cache
                // as a hot cue.
                foreach (var cached in _hotCache.Values)
                {
                    if (cached.TrackReference.Equals(trackReference))
                    {
                        // Found a hot cue hit, use it.
                        UpdateMetadata(update, cached);
                        return;
                    }
                }

                // Not in the hot cache so try actually retrieving it, if possible.
                if (ConnectionManager.Instance.GetPlayerDBServerPort(update.TrackSourcePlayer) > 0 ||
                    VirtualCdj.Instance.InOpusQuadCompatibilityMode)
                {
                    if (_activeRequests.TryAdd(update.TrackSourcePlayer, 0))
                    {
                        // We had to make sure we were not already asking for this track.
                        ClearDeck(update);  // We won't know what it is until our request completes.
                        var thread = new Thread(() =>
                        {
                            try
                            {
                                var data = RequestMetadataInternal(trackReference, failIfPassive: true);
                                if (data != null)
                                {
                                    UpdateMetadata(update, data);
                                }
                            }
                            catch (Exception e)
                            {
                                Logger.LogWarning(e, "Problem requesting track metadata from update {Update}", update);
                            }
                            finally
                            {
                                _activeRequests.TryRemove(update.TrackSourcePlayer, out _);
                            }
                        })
                        {
                            Name = "MetadataFinder metadata request",
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
    /// Allows us to automatically shut down when the VirtualCdj or ConnectionManager, which we
    /// depend on, does.
    /// </summary>
    private void OnDependencyStopped(object? sender, EventArgs e)
    {
        if (IsRunning)
        {
            Logger.LogInformation("MetadataFinder stopping because {Sender} has.", sender);
            Stop();
        }
    }

    /// <summary>
    /// The body of the queue-handling task: processes queued status updates until the channel is
    /// completed (by <see cref="Stop"/>) or we are no longer running.
    /// </summary>
    /// <param name="reader">supplies the queued status updates</param>
    private void RunQueueHandler(ChannelReader<CdjStatus> reader)
    {
        while (IsRunning)
        {
            CdjStatus update;
            try
            {
                if (!reader.TryRead(out update!))
                {
                    if (!reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
                    {
                        break;  // The channel was completed, presumably due to MetadataFinder shutdown.
                    }
                    continue;
                }
            }
            catch (Exception e)
            {
                Logger.LogDebug(e, "Queue read interrupted, presumably due to MetadataFinder shutdown.");
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
                Logger.LogError(e, "Problem handling CDJ status update.");
            }
        }
    }

    /// <summary>
    /// Start finding track metadata for all active players. Starts the <see cref="VirtualCdj"/>
    /// if it is not already running, because we need it to send us device status updates to notice
    /// when new tracks are loaded; this starts the <see cref="DeviceFinder"/> (which is also
    /// needed by the <see cref="VirtualCdj"/>) so we can keep track of the comings and goings of
    /// players themselves. We start the <see cref="ConnectionManager"/> in order to make queries
    /// to obtain metadata.
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
                VirtualCdj.Instance.Stopped += OnDependencyStopped;
                DeviceFinder.Instance.DeviceFound += OnDeviceFound;
                DeviceFinder.Instance.DeviceLost += OnDeviceLost;
                _dependencyListenersRegistered = true;
            }
            ConnectionManager.Instance.Start();
            DeviceFinder.Instance.Start();
            VirtualCdj.Instance.Start();
            VirtualCdj.Instance.DeviceUpdateReceived += OnDeviceUpdateReceived;
            var channel = Channel.CreateBounded<CdjStatus>(new BoundedChannelOptions(100)
            {
                FullMode = BoundedChannelFullMode.DropWrite,
                SingleReader = true
            });
            _pendingUpdates = channel;
            _running = true;
            _queueHandler = Task.Factory.StartNew(() => RunQueueHandler(channel.Reader), CancellationToken.None,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);
            DeliverLifecycleAnnouncement(Logger, true);

            // If there are already any rekordbox instances on the network, "mount" their collections.
            foreach (var existingDevice in DeviceFinder.Instance.CurrentDevices)
            {
                HandleDeviceFound(existingDevice);
            }
        }
    }

    /// <summary>
    /// Stop finding track metadata for all active players.
    /// </summary>
    public void Stop()
    {
        lock (_lifecycleLock)
        {
            if (!IsRunning)
            {
                return;
            }
            VirtualCdj.Instance.DeviceUpdateReceived -= OnDeviceUpdateReceived;
            _running = false;
            _pendingUpdates?.Writer.TryComplete();
            _pendingUpdates = null;
            _queueHandler = null;

            // Report the loss of our hot cached metadata on the proper thread, outside our lock.
            var dyingCache = _hotCache.Keys.ToArray();
            EventDispatcher.Post(() =>
            {
                foreach (var deck in dyingCache)
                {
                    if (deck.HotCue == 0)
                    {
                        EventDispatcher.DeliverNow(TrackMetadataChanged, this,
                            new TrackMetadataUpdate(deck.Player, null), Logger);
                    }
                }
            });
            _hotCache.Clear();
            DeliverLifecycleAnnouncement(Logger, false);
        }
    }

    /// <summary>
    /// Test seam: discards all recorded media mounts and details (which, as in the Java
    /// implementation, otherwise survive a stop/start cycle), so tests remain independent of each
    /// other.
    /// </summary>
    internal void ClearMountsForTests()
    {
        _mediaMounts.Clear();
        _mediaDetails.Clear();
    }

    /// <summary>
    /// The single instance of this class which exists in the program.
    /// </summary>
    public static MetadataFinder Instance { get; } = new();

    /// <summary>
    /// Prevent direct instantiation, and arrange for us to hear about the responses to any media
    /// details requests we ask the Virtual CDJ to make for us, passing them on to
    /// <see cref="MediaDetailsAvailable"/> subscribers.
    /// </summary>
    private MetadataFinder()
    {
        AllMetadataProviders = new CompositeMetadataProvider(this);
        VirtualCdj.Instance.MediaDetailsReceived += (sender, details) =>
        {
            _mediaDetails[details.SlotReference] = details;
            if (!_mediaMounts.ContainsKey(details.SlotReference))
            {
                // We do this after the fact in case the slot was being unmounted at the same time
                // as we were responding to the event, so we end up in the correct final state.
                Logger.LogWarning("Discarding media details reported for an unmounted media slot: {Details}", details);
                _mediaDetails.TryRemove(details.SlotReference, out _);
            }
            else
            {
                EventDispatcher.Deliver(MediaDetailsAvailable, this, details, Logger);
            }
        };
    }

    /// <inheritdoc/>
    public override string ToString()
    {
        var sb = new System.Text.StringBuilder("MetadataFinder[running:").Append(IsRunning)
            .Append(", passive:").Append(IsPassive);
        if (IsRunning)
        {
            sb.Append(", loadedTracks:[").Append(string.Join(", ", LoadedTracks.Select(e => $"{e.Key}={e.Value}")))
                .Append("], mountedMediaSlots:[").Append(string.Join(", ", MountedMediaSlots))
                .Append("], mountedMediaDetails:[").Append(string.Join(", ", MountedMediaDetails)).Append(']');
        }
        return sb.Append(']').ToString();
    }
}
