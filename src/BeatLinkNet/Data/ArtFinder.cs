using System.Collections.Concurrent;
using System.Threading.Channels;
using BeatLink.DbServer;
using Microsoft.Extensions.Logging;

namespace BeatLink.Data;

/// <summary>
/// <para>Watches for new metadata to become available for tracks loaded on players, and queries
/// the appropriate player for the album art when that happens.</para>
///
/// <para>Maintains a hot cache of art for any track currently loaded in a player, either on the
/// main playback deck, or as a hot cue, since those tracks could start playing instantly. Also
/// maintains a second-level in-memory cache of artwork, discarding the least-recently-used art
/// when the cache fills, because tracks can share artwork, so the DJ may load another track with
/// the same album art.</para>
///
/// <para>Implicitly honors the active/passive setting of the <see cref="MetadataFinder"/> (see
/// <see cref="MetadataFinder.IsPassive"/>), because art is loaded in response to metadata
/// updates.</para>
/// </summary>
/// <remarks>
/// Ported from Deep Symmetry's beat-link <c>ArtFinder</c>, by James Elliott.
/// </remarks>
public sealed class ArtFinder : LifecycleParticipant
{
    private static readonly ILogger Logger = BeatLinkLogging.CreateLogger<ArtFinder>();

    /// <summary>
    /// Keeps track of the current album art cached for each player. We hot cache art for any track
    /// which is currently on-deck in the player, as well as any that were loaded into a player's
    /// hot-cue slot.
    /// </summary>
    private readonly ConcurrentDictionary<DeckReference, AlbumArt> _hotCache = new();

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
    /// Guards the second-level art cache and its clock-eviction bookkeeping, standing in for the
    /// Java <c>synchronized</c> methods (which locked the finder instance).
    /// </summary>
    private readonly object _cacheLock = new();

    /// <summary>
    /// Removes the specified art from our second-level cache if it was present there.
    /// </summary>
    /// <param name="artReference">identifies the album art which should no longer be cached</param>
    private void RemoveArtFromCache(DataReference artReference)
    {
        lock (_cacheLock)
        {
            _artCache.TryRemove(artReference, out _);
            _artCacheRecentlyUsed.Remove(artReference);
            _artCacheEvictionQueue.Remove(artReference);
        }
    }

