using System.Collections.Concurrent;
using System.Threading.Channels;
using BeatLink.DbServer;
using Microsoft.Extensions.Logging;

namespace BeatLink.Data;

/// <summary>
/// <para>Watches for new metadata to become available for tracks loaded on players, and queries
/// the appropriate player for the track beat grid when that happens.</para>
///
/// <para>Maintains a hot cache of beat grids for any track currently loaded in a player, either on
/// the main playback deck, or as a hot cue, since those tracks could start playing instantly.</para>
///
/// <para>Implicitly honors the active/passive setting of the <see cref="MetadataFinder"/> (see
/// <see cref="MetadataFinder.IsPassive"/>), because beat grids are loaded in response to metadata
/// updates.</para>
/// </summary>
/// <remarks>
/// Ported from Deep Symmetry's beat-link <c>BeatGridFinder</c>, by James Elliott.
/// </remarks>
public sealed class BeatGridFinder : LifecycleParticipant
{
    private static readonly ILogger Logger = BeatLinkLogging.CreateLogger<BeatGridFinder>();

    /// <summary>
    /// Keeps track of the current beat grids cached for each player. We hot cache beat grids for
    /// any track which is currently on-deck in the player, as well as any that were loaded into a
    /// player's hot-cue slot.
    /// </summary>
    private readonly ConcurrentDictionary<DeckReference, BeatGrid> _hotCache = new();

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
    /// Our mount listener evicts any cached beat grids that belong to media databases which have
    /// been unmounted, since they are no longer valid.
    /// </summary>
    private void OnMediaUnmounted(object? sender, SlotReference slot)
    {
        // Iterate over a copy to avoid concurrent modification issues.
        foreach (var entry in _hotCache.ToArray())
        {
            if (ReferenceEquals(slot, SlotReference.GetSlotReference(entry.Value.DataReference)))
            {
                Logger.LogDebug("Evicting cached beat grid in response to unmount report {Grid}", entry.Value);
                _hotCache.TryRemove(entry.Key, out _);
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
            Logger.LogDebug("Ignoring noise from Kuvo gateways, especially in CDJ-3000s, which fight each other and come and go constantly.");
            return;
        }
        Logger.LogInformation("Clearing beat grids in response to the loss of a device, {Announcement}", announcement);
        ClearBeatGrids(announcement);
    }

    /// <summary>
    /// Keep track of whether we are running.
    /// </summary>
    private volatile bool _running;

    /// <summary>
    /// Check whether we are currently running. Unless the <see cref="MetadataFinder"/> is in
    /// passive mode, we will automatically request beat grids from the appropriate player when a
    /// new track is loaded that is not found in the hot cache.
    /// </summary>
    /// <seealso cref="MetadataFinder.IsPassive"/>
    public override bool IsRunning => _running;

    /// <summary>
    /// The task processing our queued metadata updates on a separate thread so as not to slow
    /// down the high-priority update delivery thread; we perform potentially slow I/O.
    /// </summary>
    private Task? _queueHandler;

    /// <summary>
    /// We have received an update that invalidates the beat grid for a player, so clear it and
    /// alert any listeners if this represents a change. This does not affect the hot cues; they
    /// will stick around until the player loads a new track that overwrites one or more of them.
    /// </summary>
    /// <param name="update">the update which means we have no beat grid for the associated player</param>
    private void ClearDeck(TrackMetadataUpdate update)
    {
        if (_hotCache.TryRemove(DeckReference.GetDeckReference(update.Player, 0), out _))
        {
            DeliverBeatGridUpdate(update.Player, null);
        }
    }

    /// <summary>
    /// We have received notification that a device is no longer on the network, so clear out all
    /// its beat grids.
    /// </summary>
    /// <param name="announcement">the packet which reported the device's disappearance</param>
    private void ClearBeatGrids(DeviceAnnouncement announcement)
    {
        var player = announcement.DeviceNumber;
        // Iterate over a copy to avoid concurrent modification issues.
        foreach (var deck in _hotCache.Keys.ToArray())
        {
            if (deck.Player == player && _hotCache.TryRemove(deck, out _) && deck.HotCue == 0)
            {
                DeliverBeatGridUpdate(player, null);  // Inform listeners the beat grid is gone.
            }
        }
    }

    /// <summary>
    /// We have obtained a beat grid for a device, so store it and alert any listeners.
    /// </summary>
    /// <param name="update">the update which caused us to retrieve this beat grid</param>
    /// <param name="beatGrid">the beat grid which we retrieved</param>
    private void UpdateBeatGrid(TrackMetadataUpdate update, BeatGrid beatGrid)
    {
        _hotCache[DeckReference.GetDeckReference(update.Player, 0)] = beatGrid;  // Main deck
        if (update.Metadata?.CueList != null)
        {
            // Update the cache with any hot cues in this track as well.
            foreach (var entry in update.Metadata.CueList.Entries)
            {
                if (entry.HotCueNumber != 0)
                {
                    _hotCache[DeckReference.GetDeckReference(update.Player, entry.HotCueNumber)] = beatGrid;
                }
            }
        }
        DeliverBeatGridUpdate(update.Player, beatGrid);
    }

    /// <summary>
    /// Get the beat grids available for all tracks currently loaded in any player, either on the
    /// play deck, or in a hot cue.
    /// </summary>
    /// <exception cref="InvalidOperationException">if the BeatGridFinder is not running</exception>
    public IReadOnlyDictionary<DeckReference, BeatGrid> LoadedBeatGrids
    {
        get
        {
            EnsureRunning();
            // Make a copy so callers get an immutable snapshot of the current state.
            return new Dictionary<DeckReference, BeatGrid>(_hotCache);
        }
    }

    /// <summary>
    /// Look up the beat grid we have for the track loaded in the main deck of a given player
    /// number.
    /// </summary>
    /// <param name="player">the device number whose beat grid for the playing track is desired</param>
    /// <returns>the beat grid for the track loaded on that player, if available</returns>
    /// <exception cref="InvalidOperationException">if the BeatGridFinder is not running</exception>
    public BeatGrid? GetLatestBeatGridFor(int player)
    {
        EnsureRunning();
        return _hotCache.GetValueOrDefault(DeckReference.GetDeckReference(player, 0));
    }

    /// <summary>
    /// Look up the beat grid we have for a given player, identified by a status update received
    /// from that player.
    /// </summary>
    /// <param name="update">a status update from the player for which a beat grid is desired</param>
    /// <returns>the beat grid for the track loaded on that player, if available</returns>
    /// <exception cref="InvalidOperationException">if the BeatGridFinder is not running</exception>
    public BeatGrid? GetLatestBeatGridFor(DeviceUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        var result = GetLatestBeatGridFor(update.DeviceNumber);
        if (result != null && update is CdjStatus status &&
            result.DataReference.RekordboxId != status.RekordboxId)
        {
            return null;
        }
        return result;
    }

    /// <summary>
    /// Ask the specified player for the beat grid in the specified slot with the specified
    /// rekordbox ID, using registered metadata providers instead if possible, and possibly giving
    /// up if we are in passive mode.
    /// </summary>
    /// <param name="trackReference">uniquely identifies the desired beat grid</param>
    /// <param name="fromUpdate">if not <c>null</c> this is an automatic update in response to a
    /// track change, so we should only try a dbserver query if we are not in passive mode</param>
    /// <returns>the beat grid found, if any</returns>
    private BeatGrid? RequestBeatGridInternal(DataReference trackReference, TrackMetadataUpdate? fromUpdate)
    {
        // First see if any registered metadata providers can offer it to us.
        var sourceDetails = MetadataFinder.Instance.GetMediaDetailsFor(trackReference.GetSlotReference());
        if (sourceDetails != null)
        {
            var provided = MetadataFinder.Instance.AllMetadataProviders.GetBeatGrid(sourceDetails, trackReference);
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

        // We have to actually request the beat grid using the dbserver protocol.
        try
        {
            return ConnectionManager.Instance.InvokeWithClientSession(trackReference.Player,
                client => GetBeatGrid(trackReference.RekordboxId, SlotReference.GetSlotReference(trackReference),
                    trackReference.TrackType, fromUpdate, client),
                "requesting beat grid");
        }
        catch (Exception e)
        {
            Logger.LogError(e, "Problem requesting beat grid, returning null");
        }
        return null;
    }

    /// <summary>
    /// Ask the specified player for the beat grid of the track in the specified slot with the
    /// specified rekordbox ID, first checking if we have a cached copy we can use instead.
    /// </summary>
    /// <param name="track">uniquely identifies the track whose beat grid is desired</param>
    /// <returns>the beat grid, if any</returns>
    public BeatGrid? RequestBeatGridFrom(DataReference track)
    {
        ArgumentNullException.ThrowIfNull(track);
        foreach (var cached in _hotCache.Values)
        {
            if (cached.DataReference.Equals(track))
            {
                // Found a hot cue hit, use it.
                return cached;
            }
        }
        return RequestBeatGridInternal(track, null);
    }

    /// <summary>
    /// Keeps track of whether we have a retry thread already in progress, so we never have more
    /// than one.
    /// </summary>
    private readonly WaveformFinder.RetryGate _retrying = new();

    /// <summary>
    /// Requests the beat grid for a specific track ID, given a connection to a player that has
    /// already been set up.
    /// </summary>
    /// <param name="rekordboxId">the track of interest</param>
    /// <param name="slot">identifies the media slot we are querying</param>
    /// <param name="trackType">the type of track involved (since we can request metadata for
    /// unanalyzed tracks from CDJ-3000s)</param>
    /// <param name="fromUpdate">if not <c>null</c> this is an automatic update in response to a
    /// track change, so we should only try a dbserver query if we are not in passive mode</param>
    /// <param name="client">the dbserver client that is communicating with the appropriate player</param>
    /// <returns>the retrieved beat grid, or <c>null</c> if there was none available</returns>
    /// <exception cref="IOException">if there is a communication problem</exception>
    internal BeatGrid? GetBeatGrid(int rekordboxId, SlotReference slot, CdjStatus.TrackType trackType,
        TrackMetadataUpdate? fromUpdate, Client client)
    {
        var response = client.SimpleRequest(Message.KnownType.BeatGridReq, null,
            client.BuildRmst(Message.MenuIdentifier.Data, slot.Slot, trackType), new NumberField(rekordboxId));
        if (response.RecognizedType == Message.KnownType.BeatGrid)
        {
            return new BeatGrid(new DataReference(slot, rekordboxId, trackType), response);
        }
        if (WaveformFinder.RetryUnanalyzedTrack(fromUpdate, _retrying, RetryHandleUpdate, "beat grid"))
        {
            return null;  // We will hopefully get the data from the retry that has been queued.
        }
        Logger.LogError("Unexpected response type when requesting beat grid: {Response}", response);
        return null;
    }

    /// <summary>
    /// Re-delivers a metadata update to our queue when it is time to retry a request for a track
    /// that was still being analyzed.
    /// </summary>
    /// <param name="update">the metadata update to re-process</param>
    private void RetryHandleUpdate(TrackMetadataUpdate update) => _pendingUpdates?.Writer.TryWrite(update);

    /// <summary>
    /// Keep track of the devices we are currently trying to get beat grids from in response to
    /// metadata updates.
    /// </summary>
    private readonly ConcurrentDictionary<int, byte> _activeRequests = new();

    /// <summary>
    /// Raised when the beat grid available for a player changes. Delivered through the
    /// <see cref="EventDispatcher"/>, so a UI application can arrange to receive it on its
    /// dispatch thread.
    /// </summary>
    public event EventHandler<BeatGridUpdate>? BeatGridChanged;

    /// <summary>
    /// Send a beat grid update announcement to all registered listeners.
    /// </summary>
    /// <param name="player">the player whose beat grid information has changed</param>
    /// <param name="beatGrid">the new beat grid associated with that player, if any</param>
    private void DeliverBeatGridUpdate(int player, BeatGrid? beatGrid) =>
        EventDispatcher.Deliver(BeatGridChanged, this, new BeatGridUpdate(player, beatGrid), Logger);

    /// <summary>
    /// Process a metadata update from the <see cref="MetadataFinder"/>, and see if it means the
    /// beat grid information associated with any player has changed.
    /// </summary>
    /// <param name="update">describes the new metadata we have for a player, if any</param>
    private void HandleUpdate(TrackMetadataUpdate update)
    {
        if (update.Metadata == null)
        {
            ClearDeck(update);
        }
        else
        {
            // We can offer beat grid information for this device; check if we've already looked
            // it up.
            var lastBeatGrid = _hotCache.GetValueOrDefault(DeckReference.GetDeckReference(update.Player, 0));
            if (lastBeatGrid == null || !lastBeatGrid.DataReference.Equals(update.Metadata.TrackReference))
            {
                // We have something new! First see if we can find the new grid in the hot cache.
                foreach (var cached in _hotCache.Values)
                {
                    if (cached.DataReference.Equals(update.Metadata.TrackReference))
                    {
                        // Found a hot cue hit, use it.
                        UpdateBeatGrid(update, cached);
                        return;
                    }
                }

                // Not in the cache so try actually retrieving it.
                if (_activeRequests.TryAdd(update.Player, 0))
                {
                    // We had to make sure we were not already asking for this track.
                    ClearDeck(update);  // We won't know what it is until our request completes.
                    var thread = new Thread(() =>
                    {
                        try
                        {
                            var grid = RequestBeatGridInternal(update.Metadata.TrackReference, update);
                            if (grid != null && grid.BeatCount > 0)
                            {
                                UpdateBeatGrid(update, grid);
                            }
                            else
                            {
                                WaveformFinder.RetryUnanalyzedTrack(update, _retrying, RetryHandleUpdate, "beat grid");
                            }
                        }
                        catch (Exception e)
                        {
                            Logger.LogWarning(e, "Problem requesting beat grid from update {Update}", update);
                            WaveformFinder.RetryUnanalyzedTrack(update, _retrying, RetryHandleUpdate, "beat grid");
                        }
                        finally
                        {
                            _activeRequests.TryRemove(update.Player, out _);
                        }
                    })
                    {
                        Name = "Beat Grid request",
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
            Logger.LogInformation("BeatGridFinder stopping because {Sender} has.", sender);
            Stop();
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
                Logger.LogDebug(e, "Queue read interrupted, presumably due to BeatGridFinder shutdown.");
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
    /// Start finding beat grids for all active players. Starts the <see cref="MetadataFinder"/>
    /// if it is not already running, because we need it to send us metadata updates to notice when
    /// new tracks are loaded. This in turn starts the <see cref="DeviceFinder"/>, so we can keep
    /// track of the comings and goings of players themselves. We also start the
    /// <see cref="ConnectionManager"/> in order to make queries to obtain beat grids.
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

            // Send ourselves "updates" about any tracks that were loaded before we started, since
            // we missed those.
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
    /// Stop finding beat grids for all active players.
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

            // Report the loss of our beat grids, on the proper thread, and outside our lock.
            var dyingCache = _hotCache.Keys.ToArray();
            EventDispatcher.Post(() =>
            {
                foreach (var deck in dyingCache)
                {
                    if (deck.HotCue == 0)
                    {
                        EventDispatcher.DeliverNow(BeatGridChanged, this,
                            new BeatGridUpdate(deck.Player, null), Logger);
                    }
                }
            });
            _hotCache.Clear();
            DeliverLifecycleAnnouncement(Logger, false);
        }
    }

    /// <summary>
    /// Test seam: places a beat grid directly in the hot cache for the main deck of the specified
    /// player, so interpolation logic (like the <see cref="TimeFinder"/>'s) can be exercised
    /// without a dbserver conversation.
    /// </summary>
    /// <param name="player">the player whose deck should appear to have the beat grid loaded</param>
    /// <param name="grid">the beat grid to inject</param>
    internal void InjectBeatGridForTests(int player, BeatGrid grid) =>
        _hotCache[DeckReference.GetDeckReference(player, 0)] = grid;

    /// <summary>
    /// The single instance of this class which exists in the program.
    /// </summary>
    public static BeatGridFinder Instance { get; } = new();

    /// <summary>
    /// Prevent direct instantiation.
    /// </summary>
    private BeatGridFinder()
    {
    }

    /// <inheritdoc/>
    public override string ToString()
    {
        var sb = new System.Text.StringBuilder("BeatGridFinder[running:").Append(IsRunning)
            .Append(", passive:").Append(MetadataFinder.Instance.IsPassive);
        if (IsRunning)
        {
            sb.Append(", loadedBeatGrids:[").Append(string.Join(", ", LoadedBeatGrids.Select(e => $"{e.Key}={e.Value}"))).Append(']');
        }
        return sb.Append(']').ToString();
    }
}
