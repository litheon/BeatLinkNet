using System.Net;
using BeatLink.Data;

namespace BeatLink.Tests;

/// <summary>
/// Exercises the <see cref="TimeFinder"/>'s interpolation and listener logic using synthetic
/// status, beat, and precise-position packets injected through its internal processing seams.
/// The service dependency chain is started over loopback seams (ephemeral ports only), with the
/// <see cref="VirtualCdj"/> forced into a running state so no real network claim takes place, and
/// beat grids are injected directly into the <see cref="BeatGridFinder"/> hot cache.
/// </summary>
[Collection("Network services")]
public sealed class TimeFinderTests : IDisposable
{
    private const int Player = 2;

    private static TimeFinder Finder => TimeFinder.Instance;

    public TimeFinderTests()
    {
        ResetState();
    }

    public void Dispose()
    {
        ResetState();
    }

    private static void ResetState()
    {
        Finder.Stop();
        BeatGridFinder.Instance.Stop();
        MetadataFinder.Instance.Stop();
        MetadataFinder.Instance.ClearMountsForTests();
        DbServer.ConnectionManager.Instance.Stop();
        BeatFinder.Instance.Stop();
        BeatFinder.Instance.PortOverride = null;
        DeviceFinder.Instance.Stop();
        DeviceFinder.Instance.PortOverride = null;
        DeviceFinder.Instance.RemoveIgnoredAddress(IPAddress.Loopback);
        VirtualCdj.Instance.ResetForTests();
        Finder.Slack = 50;
        Finder.UsePrecisePositionPackets = true;
        // Stale async lifecycle announcements from stopped services must finish before
        // the next test configures singleton state, or they can undo it mid-test.
        LifecycleParticipant.WaitForPendingAnnouncements(TimeSpan.FromSeconds(5));
    }

    /// <summary>
    /// Starts the TimeFinder and the entire loopback-safe dependency chain it pulls in.
    /// </summary>
    private static void StartFinder()
    {
        DeviceFinder.Instance.PortOverride = 0;
        BeatFinder.Instance.PortOverride = 0;
        VirtualCdj.Instance.SetRunningStateForTests(true);
        Finder.Start();
        Assert.True(Finder.IsRunning);
        Assert.True(BeatGridFinder.Instance.IsRunning);
        Assert.True(BeatFinder.Instance.IsRunning);
    }

    /// <summary>
    /// An eight-beat grid for track 42 (the rekordbox ID used by the status fixture), with beats
    /// at half-second intervals starting at time zero.
    /// </summary>
    private static BeatGrid MakeBeatGrid()
    {
        var reference = new DataReference(Player, CdjStatus.TrackSourceSlot.UsbSlot, 42,
            CdjStatus.TrackType.Rekordbox);
        return new BeatGrid(reference,
            beatWithinBarValues: [1, 2, 3, 4, 1, 2, 3, 4],
            bpmValues: [12000, 12000, 12000, 12000, 12000, 12000, 12000, 12000],
            timeWithinTrackValues: [0, 500, 1000, 1500, 2000, 2500, 3000, 3500]);
    }

    /// <summary>
    /// Builds a synthetic CDJ status update for the fixture track (rekordbox ID 42), with the
    /// fields relevant to position interpolation under direct control.
    /// </summary>
    private static CdjStatus MakeStatus(int beatNumber, bool playing, long pitch = Util.NeutralPitch,
        bool reverse = false)
    {
        var bytes = PacketFixtures.NexusCdjStatusPacket();
        bytes[0x21] = Player;
        bytes[0x28] = Player;                              // Track source player.
        Util.NumberToBytes(beatNumber, bytes, 0xa0, 4);    // Beat number.
        Util.NumberToBytes(pitch, bytes, 0x8d, 3);         // Pitch.
        bytes[0x89] = (byte)(playing ? 0x40 : 0x00);       // Playing flag.
        bytes[0x7b] = (byte)(playing ? 3 : 5);             // Play state 1: playing or paused.
        bytes[0x9d] = (byte)(reverse ? 1 : 9);             // Play state 3: paused/reverse or forward vinyl.
        return new CdjStatus(bytes, VirtualCdjTestHelpers.AddressFor(Player));
    }

