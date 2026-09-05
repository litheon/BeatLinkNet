using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace BeatLink.Data;

/// <summary>
/// <para>Watches for new metadata to become available for tracks loaded on players, and when
/// enough elements have been loaded, calculates a signature string that uniquely identifies the
/// track, based on its title, artist, duration, waveform, and beat grid. Reports the signatures to
/// any registered listeners once they have been identified. This supports the efficient triggering
/// of cues based on the recognition of specific tracks, regardless of the media from which they
/// have been loaded.</para>
/// </summary>
/// <remarks>
/// Ported from Deep Symmetry's beat-link <c>SignatureFinder</c>, by James Elliott.
/// </remarks>
public sealed class SignatureFinder : LifecycleParticipant
{
    private static readonly ILogger Logger = BeatLinkLogging.CreateLogger<SignatureFinder>();

    /// <summary>
    /// A queue used to hold players whose tracks now have enough information to calculate a
    /// signature, so we can process them on a lower priority thread, and not hold up delivery to
    /// more time-sensitive listeners. Present only while we are running.
    /// </summary>
    private volatile Channel<int>? _pendingUpdates;

    /// <summary>
    /// Holds the currently-recognized track signatures for each track loaded in a player.
    /// </summary>
    private readonly ConcurrentDictionary<int, string> _signatures = new();

    /// <summary>
    /// Called whenever we have lost some piece of information that determines a loaded track
    /// signature. If we were previously offering a signature for that player, clear it and notify
    /// our listeners.
    /// </summary>
    /// <param name="player">the player number that now lacks required signature information</param>
    private void ClearSignature(int player)
    {
        if (_signatures.TryRemove(player, out _))
        {
            DeliverSignatureUpdate(player, null);
        }
    }

    /// <summary>
    /// Called whenever we have a new piece of information that might complete our ability to
    /// calculate a track signature. Enqueues a message to the signature builder task that will
    /// cause it to see if it has everything it needs for that player, and if so, build and report
    /// the new signature.
    /// </summary>
    /// <param name="player">the player which may now have complete signature information</param>
    private void CheckIfSignatureReady(int player) => _pendingUpdates?.Writer.TryWrite(player);

    /// <summary>
    /// Our metadata listener updates our signature state as track metadata comes and goes.
    /// </summary>
    private void OnTrackMetadataChanged(object? sender, TrackMetadataUpdate update)
    {
        if (update.Metadata == null)
        {
            ClearSignature(update.Player);
        }
        else
        {
            CheckIfSignatureReady(update.Player);
        }
    }

    /// <summary>
    /// Keeps track of the RGB waveforms we have available for the players, since these are what we
    /// need to hash to compute a track signature. We ask for them using a raw analysis tag finder,
    /// since the <see cref="WaveformFinder"/> may be configured to request a different style.
    /// </summary>
    private readonly ConcurrentDictionary<int, WaveformDetail> _rgbWaveforms = new();

    /// <summary>
    /// Our tag listener updates our signature state as track RGB waveforms come and go.
    /// </summary>
    private void OnRgbWaveformTagChanged(object? sender, AnalysisTagUpdate update)
    {
        if (update.TaggedSection == null)
        {
            _rgbWaveforms.TryRemove(update.Player, out _);
            ClearSignature(update.Player);
        }
        else
        {
            // (Unlike the Java implementation, which would throw a runtime exception into the
            // listener isolation logic, we quietly ignore the tag if the player has no current
            // status from which to build the data reference.)
            if (VirtualCdj.Instance.GetLatestStatusFor(update.Player) is not CdjStatus lastStatus)
            {
                Logger.LogWarning("Ignoring RGB waveform tag for player {Player} with no status available.", update.Player);
                return;
            }
            var reference = new DataReference(update.Player, lastStatus.SourceSlot,
                lastStatus.RekordboxId, lastStatus.LoadedTrackType);
            var detail = new WaveformDetail(reference, update.TaggedSection);
            _rgbWaveforms[update.Player] = detail;
            CheckIfSignatureReady(update.Player);
        }
    }