    /// <summary>
    /// Our mount listener evicts any cached artwork that belongs to media databases which have
    /// been unmounted, since it is no longer valid.
    /// </summary>
    private void OnMediaUnmounted(object? sender, SlotReference slot)
    {
        // Iterate over a copy to avoid concurrent modification issues.
        foreach (var artReference in _artCache.Keys.ToArray())
        {
            if (ReferenceEquals(SlotReference.GetSlotReference(artReference), slot))
            {
                Logger.LogDebug("Evicting cached artwork in response to unmount report {ArtReference}", artReference);
                RemoveArtFromCache(artReference);
            }
        }
        // Again iterate over a copy to avoid concurrent modification issues.
        foreach (var entry in _hotCache.ToArray())
        {
            if (ReferenceEquals(slot, SlotReference.GetSlotReference(entry.Value.ArtReference)))
            {
                Logger.LogDebug("Evicting hot cached artwork in response to unmount report {Art}", entry.Value);
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
        Logger.LogInformation("Clearing artwork in response to the loss of a device, {Announcement}", announcement);
        ClearArt(announcement);
    }

    /// <summary>
    /// Keep track of whether we are running.
    /// </summary>
    private volatile bool _running;

    /// <summary>
    /// Check whether we are currently running. Unless the <see cref="MetadataFinder"/> is in
    /// passive mode, we will automatically request album art from the appropriate player when a
    /// new track is loaded that is not found in the hot cache or the second-level memory cache.
    /// </summary>
    /// <seealso cref="MetadataFinder.IsPassive"/>
    public override bool IsRunning => _running;

    /// <summary>
    /// The task processing our queued metadata updates on a separate thread so as not to slow down
    /// the high-priority update delivery thread; we perform potentially slow I/O.
    /// </summary>
    private Task? _queueHandler;

    /// <summary>
    /// We have received an update that invalidates any previous metadata for a player, so clear
    /// its art, and alert any listeners if this represents a change. This does not affect the hot
    /// cues; they will stick around until the player loads a new track that overwrites one or more
    /// of them.
    /// </summary>
    /// <param name="update">the update which means we have no metadata for the associated player</param>
    private void ClearDeck(TrackMetadataUpdate update)
    {
        if (_hotCache.TryRemove(DeckReference.GetDeckReference(update.Player, 0), out _))
        {
            DeliverAlbumArtUpdate(update.Player, null);
        }
    }

    /// <summary>
    /// We have received notification that a device is no longer on the network, so clear out its
    /// artwork.
    /// </summary>
    /// <param name="announcement">the packet which reported the device's disappearance</param>
    private void ClearArt(DeviceAnnouncement announcement)
    {
        var player = announcement.DeviceNumber;
        // Iterate over a copy to avoid concurrent modification issues.
        foreach (var deck in _hotCache.Keys.ToArray())
        {
            if (deck.Player == player && _hotCache.TryRemove(deck, out _) && deck.HotCue == 0)
            {
                DeliverAlbumArtUpdate(player, null);  // Inform listeners that the artwork is gone.
            }
        }
        // Again iterate over a copy to avoid concurrent modification issues.
        foreach (var art in _artCache.Keys.ToArray())
        {
            if (art.Player == player)
            {
                RemoveArtFromCache(art);
            }
        }
    }

    /// <summary>
    /// We have obtained album art for a device, so store it and alert any listeners.
    /// </summary>
    /// <param name="update">the update which caused us to retrieve this art</param>
    /// <param name="art">the album art which we retrieved</param>
    private void UpdateArt(TrackMetadataUpdate update, AlbumArt art)
    {
        _hotCache[DeckReference.GetDeckReference(update.Player, 0)] = art;  // Main deck
        if (update.Metadata?.CueList != null)
        {
            // Update the cache with any hot cues in this track as well.
            foreach (var entry in update.Metadata.CueList.Entries)
            {
                if (entry.HotCueNumber != 0)
                {
                    _hotCache[DeckReference.GetDeckReference(update.Player, entry.HotCueNumber)] = art;
                }
            }
        }
        DeliverAlbumArtUpdate(update.Player, art);
    }

    /// <summary>
    /// Get the art available for all tracks currently loaded in any player, either on the play
    /// deck, or in a hot cue.
    /// </summary>
    /// <exception cref="InvalidOperationException">if the ArtFinder is not running</exception>
    public IReadOnlyDictionary<DeckReference, AlbumArt> LoadedArt
    {
        get
        {
            EnsureRunning();
            // Make a copy so callers get an immutable snapshot of the current state.
            return new Dictionary<DeckReference, AlbumArt>(_hotCache);
        }
    }

    /// <summary>
    /// Look up the album art we have for the track loaded in the main deck of a given player
    /// number.
    /// </summary>
    /// <param name="player">the device number whose album art for the playing track is desired</param>
    /// <returns>the album art for the track loaded on that player, if available</returns>
    /// <exception cref="InvalidOperationException">if the ArtFinder is not running</exception>
    public AlbumArt? GetLatestArtFor(int player)
    {
        EnsureRunning();
        return _hotCache.GetValueOrDefault(DeckReference.GetDeckReference(player, 0));
    }

    /// <summary>
    /// Look up the album art we have for a given player, identified by a status update received
    /// from that player.
    /// </summary>
    /// <param name="update">a status update from the player for which album art is desired</param>
    /// <returns>the album art for the track loaded on that player, if available</returns>
    /// <exception cref="InvalidOperationException">if the ArtFinder is not running</exception>
    public AlbumArt? GetLatestArtFor(DeviceUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        return GetLatestArtFor(update.DeviceNumber);
    }

    /// <summary>
    /// The default maximum number of artwork images we will retain in our second-level cache.
    /// </summary>
    public const int DefaultArtCacheSize = 100;

    /// <summary>
    /// Establish the second-level artwork cache. Since multiple tracks share the same art, it can
    /// be worthwhile to keep art around even for tracks that are not currently loaded, to save on
    /// having to request it again when another track from the same album is loaded. This, along
    /// with <see cref="_artCacheEvictionQueue"/> and <see cref="_artCacheRecentlyUsed"/>, provides
    /// a simple implementation of the "clock" or "second-chance" variant on a least-recently-used
    /// cache. (Thanks to Ben Manes for suggesting this approach to the Java implementation.)
    /// </summary>
    private readonly ConcurrentDictionary<DataReference, AlbumArt> _artCache = new();

    /// <summary>
    /// Keeps track of the order in which art has been added to the cache, so older and unused art
    /// is prioritized for eviction. Guarded by <see cref="_cacheLock"/>.
    /// </summary>
    private readonly LinkedList<DataReference> _artCacheEvictionQueue = new();

    /// <summary>
    /// Keeps track of whether artwork has been used since it was added to the cache or previously
    /// considered for eviction, so older and unused art is prioritized for eviction. Guarded by
    /// <see cref="_cacheLock"/>.
    /// </summary>
    private readonly HashSet<DataReference> _artCacheRecentlyUsed = [];

    /// <summary>
    /// Establishes how many album art images we retain in our second-level cache.
    /// </summary>
    private volatile int _artCacheSize = DefaultArtCacheSize;

    /// <summary>
    /// How many album art images can be kept in the in-memory second-level cache. If you set this
    /// to a smaller number than are currently present in the cache, some of the older images will
    /// be immediately discarded so that only the number you specified remain.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">if the value is set to less than 1</exception>
    public int ArtCacheSize
    {
        get => _artCacheSize;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            lock (_cacheLock)
            {
                _artCacheSize = value;
                while (_artCache.Count > value)
                {
                    EvictFromArtCache();
                }
            }
        }
    }

    /// <summary>
    /// Removes an element from the second-level artwork cache. Looks for the first item in the
    /// eviction queue that has not been used since it was created or considered for eviction and
    /// removes that. Any items which are found to have been used are instead moved to the end of
    /// the queue and marked unused. Must be called while holding <see cref="_cacheLock"/>.
    /// </summary>
    private void EvictFromArtCache()
    {
        var evicted = false;
        while (!evicted && _artCacheEvictionQueue.Count > 0)
        {
            var candidate = _artCacheEvictionQueue.First!.Value;
            _artCacheEvictionQueue.RemoveFirst();
            if (_artCacheRecentlyUsed.Remove(candidate))
            {
                // This artwork has been used, give it a second chance.
                _artCacheEvictionQueue.AddLast(candidate);
            }
            else
            {
                // This candidate is ready to be evicted.
                _artCache.TryRemove(candidate, out _);
                evicted = true;
            }
        }
    }

    /// <summary>
    /// Controls whether we attempt to obtain high-resolution art when it is available, falling
    /// back to normal resolution when it is not.
    /// </summary>
    private volatile bool _requestHighResolutionArt = true;

    /// <inheritdoc cref="_requestHighResolutionArt"/>
    public bool RequestHighResolutionArt
    {
        get => _requestHighResolutionArt;
        set => _requestHighResolutionArt = value;
    }

    /// <summary>
    /// Adds artwork to our second-level cache, evicting older unused art if necessary to enforce
    /// the size limit.
    /// </summary>
    /// <param name="artReference">identifies the album art being cached</param>
    /// <param name="art">the art to cache</param>
    private void AddArtToCache(DataReference artReference, AlbumArt art)
    {
        lock (_cacheLock)
        {
            while (_artCache.Count >= _artCacheSize)
            {
                EvictFromArtCache();
            }
            _artCache[artReference] = art;
            _artCacheEvictionQueue.AddLast(artReference);
        }
    }

    /// <summary>
    /// Ask the specified player for the album art in the specified slot with the specified
    /// rekordbox ID, using registered metadata providers instead if possible, and possibly giving
    /// up if we are in passive mode.
    /// </summary>
    /// <param name="artReference">uniquely identifies the desired album art</param>
    /// <param name="failIfPassive">will prevent the request from taking place if we are in passive
    /// mode, so that automatic artwork updates will use available caches and registered providers
    /// only</param>
    /// <returns>the album art found, if any</returns>
    private AlbumArt? RequestArtworkInternal(DataReference artReference, bool failIfPassive)
    {
        // First see if any registered metadata providers can offer it for us.
        var sourceDetails = MetadataFinder.Instance.GetMediaDetailsFor(artReference.GetSlotReference());
        if (sourceDetails != null)
        {
            var provided = MetadataFinder.Instance.AllMetadataProviders.GetAlbumArt(sourceDetails, artReference);
            if (provided != null)
            {
                AddArtToCache(artReference, provided);
                return provided;
            }
        }

        // At this point, unless we are allowed to actively request the data, we are done. We can
        // always actively request tracks from rekordbox.
        if (MetadataFinder.Instance.IsPassive && failIfPassive &&
            artReference.Slot != CdjStatus.TrackSourceSlot.Collection)
        {
            return null;
        }

        // We have to actually request the art using the dbserver protocol.
        try
        {
            var artwork = ConnectionManager.Instance.InvokeWithClientSession(artReference.Player,
                client => GetArtwork(artReference.RekordboxId, SlotReference.GetSlotReference(artReference),
                    artReference.TrackType, client),
                "requesting artwork");
            if (artwork != null)
            {
                // Our network request succeeded, so add to the level 2 cache.
                AddArtToCache(artReference, artwork);
            }
            return artwork;
        }
        catch (Exception e)
        {
            Logger.LogError(e, "Problem requesting album art, returning null");
        }
        return null;
    }

    /// <summary>
    /// Ask the specified player for the specified artwork from the specified media slot, first
    /// checking if we have a cached copy.
    /// </summary>
    /// <param name="artReference">uniquely identifies the desired artwork</param>
    /// <returns>the artwork, if it was found, or <c>null</c></returns>
    /// <exception cref="InvalidOperationException">if the ArtFinder is not running</exception>
    public AlbumArt? RequestArtworkFrom(DataReference artReference)
    {
        ArgumentNullException.ThrowIfNull(artReference);
        EnsureRunning();
        var artwork = FindArtInMemoryCaches(artReference);  // First check the in-memory artwork caches.
        return artwork ?? RequestArtworkInternal(artReference, failIfPassive: false);
    }

    /// <summary>
    /// Request the artwork with a particular artwork ID, given a connection to a player that has
    /// already been set up. Will request high resolution art unless
    /// <see cref="RequestHighResolutionArt"/> has been set to <c>false</c>.
    /// </summary>
    /// <param name="artworkId">identifies the album art to retrieve</param>
    /// <param name="slot">the slot identifier from which the associated track was loaded</param>
    /// <param name="trackType">the kind of track that owns the artwork</param>
    /// <param name="client">the dbserver client that is communicating with the appropriate player</param>
    /// <returns>the track's artwork, or <c>null</c> if none is available</returns>
    /// <exception cref="IOException">if there is a problem communicating with the player</exception>
    internal AlbumArt GetArtwork(int artworkId, SlotReference slot, CdjStatus.TrackType trackType, Client client)
    {
        Message response;

        // Send the artwork request
        if (RequestHighResolutionArt)
        {
            response = client.SimpleRequest(Message.KnownType.AlbumArtReq, Message.KnownType.AlbumArt,
                client.BuildRmst(Message.MenuIdentifier.Data, slot.Slot, trackType), new NumberField(artworkId),
                new NumberField(1));
        }
        else
        {
            response = client.SimpleRequest(Message.KnownType.AlbumArtReq, Message.KnownType.AlbumArt,
                client.BuildRmst(Message.MenuIdentifier.Data, slot.Slot, trackType), new NumberField(artworkId));
        }

        // Create an image from the response bytes
        return new AlbumArt(new DataReference(slot, artworkId, trackType), ((BinaryField)response.Arguments[3]).Value);
    }

    /// <summary>
    /// Keep track of the devices we are currently trying to get artwork from in response to
    /// metadata updates.
    /// </summary>
    private readonly ConcurrentDictionary<int, byte> _activeRequests = new();

    /// <summary>
    /// Look for the specified album art in both the hot cache of loaded tracks and the longer-lived
    /// second-chance cache.
    /// </summary>
    /// <param name="artReference">uniquely identifies the desired album art</param>
    /// <returns>the art, if it was found in one of our caches, or <c>null</c></returns>
    private AlbumArt? FindArtInMemoryCaches(DataReference artReference)
    {
        // First see if we can find the new track in the hot cache as a hot cue.
        foreach (var cached in _hotCache.Values)
        {
            if (cached.ArtReference.Equals(artReference))
            {
                // Found a hot cue hit, use it.
                return cached;
            }
        }

        // Not in the hot cache, see if it is in our second-chance cache.
        lock (_cacheLock)
        {
            if (_artCache.TryGetValue(artReference, out var found))
            {
                _artCacheRecentlyUsed.Add(artReference);
                return found;
            }
            return null;
        }
    }

    /// <summary>
    /// Raised when the album art associated with a player changes. Delivered through the
    /// <see cref="EventDispatcher"/>, so a UI application can arrange to receive it on its
    /// dispatch thread.
    /// </summary>
    public event EventHandler<AlbumArtUpdate>? AlbumArtChanged;

    /// <summary>
    /// Send an album art update announcement to all registered listeners.
    /// </summary>
    /// <param name="player">the player whose album art has changed</param>
    /// <param name="art">the new album art, if any</param>
    private void DeliverAlbumArtUpdate(int player, AlbumArt? art) =>
        EventDispatcher.Deliver(AlbumArtChanged, this, new AlbumArtUpdate(player, art), Logger);

    /// <summary>
    /// Process a metadata update from the <see cref="MetadataFinder"/>, and see if it means the
    /// album art associated with any player has changed.
    /// </summary>
    /// <param name="update">describes the new metadata we have for a player, if any</param>
    private void HandleUpdate(TrackMetadataUpdate update)
    {
        if (update.Metadata == null || update.Metadata.ArtworkId == 0)
        {
            // Either we have no metadata, or the track has no album art.
            ClearDeck(update);
        }
        else
        {
            // We can offer artwork for this device; check if we have already looked up this art.
            var lastArt = _hotCache.GetValueOrDefault(DeckReference.GetDeckReference(update.Player, 0));
            var artReference = new DataReference(update.Metadata.TrackReference.Player,
                update.Metadata.TrackReference.Slot, update.Metadata.ArtworkId, update.Metadata.TrackType);
            if (lastArt == null || !lastArt.ArtReference.Equals(artReference))
            {
                // We have something new! First see if we can find the new track in one of our
                // in-memory caches.
                var cached = FindArtInMemoryCaches(artReference);
                if (cached != null)
                {
                    // Found a cache hit, use it.
                    UpdateArt(update, cached);
                    return;
                }

                // Not in either cache so try actually retrieving it.
                if (_activeRequests.TryAdd(update.Player, 0))
                {
                    ClearDeck(update);  // We won't know what it is until our request completes.
                    // We had to make sure we were not already asking for this track.
                    var thread = new Thread(() =>
                    {
                        try
                        {
                            var art = RequestArtworkInternal(artReference, failIfPassive: true);
                            if (art != null)
                            {
                                UpdateArt(update, art);
                            }
                        }
                        catch (Exception e)
                        {
                            Logger.LogWarning(e, "Problem requesting album art from update {Update}", update);
                        }
                        finally
                        {
                            _activeRequests.TryRemove(update.Player, out _);
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
            Logger.LogInformation("ArtFinder stopping because {Sender} has.", sender);
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
                Logger.LogDebug(e, "Queue read interrupted, presumably due to ArtFinder shutdown.");
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
    /// Start finding album art for all active players. Starts the <see cref="MetadataFinder"/> if
    /// it is not already running, because we need it to send us metadata updates to notice when
    /// new tracks are loaded. This in turn starts the <see cref="DeviceFinder"/>, so we can keep
    /// track of the comings and goings of players themselves. We also start the
    /// <see cref="ConnectionManager"/> in order to make queries to obtain art.
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
            foreach (var entry in MetadataFinder.Instance.LoadedTracks)
            {
                if (entry.Key.HotCue == 0)
                {
                    // The track is currently loaded in a main player deck.
                    _pendingUpdates?.Writer.TryWrite(new TrackMetadataUpdate(entry.Key.Player, entry.Value));
                }
            }
        }
    }

    /// <summary>
    /// Stop finding album art for all active players.
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

            // Report the loss of our hot cached art and our shutdown, on the proper thread, and
            // outside our lock.
            var dyingCache = _hotCache.Keys.ToArray();
            EventDispatcher.Post(() =>
            {
                foreach (var deck in dyingCache)
                {
                    if (deck.HotCue == 0)
                    {
                        EventDispatcher.DeliverNow(AlbumArtChanged, this,
                            new AlbumArtUpdate(deck.Player, null), Logger);
                    }
                }
            });
            _hotCache.Clear();
            lock (_cacheLock)
            {
                _artCache.Clear();
                _artCacheRecentlyUsed.Clear();
                _artCacheEvictionQueue.Clear();
            }
            DeliverLifecycleAnnouncement(Logger, false);
        }
    }

    /// <summary>
    /// Test seam: the number of images currently held in the second-level art cache.
    /// </summary>
    internal int ArtCacheCountForTests
    {
        get
        {
            lock (_cacheLock)
            {
                return _artCache.Count;
            }
        }
    }

    /// <summary>
    /// Test seam: adds art directly to the second-level cache, exercising the clock eviction.
    /// </summary>
    /// <param name="artReference">identifies the art being cached</param>
    /// <param name="art">the art to cache</param>
    internal void AddArtToCacheForTests(DataReference artReference, AlbumArt art) =>
        AddArtToCache(artReference, art);

    /// <summary>
    /// Test seam: looks up art in the in-memory caches (marking it recently used if found in the
    /// second-level cache), without any network activity.
    /// </summary>
    /// <param name="artReference">identifies the art being sought</param>
    /// <returns>the cached art, or <c>null</c></returns>
    internal AlbumArt? FindArtInMemoryCachesForTests(DataReference artReference) =>
        FindArtInMemoryCaches(artReference);

    /// <summary>
    /// Test seam: reports whether the specified art is currently in the second-level cache,
    /// without affecting its recently-used status.
    /// </summary>
    /// <param name="artReference">identifies the art being sought</param>
    /// <returns><c>true</c> if the art is cached</returns>
    internal bool IsArtInCacheForTests(DataReference artReference) => _artCache.ContainsKey(artReference);

    /// <summary>
    /// Test seam: clears the second-level cache and its bookkeeping.
    /// </summary>
    internal void ClearArtCacheForTests()
    {
        lock (_cacheLock)
        {
            _artCache.Clear();
            _artCacheRecentlyUsed.Clear();
            _artCacheEvictionQueue.Clear();
        }
    }

    /// <summary>
    /// The single instance of this class which exists in the program.
    /// </summary>
    public static ArtFinder Instance { get; } = new();

    /// <summary>
    /// Prevent direct instantiation.
    /// </summary>
    private ArtFinder()
    {
        // Nothing to do.
    }

    /// <inheritdoc/>
    public override string ToString()
    {
        var sb = new System.Text.StringBuilder("ArtFinder[running:").Append(IsRunning)
            .Append(", passive:").Append(MetadataFinder.Instance.IsPassive)
            .Append(", artCacheSize:").Append(ArtCacheSize);
        if (IsRunning)
        {
            sb.Append(", loadedArt:[").Append(string.Join(", ", LoadedArt.Select(e => $"{e.Key}={e.Value}")))
                .Append("], cached art:").Append(ArtCacheCountForTests);
        }
        return sb.Append(']').ToString();
    }
}