    /// <summary>
    /// Builds a synthetic precise position packet reporting the specified playback position at
    /// +0% pitch.
    /// </summary>
    private static PrecisePosition MakePrecisePosition(int milliseconds)
    {
        var bytes = PacketFixtures.NewPacket(0x3c, 0x0b, "CDJ-3000", 0x0b);
        bytes[0x21] = Player;
        Util.NumberToBytes(300, bytes, 0x24, 4);           // Track length in seconds.
        Util.NumberToBytes(milliseconds, bytes, 0x28, 4);  // Playback position.
        Util.NumberToBytes(0, bytes, 0x2c, 4);             // Pitch: +0.00%.
        Util.NumberToBytes(1280, bytes, 0x38, 4);          // Track BPM * 10.
        return new PrecisePosition(bytes, VirtualCdjTestHelpers.AddressFor(Player));
    }

    /// <summary>
    /// Records the callbacks a track position listener receives.
    /// </summary>
    private sealed class RecordingListener : ITrackPositionBeatListener
    {
        public List<TrackPositionUpdate?> Movements { get; } = [];

        public List<(Beat Beat, TrackPositionUpdate Position)> Beats { get; } = [];

        public void MovementChanged(TrackPositionUpdate? update)
        {
            lock (Movements)
            {
                Movements.Add(update);
            }
        }

        public void NewBeat(Beat beat, TrackPositionUpdate position)
        {
            lock (Movements)
            {
                Beats.Add((beat, position));
            }
        }

        public int MovementCount
        {
            get
            {
                lock (Movements)
                {
                    return Movements.Count;
                }
            }
        }
    }

    [Fact]
    public void RequiresRunningForQueries()
    {
        Assert.False(Finder.IsRunning);
        Assert.Throws<InvalidOperationException>(() => Finder.LatestPositions);
        Assert.Throws<InvalidOperationException>(() => Finder.GetLatestPositionFor(Player));
        Assert.Equal(-1, Finder.GetTimeFor(Player));  // GetTimeFor does not require running.
    }

    [Fact]
    public void StatusEstablishesPositionAndInterpolatesWhilePlaying()
    {
        StartFinder();
        BeatGridFinder.Instance.InjectBeatGridForTests(Player, MakeBeatGrid());

        // Without a track position, listeners get an immediate null orientation callback.
        var listener = new RecordingListener();
        Finder.AddTrackPositionListener(Player, listener);
        Assert.Equal(1, listener.MovementCount);
        Assert.Null(listener.Movements[0]);

        // The first status for a new track guesses the time of the reported beat.
        Finder.ProcessDeviceUpdate(MakeStatus(beatNumber: 4, playing: true));
        var position = Finder.GetLatestPositionFor(Player);
        Assert.NotNull(position);
        Assert.Equal(1500, position.Milliseconds);
        Assert.Equal(4, position.BeatNumber);
        Assert.True(position.Playing);
        Assert.False(position.Reverse);
        Assert.False(position.Precise);
        Assert.Equal(1.0, position.Pitch, 6);
        Assert.Equal(2, listener.MovementCount);
        Assert.Same(position, listener.Movements[1]);

        // While playing at normal pitch, the interpolated time advances with the wall clock.
        Thread.Sleep(200);
        var interpolated = Finder.GetTimeFor(Player);
        Assert.InRange(interpolated, 1600, 3000);
        Assert.True(Finder.GetTimeFor(Player) >= interpolated);
    }

    [Fact]
    public void StoppedStatusFreezesInterpolatedTime()
    {
        StartFinder();
        BeatGridFinder.Instance.InjectBeatGridForTests(Player, MakeBeatGrid());

        Finder.ProcessDeviceUpdate(MakeStatus(beatNumber: 4, playing: true));
        Thread.Sleep(100);
        Finder.ProcessDeviceUpdate(MakeStatus(beatNumber: 4, playing: false));

        var position = Finder.GetLatestPositionFor(Player);
        Assert.NotNull(position);
        Assert.False(position.Playing);

        // A stopped player's interpolated time does not move.
        var first = Finder.GetTimeFor(Player);
        Thread.Sleep(120);
        Assert.Equal(first, Finder.GetTimeFor(Player));
    }

    [Fact]
    public void ReversePlaybackInterpolatesBackwards()
    {
        StartFinder();
        BeatGridFinder.Instance.InjectBeatGridForTests(Player, MakeBeatGrid());

        Finder.ProcessDeviceUpdate(MakeStatus(beatNumber: 6, playing: true));
        Thread.Sleep(50);
        Finder.ProcessDeviceUpdate(MakeStatus(beatNumber: 6, playing: true, reverse: true));

        var position = Finder.GetLatestPositionFor(Player);
        Assert.NotNull(position);
        Assert.True(position.Reverse);
        Assert.True(position.Playing);

        var first = Finder.GetTimeFor(Player);
        Thread.Sleep(150);
        Assert.True(Finder.GetTimeFor(Player) < first, "time should move backwards while playing in reverse");
    }