    /// <summary>
    /// Our beat grid listener updates our signature state as track beat grids come and go.
    /// </summary>
    private void OnBeatGridChanged(object? sender, BeatGridUpdate update)
    {
        if (update.BeatGrid == null)
        {
            ClearSignature(update.Player);
        }
        else
        {
            CheckIfSignatureReady(update.Player);
        }
    }

    /// <summary>
    /// Keep track of whether we are running.
    /// </summary>
    private volatile bool _running;

    /// <summary>
    /// Check whether we are currently running: <c>true</c> if track signatures are being computed
    /// for all active players.
    /// </summary>
    /// <seealso cref="MetadataFinder.IsPassive"/>
    public override bool IsRunning => _running;

    /// <summary>
    /// The task which computes signatures on a separate thread so as not to slow down the
    /// high-priority update delivery thread; we perform potentially slow cryptographic
    /// computation.
    /// </summary>
    private Task? _queueHandler;

    /// <summary>
    /// Get the signatures that have been computed for all tracks currently loaded in any player
    /// for which we have been able to obtain all necessary metadata.
    /// </summary>
    /// <exception cref="InvalidOperationException">if the SignatureFinder is not running</exception>
    public IReadOnlyDictionary<int, string> Signatures
    {
        get
        {
            EnsureRunning();
            // Make a copy so callers get an immutable snapshot of the current state.
            return new Dictionary<int, string>(_signatures);
        }
    }

    /// <summary>
    /// Look up the signature we have calculated for the track loaded in the specified player.
    /// </summary>
    /// <param name="player">the player whose track signature is desired</param>
    /// <returns>the track signature if we have been able to compute one</returns>
    /// <exception cref="InvalidOperationException">if the SignatureFinder is not running</exception>
    public string? GetLatestSignatureFor(int player)
    {
        EnsureRunning();
        return _signatures.GetValueOrDefault(player);
    }

    /// <summary>
    /// Look up the signature we have calculated for the track loaded in a player, identified by a
    /// status update received from that player.
    /// </summary>
    /// <param name="update">a status update from the player for which a track signature is desired</param>
    /// <returns>the track signature if we have been able to compute one</returns>
    /// <exception cref="InvalidOperationException">if the SignatureFinder is not running</exception>
    public string? GetLatestSignatureFor(DeviceUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        return GetLatestSignatureFor(update.DeviceNumber);
    }

    /// <summary>
    /// Raised when the track signature available for a player changes. Delivered through the
    /// <see cref="EventDispatcher"/>, so a UI application can arrange to receive it on its
    /// dispatch thread.
    /// </summary>
    public event EventHandler<SignatureUpdate>? SignatureChanged;