    [Fact]
    public void PitchChangesScaleInterpolationAndNotifyListeners()
    {
        StartFinder();
        BeatGridFinder.Instance.InjectBeatGridForTests(Player, MakeBeatGrid());

        var listener = new RecordingListener();
        Finder.AddTrackPositionListener(Player, listener);

        Finder.ProcessDeviceUpdate(MakeStatus(beatNumber: 1, playing: true));
        Assert.Equal(2, listener.MovementCount);  // Null orientation plus the initial position.

        // A second status right away with identical playback parameters is not a significant
        // change (the interpolations agree within the 50 ms slack), so no callback is delivered.
        Finder.ProcessDeviceUpdate(MakeStatus(beatNumber: 1, playing: true));
        Assert.Equal(2, listener.MovementCount);

        // Doubling the pitch is a significant change, and updates the interpolation rate.
        Finder.ProcessDeviceUpdate(MakeStatus(beatNumber: 1, playing: true, pitch: Util.NeutralPitch * 2));
        Assert.Equal(3, listener.MovementCount);
        var position = Finder.GetLatestPositionFor(Player);
        Assert.NotNull(position);
        Assert.Equal(2.0, position.Pitch, 6);

        var before = Finder.GetTimeFor(Player);
        Thread.Sleep(150);
        var advanced = Finder.GetTimeFor(Player) - before;
        Assert.InRange(advanced, 200, 2000);  // Roughly twice the elapsed wall time.
    }

    [Fact]
    public void SlackControlsListenerUpdateFrequency()
    {
        StartFinder();
        BeatGridFinder.Instance.InjectBeatGridForTests(Player, MakeBeatGrid());
        Assert.Equal(50, Finder.Slack);

        var listener = new RecordingListener();
        Finder.AddTrackPositionListener(Player, listener);
        Finder.ProcessDeviceUpdate(MakeStatus(beatNumber: 1, playing: true));
        Assert.Equal(2, listener.MovementCount);

        // A jump to beat 7 diverges the interpolation by ~3000 ms, well past the default 50 ms
        // slack, so the listener is told.
        Finder.ProcessDeviceUpdate(MakeStatus(beatNumber: 7, playing: true));
        Assert.Equal(3, listener.MovementCount);
        Assert.Equal(3000, Finder.GetLatestPositionFor(Player)!.Milliseconds);

        // With an enormous slack, an equally large jump back is absorbed silently, even though
        // the tracked position itself is updated.
        Finder.Slack = 10_000;
        Finder.ProcessDeviceUpdate(MakeStatus(beatNumber: 1, playing: true));
        Assert.Equal(3, listener.MovementCount);
        Assert.Equal(0, Finder.GetLatestPositionFor(Player)!.Milliseconds);
    }

    [Fact]
    public void BeatsAdvancePositionDefinitivelyAndReachBeatListeners()
    {
        StartFinder();
        BeatGridFinder.Instance.InjectBeatGridForTests(Player, MakeBeatGrid());

        var listener = new RecordingListener();
        Finder.AddTrackPositionListener(Player, listener);

        // Establish the position within beat 4, then interpolate a couple hundred ms into it.
        Finder.ProcessDeviceUpdate(MakeStatus(beatNumber: 4, playing: true));
        Thread.Sleep(250);
        Finder.ProcessDeviceUpdate(MakeStatus(beatNumber: 4, playing: true));
        var interpolated = Finder.GetLatestPositionFor(Player);
        Assert.NotNull(interpolated);
        Assert.InRange(interpolated.Milliseconds, 1600, 2400);

        // A beat packet now marks the definitive start of beat 5.
        var beat = VirtualCdjTestHelpers.MakeBeat(Player, 12000);
        Finder.ProcessBeat(beat);
        var position = Finder.GetLatestPositionFor(Player);
        Assert.NotNull(position);
        Assert.Equal(5, position.BeatNumber);
        Assert.Equal(2000, position.Milliseconds);
        Assert.True(position.Definitive);
        Assert.True(position.FromBeat);
        Assert.True(position.Playing);

        // The beat-aware listener saw the beat along with the resulting position.
        var (reportedBeat, reportedPosition) = Assert.Single(listener.Beats);
        Assert.Same(beat, reportedBeat);
        Assert.Same(position, reportedPosition);
    }