    /// <summary>
    /// Send a signature update announcement to all registered listeners.
    /// </summary>
    /// <param name="player">the player whose track signature has changed</param>
    /// <param name="signature">the new signature, if any</param>
    private void DeliverSignatureUpdate(int player, string? signature) =>
        EventDispatcher.Deliver(SignatureChanged, this, new SignatureUpdate(player, signature), Logger);

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
            Logger.LogInformation("SignatureFinder stopping because {Sender} has.", sender);
            Stop();
        }
    }

    /// <summary>
    /// Send ourselves "updates" about any tracks that were loaded before we started, since we
    /// missed them.
    /// </summary>
    private void CheckExistingTracks()
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
                    CheckIfSignatureReady(entry.Key.Player);
                }
            }
        });
    }

    /// <summary>
    /// Helper method to add an integer value to a message digest as four big-endian bytes,
    /// matching the byte order produced by the Java implementation's <c>Util.numberToBytes</c>.
    /// </summary>
    /// <param name="digest">the message digest being built</param>
    /// <param name="value">the integer whose bytes should be included in the digest</param>
    private static void DigestInteger(IncrementalHash digest, int value)
    {
        Span<byte> valueBytes = stackalloc byte[4];
        Util.NumberToBytes(value, valueBytes, 0, 4);
        digest.AppendData(valueBytes);
    }

    /// <summary>
    /// Calculate the signature by which we can reliably recognize a loaded track.
    /// </summary>
    /// <param name="title">the track title</param>
    /// <param name="artist">the track artist, or <c>null</c> if there is no artist</param>
    /// <param name="duration">the duration of the track in seconds</param>
    /// <param name="waveformDetail">the RGB waveform detail of the track</param>
    /// <param name="beatGrid">the beat grid of the track</param>
    /// <returns>the SHA-1 hash of all the arguments supplied, or <c>null</c> if either
    /// <paramref name="waveformDetail"/> or <paramref name="beatGrid"/> was <c>null</c></returns>
    /// <exception cref="ArgumentException">if <paramref name="waveformDetail"/> is not an
    /// RGB-style waveform</exception>
    public string? ComputeTrackSignature(string? title, SearchableItem? artist, int duration,
        WaveformDetail? waveformDetail, BeatGrid? beatGrid)
    {
        var safeTitle = title ?? "";
        var artistName = artist == null ? "[no artist]" : artist.Label;
        if (waveformDetail == null || beatGrid == null)
        {
            // (The Java implementation reaches the same outcome by catching the
            // NullPointerException these missing inputs would provoke.)
            Logger.LogInformation("Returning null track signature because an input element was null.");
            return null;
        }

        // Validate that we were given the right kind of waveform.
        if (waveformDetail.Style != WaveformStyle.Rgb)
        {
            throw new ArgumentException("Signatures can only be computed from RGB waveform details.",
                nameof(waveformDetail));
        }

        // Compute the SHA-1 hash of our fields. The digest layout matches the Java implementation
        // exactly: UTF-8 title, a zero byte, UTF-8 artist name (or "[no artist]"), a zero byte,
        // the duration as a 4-byte big-endian integer, the raw RGB waveform detail bytes, then for
        // each beat from 1 through the beat count, the beat-within-bar and the time within the
        // track in milliseconds, each as 4-byte big-endian integers. (For historical reasons, the
        // tempo at each beat is not considered in calculating the signature.)
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        digest.AppendData(Encoding.UTF8.GetBytes(safeTitle));
        digest.AppendData([0]);
        digest.AppendData(Encoding.UTF8.GetBytes(artistName));
        digest.AppendData([0]);
        DigestInteger(digest, duration);
        digest.AppendData(waveformDetail.Data.Span);
        for (var i = 1; i <= beatGrid.BeatCount; i++)
        {
            DigestInteger(digest, beatGrid.GetBeatWithinBar(i));
            DigestInteger(digest, (int)beatGrid.GetTimeWithinTrack(i));
        }
        var result = digest.GetHashAndReset();

        // Create a lowercase hex string representation of the hash.
        return Convert.ToHexStringLower(result);
    }

    /// <summary>
    /// We have reason to believe we might have enough information to calculate a signature for the
    /// track loaded on a player. Verify that, and if so, perform the computation and record and
    /// report the new signature.
    /// </summary>
    /// <param name="player">the player whose signature information should be examined</param>
    private void HandleUpdate(int player)
    {
        var metadata = MetadataFinder.Instance.GetLatestMetadataFor(player);
        var waveformDetail = _rgbWaveforms.GetValueOrDefault(player);
        var beatGrid = BeatGridFinder.Instance.GetLatestBeatGridFor(player);
        if (metadata != null && waveformDetail != null && beatGrid != null)
        {
            var signature = ComputeTrackSignature(metadata.Title, metadata.Artist,
                metadata.Duration, waveformDetail, beatGrid);
            if (signature != null)
            {
                _signatures[player] = signature;
                DeliverSignatureUpdate(player, signature);
            }
        }
    }

    /// <summary>
    /// Serializes lifecycle transitions, standing in for the Java <c>synchronized</c> start and
    /// stop methods.
    /// </summary>
    private readonly object _lifecycleLock = new();

    /// <summary>
    /// The body of the queue-handling task: processes queued signature checks until the channel
    /// is completed (by <see cref="Stop"/>) or we are no longer running.
    /// </summary>
    /// <param name="reader">supplies the queued player numbers</param>
    private void RunQueueHandler(ChannelReader<int> reader)
    {
        while (IsRunning)
        {
            int player;
            try
            {
                if (!reader.TryRead(out player))
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
                Logger.LogDebug(e, "Queue read interrupted, presumably due to SignatureFinder shutdown.");
                break;
            }
            if (!IsRunning)
            {
                break;
            }
            try
            {
                HandleUpdate(player);
            }
            catch (Exception e)
            {
                Logger.LogError(e, "Problem processing track signature update");
            }
        }
    }

    /// <summary>
    /// Start finding signatures for all active players. Starts the <see cref="MetadataFinder"/>
    /// if it is not already running, because we need it to send us metadata updates to notice when
    /// new tracks are loaded. This in turn starts the <see cref="DeviceFinder"/>, so we can keep
    /// track of the comings and goings of players themselves. We also start the
    /// <see cref="AnalysisTagFinder"/> and <see cref="BeatGridFinder"/> in order to get the other
    /// pieces of information we need for computing track signatures.
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
                MetadataFinder.Instance.Stopped += OnDependencyStopped;
                AnalysisTagFinder.Instance.Stopped += OnDependencyStopped;
                BeatGridFinder.Instance.Stopped += OnDependencyStopped;
                _dependencyListenersRegistered = true;
            }
            MetadataFinder.Instance.Start();
            MetadataFinder.Instance.TrackMetadataChanged += OnTrackMetadataChanged;

            // Configure the analysis tag finder to feed us RGB waveform detail tags for all
            // loaded tracks.
            AnalysisTagFinder.Instance.Start();
            AnalysisTagFinder.Instance.AddAnalysisTagListener(OnRgbWaveformTagChanged, ".EXT", "PWV5");

            BeatGridFinder.Instance.Start();
            BeatGridFinder.Instance.BeatGridChanged += OnBeatGridChanged;

            var channel = Channel.CreateBounded<int>(new BoundedChannelOptions(20)
            {
                FullMode = BoundedChannelFullMode.DropWrite,
                SingleReader = true
            });
            _pendingUpdates = channel;
            _running = true;
            _queueHandler = Task.Factory.StartNew(() => RunQueueHandler(channel.Reader), CancellationToken.None,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);
            DeliverLifecycleAnnouncement(Logger, true);
            CheckExistingTracks();
        }
    }

    /// <summary>
    /// Stop finding signatures for all active players.
    /// </summary>
    public void Stop()
    {
        lock (_lifecycleLock)
        {
            if (IsRunning)
            {
                MetadataFinder.Instance.TrackMetadataChanged -= OnTrackMetadataChanged;
                AnalysisTagFinder.Instance.RemoveAnalysisTagListener(OnRgbWaveformTagChanged, ".EXT", "PWV5");
                BeatGridFinder.Instance.BeatGridChanged -= OnBeatGridChanged;
                _running = false;
                _pendingUpdates?.Writer.TryComplete();
                _pendingUpdates = null;
                _queueHandler = null;
                _rgbWaveforms.Clear();

                // Report the loss of our signatures, on the proper thread, outside our lock.
                var dyingSignatures = _signatures.Keys.ToArray();
                _signatures.Clear();
                EventDispatcher.Post(() =>
                {
                    foreach (var player in dyingSignatures)
                    {
                        EventDispatcher.DeliverNow(SignatureChanged, this, new SignatureUpdate(player, null), Logger);
                    }
                });
            }
            DeliverLifecycleAnnouncement(Logger, false);
        }
    }

    /// <summary>
    /// The single instance of this class which exists in the program.
    /// </summary>
    public static SignatureFinder Instance { get; } = new();

    /// <summary>
    /// Prevent direct instantiation.
    /// </summary>
    private SignatureFinder()
    {
        // Nothing to do.
    }

    /// <inheritdoc/>
    public override string ToString() =>
        $"SignatureFinder[running:{IsRunning}, signatures:[{string.Join(", ", _signatures.Select(e => $"{e.Key}={e.Value}"))}]]";
}