    [Fact]
    public void OutOfOrderBeatDoesNotAdvanceBeatNumber()
    {
        StartFinder();
        BeatGridFinder.Instance.InjectBeatGridForTests(Player, MakeBeatGrid());

        // A beat arriving right at the start of beat 4 (less than a fifth of a beat in) is
        // treated as the out-of-order packet for the beat we already know about.
        Finder.ProcessDeviceUpdate(MakeStatus(beatNumber: 4, playing: true));
        Finder.ProcessBeat(VirtualCdjTestHelpers.MakeBeat(Player, 12000));
        var position = Finder.GetLatestPositionFor(Player);
        Assert.NotNull(position);
        Assert.Equal(4, position.BeatNumber);
        Assert.Equal(1500, position.Milliseconds);
        Assert.True(position.FromBeat);
    }

    [Fact]
    public void BeatsBeforeAnyStatusAreIgnored()
    {
        StartFinder();
        BeatGridFinder.Instance.InjectBeatGridForTests(Player, MakeBeatGrid());
        Finder.ProcessBeat(VirtualCdjTestHelpers.MakeBeat(Player, 12000));
        Assert.Null(Finder.GetLatestPositionFor(Player));
    }

    [Fact]
    public void PrecisePositionsDominateStatusUpdatesAndCanBeDisabled()
    {
        StartFinder();
        BeatGridFinder.Instance.InjectBeatGridForTests(Player, MakeBeatGrid());
        Assert.True(Finder.UsePrecisePositionPackets);

        Finder.ProcessPrecisePosition(MakePrecisePosition(1234));
        var position = Finder.GetLatestPositionFor(Player);
        Assert.NotNull(position);
        Assert.True(position.Precise);
        Assert.True(position.Definitive);
        Assert.Equal(1234, position.Milliseconds);
        Assert.Equal(3, position.BeatNumber);  // 1234 ms falls within beat 3 of the grid.
        Assert.False(position.Playing);        // No status update says the player is playing.

        // Ordinary status updates are ignored while precise positions are in effect.
        Finder.ProcessDeviceUpdate(MakeStatus(beatNumber: 7, playing: true));
        Assert.Same(position, Finder.GetLatestPositionFor(Player));

        // Stopped and precise means the interpolated time holds exactly steady.
        Assert.Equal(1234, Finder.GetTimeFor(Player));

        // The 8.0.0 jitter workaround: once disabled, precise position packets are disregarded.
        Finder.UsePrecisePositionPackets = false;
        Finder.ProcessPrecisePosition(MakePrecisePosition(4321));
        Assert.Equal(1234, Finder.GetLatestPositionFor(Player)!.Milliseconds);
    }

    [Fact]
    public void ListenerManagementReplacesAndRemovesRegistrations()
    {
        StartFinder();
        BeatGridFinder.Instance.InjectBeatGridForTests(Player, MakeBeatGrid());

        var listener = new RecordingListener();
        Finder.AddTrackPositionListener(9, listener);      // Initially interested in player 9.
        Assert.Equal(1, listener.MovementCount);

        // Updates for other players are not delivered.
        Finder.ProcessDeviceUpdate(MakeStatus(beatNumber: 1, playing: true));
        Assert.Equal(1, listener.MovementCount);

        // Re-registering for our player replaces the registration (with a fresh orientation).
        Finder.AddTrackPositionListener(Player, listener);
        Assert.Equal(2, listener.MovementCount);
        Assert.NotNull(listener.Movements[1]);

        // After removal, no further updates arrive.
        Finder.RemoveTrackPositionListener(listener);
        Finder.ProcessDeviceUpdate(MakeStatus(beatNumber: 1, playing: true, pitch: Util.NeutralPitch / 2));
        Assert.Equal(2, listener.MovementCount);
    }

    [Fact]
    public void StoppingClearsPositions()
    {
        StartFinder();
        BeatGridFinder.Instance.InjectBeatGridForTests(Player, MakeBeatGrid());
        Finder.ProcessDeviceUpdate(MakeStatus(beatNumber: 4, playing: true));
        Assert.NotNull(Finder.GetLatestPositionFor(Player));

        Finder.Stop();
        Assert.False(Finder.IsRunning);
        Assert.Equal(-1, Finder.GetTimeFor(Player));
        Assert.Throws<InvalidOperationException>(() => Finder.GetLatestPositionFor(Player));
    }
}
