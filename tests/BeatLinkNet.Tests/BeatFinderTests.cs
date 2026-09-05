using System.Net;
using System.Net.Sockets;

namespace BeatLink.Tests;

/// <summary>
/// Exercises the <see cref="BeatFinder"/> singleton over loopback UDP. These tests share mutable
/// singleton state with the <see cref="DeviceFinderTests"/> (via the <see cref="DeviceFinder"/>
/// ignore lists the <see cref="BeatFinder"/> consults), so both classes are placed in the same
/// xUnit collection to keep them from running in parallel.
/// </summary>
[Collection("Network services")]
public sealed class BeatFinderTests : IDisposable
{
    private static BeatFinder Finder => BeatFinder.Instance;

    public BeatFinderTests()
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
        Finder.PortOverride = null;
        DeviceFinder.Instance.RemoveIgnoredAddress(IPAddress.Loopback);
        // Stale async lifecycle announcements from stopped services must finish before
        // the next test configures singleton state, or they can undo it mid-test.
        LifecycleParticipant.WaitForPendingAnnouncements(TimeSpan.FromSeconds(5));
    }

    /// <summary>
    /// Starts the finder on an ephemeral loopback-reachable port and returns that port.
    /// </summary>
    private static int StartOnEphemeralPort()
    {
        Finder.PortOverride = 0;
        Finder.Start();
        return Finder.LocalPort!.Value;
    }

    private static byte[] BeatPacket(string name, byte deviceNumber)
    {
        var bytes = PacketFixtures.NewPacket(0x60, 0x28, name, 0x0b);
        bytes[0x21] = deviceNumber;
        Util.NumberToBytes(300, bytes, 0x24, 4);
        Util.NumberToBytes(Util.NeutralPitch, bytes, 0x55, 3);
        Util.NumberToBytes(12850, bytes, 0x5a, 2);
        bytes[0x5c] = 3;
        return bytes;
    }

    private static byte[] PositionPacket(byte deviceNumber)
    {
        var bytes = PacketFixtures.NewPacket(0x3c, 0x0b, "CDJ-3000", 0x0b);
        bytes[0x21] = deviceNumber;
        Util.NumberToBytes(300, bytes, 0x24, 4);      // track length in seconds
        Util.NumberToBytes(45123, bytes, 0x28, 4);    // playback position in ms
        Util.NumberToBytes(500, bytes, 0x2c, 4);      // pitch percentage * 100
        Util.NumberToBytes(1285, bytes, 0x38, 4);     // BPM * 10
        return bytes;
    }

    private static byte[] OnAirPacket(byte[] channels, byte[]? extendedChannels = null)
    {
        var length = extendedChannels == null ? 0x2d : 0x35;
        var bytes = PacketFixtures.NewPacket(length, 0x03, "DJM-900nexus", 0x0b);
        bytes[0x21] = 33;
        channels.CopyTo(bytes, 0x24);
        extendedChannels?.CopyTo(bytes, 0x2d);
        return bytes;
    }

    private static byte[] SyncPacket(byte command)
    {
        var bytes = PacketFixtures.NewPacket(0x2c, 0x2a, "DJM-900nexus", 0x0b);
        bytes[0x21] = 33;
        bytes[0x2b] = command;
        return bytes;
    }

    private static byte[] MasterHandoffRequestPacket(byte toPlayer)
    {
        var bytes = PacketFixtures.NewPacket(0x28, 0x26, "CDJ-3000", 0x0b);
        bytes[0x21] = toPlayer;
        return bytes;
    }

    private static byte[] MasterHandoffResponsePacket(byte fromPlayer, bool yielded)
    {
        var bytes = PacketFixtures.NewPacket(0x2c, 0x27, "CDJ-3000", 0x0b);
        bytes[0x21] = fromPlayer;
        bytes[0x2b] = yielded ? (byte)1 : (byte)0;
        return bytes;
    }

    private static byte[] FaderStartPacket(byte channel1, byte channel2, byte channel3, byte channel4)
    {
        var bytes = PacketFixtures.NewPacket(0x28, 0x02, "DJM-900nexus", 0x0b);
        bytes[0x21] = 33;
        bytes[0x24] = channel1;
        bytes[0x25] = channel2;
        bytes[0x26] = channel3;
        bytes[0x27] = channel4;
        return bytes;
    }

    /// <summary>
    /// Sends a beat packet whose arrival proves that all previously sent packets have been fully
    /// processed, since the single receive thread handles packets in order.
    /// </summary>
    private static async Task AwaitBarrierBeat(int port)
    {
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<Beat> onBeat = (_, beat) =>
        {
            if (beat.DeviceNumber == 15)
            {
                barrier.TrySetResult();
            }
        };
        Finder.BeatReceived += onBeat;
        try
        {
            ServiceTestHelpers.SendToLoopback(port, BeatPacket("CDJ-3000", 15));
            await barrier.Task.WaitAsync(ServiceTestHelpers.EventTimeout);
        }
        finally
        {
            Finder.BeatReceived -= onBeat;
        }
    }

    [Fact]
    public async Task StartIsIdempotentAndStopCleansUp()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler onStarted = (_, _) => started.TrySetResult();
        EventHandler onStopped = (_, _) => stopped.TrySetResult();
        Finder.Started += onStarted;
        Finder.Stopped += onStopped;
        try
        {
            Assert.False(Finder.IsRunning);
            var port = StartOnEphemeralPort();
            Assert.True(Finder.IsRunning);
            await started.Task.WaitAsync(ServiceTestHelpers.EventTimeout);

            // Starting again has no effect and keeps the same socket.
            Finder.Start();
            Assert.Equal(port, Finder.LocalPort);

            // The port override cannot be changed while running.
            Assert.Throws<InvalidOperationException>(() => Finder.PortOverride = 12345);

            Finder.Stop();
            Assert.False(Finder.IsRunning);
            Assert.Null(Finder.LocalPort);
            await stopped.Task.WaitAsync(ServiceTestHelpers.EventTimeout);
            Assert.Throws<InvalidOperationException>(() => Finder.CanSeeXdjAzInProDjLinkMode);

            // The socket has been released, so the port can be bound again.
            using (var reclaimed = new UdpClient(port))
            {
                Assert.NotNull(reclaimed.Client.LocalEndPoint);
            }

            // Stopping again is also idempotent.
            Finder.Stop();
        }
        finally
        {
            Finder.Started -= onStarted;
            Finder.Stopped -= onStopped;
        }
    }

    [Fact]
    public void ExclusiveBindFailureSurfacesAsSocketException()
    {
        using var competitor = new UdpClient(0);
        var port = ((IPEndPoint)competitor.Client.LocalEndPoint!).Port;
        Finder.PortOverride = port;
        Assert.Throws<SocketException>(Finder.Start);
        Assert.False(Finder.IsRunning);
    }

    [Fact]
    public async Task BeatReceivedFiresWithCorrectPayload()
    {
        var received = new TaskCompletionSource<Beat>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<Beat> onBeat = (_, beat) => received.TrySetResult(beat);
        Finder.BeatReceived += onBeat;
        try
        {
            var port = StartOnEphemeralPort();
            ServiceTestHelpers.SendToLoopback(port, BeatPacket("CDJ-3000", 2));
            var beat = await received.Task.WaitAsync(ServiceTestHelpers.EventTimeout);
            Assert.Equal("CDJ-3000", beat.DeviceName);
            Assert.Equal(2, beat.DeviceNumber);
            Assert.Equal(12850, beat.Bpm);
            Assert.Equal(3, beat.BeatWithinBar);
            Assert.Equal(300, beat.NextBeat);
            Assert.Equal(IPAddress.Loopback, beat.Address);
        }
        finally
        {
            Finder.BeatReceived -= onBeat;
        }
    }

    [Fact]
    public async Task PriorityBeatListenersRunBeforePublicSubscribers()
    {
        var order = new List<string>();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new RecordingPriorityListener("first", order);
        var second = new RecordingPriorityListener("second", order);
        EventHandler<Beat> onBeat = (_, _) =>
        {
            lock (order)
            {
                order.Add("public");
            }
            done.TrySetResult();
        };
        Finder.AddPriorityBeatListener(first);
        Finder.AddPriorityBeatListener(second);
        Finder.BeatReceived += onBeat;
        try
        {
            var port = StartOnEphemeralPort();
            ServiceTestHelpers.SendToLoopback(port, BeatPacket("CDJ-3000", 2));
            await done.Task.WaitAsync(ServiceTestHelpers.EventTimeout);
            lock (order)
            {
                Assert.Equal(new[] { "first", "second", "public" }, order);
            }

            // A removed priority listener stops receiving beats.
            Finder.RemovePriorityBeatListener(first);
            Finder.BeatReceived -= onBeat;
            await AwaitBarrierBeat(port);
            lock (order)
            {
                Assert.Equal(new[] { "first", "second", "public", "second" }, order);
            }
        }
        finally
        {
            Finder.RemovePriorityBeatListener(first);
            Finder.RemovePriorityBeatListener(second);
            Finder.BeatReceived -= onBeat;
        }
    }

    [Fact]
    public async Task PrecisePositionReceivedFiresWithCorrectPayload()
    {
        var received = new TaskCompletionSource<PrecisePosition>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<PrecisePosition> onPosition = (_, position) => received.TrySetResult(position);
        Finder.PrecisePositionReceived += onPosition;
        try
        {
            var port = StartOnEphemeralPort();
            ServiceTestHelpers.SendToLoopback(port, PositionPacket(1));
            var position = await received.Task.WaitAsync(ServiceTestHelpers.EventTimeout);
            Assert.Equal(1, position.DeviceNumber);
            Assert.Equal(300, position.TrackLength);
            Assert.Equal(45123, position.PlaybackPosition);
        }
        finally
        {
            Finder.PrecisePositionReceived -= onPosition;
        }
    }

    [Fact]
    public async Task ChannelsOnAirFiresWithAudibleChannels()
    {
        var updates = new List<IReadOnlySet<int>>();
        EventHandler<IReadOnlySet<int>> onAir = (_, channels) =>
        {
            lock (updates)
            {
                updates.Add(channels);
            }
        };
        Finder.ChannelsOnAir += onAir;
        try
        {
            var port = StartOnEphemeralPort();
            ServiceTestHelpers.SendToLoopback(port, OnAirPacket([0, 1, 1, 0]));
            ServiceTestHelpers.SendToLoopback(port, OnAirPacket([1, 0, 0, 0], [1, 0]));
            await AwaitBarrierBeat(port);
            lock (updates)
            {
                Assert.Equal(2, updates.Count);
                Assert.Equal(new[] { 2, 3 }, updates[0].Order());
                Assert.Equal(new[] { 1, 5 }, updates[1].Order());
            }
        }
        finally
        {
            Finder.ChannelsOnAir -= onAir;
        }
    }

    [Fact]
    public async Task SyncAndMasterCommandsFire()
    {
        var syncCommands = new List<bool>();
        var masterCount = 0;
        EventHandler<bool> onSync = (_, mode) =>
        {
            lock (syncCommands)
            {
                syncCommands.Add(mode);
            }
        };
        EventHandler<EventArgs> onMaster = (_, _) => Interlocked.Increment(ref masterCount);
        Finder.SyncModeCommanded += onSync;
        Finder.MasterCommanded += onMaster;
        try
        {
            var port = StartOnEphemeralPort();
            ServiceTestHelpers.SendToLoopback(port, SyncPacket(0x10));
            ServiceTestHelpers.SendToLoopback(port, SyncPacket(0x20));
            ServiceTestHelpers.SendToLoopback(port, SyncPacket(0x01));
            await AwaitBarrierBeat(port);
            lock (syncCommands)
            {
                // The become-master command also implies sync mode on, as in the Java implementation.
                Assert.Equal(new[] { true, false, true }, syncCommands);
            }
            Assert.Equal(1, Volatile.Read(ref masterCount));
        }
        finally
        {
            Finder.SyncModeCommanded -= onSync;
            Finder.MasterCommanded -= onMaster;
        }
    }

    [Fact]
    public async Task MasterYieldEventsFire()
    {
        var requested = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var responded = new TaskCompletionSource<MasterYieldResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<int> onRequested = (_, player) => requested.TrySetResult(player);
        EventHandler<MasterYieldResponse> onResponded = (_, response) => responded.TrySetResult(response);
        Finder.MasterYieldRequested += onRequested;
        Finder.MasterYieldResponded += onResponded;
        try
        {
            var port = StartOnEphemeralPort();
            ServiceTestHelpers.SendToLoopback(port, MasterHandoffRequestPacket(2));
            ServiceTestHelpers.SendToLoopback(port, MasterHandoffResponsePacket(3, true));
            Assert.Equal(2, await requested.Task.WaitAsync(ServiceTestHelpers.EventTimeout));
            var response = await responded.Task.WaitAsync(ServiceTestHelpers.EventTimeout);
            Assert.Equal(new MasterYieldResponse(3, true), response);
        }
        finally
        {
            Finder.MasterYieldRequested -= onRequested;
            Finder.MasterYieldResponded -= onResponded;
        }
    }

    [Fact]
    public async Task FaderStartCommandFiresOnceWithCorrectSets()
    {
        var commands = new List<FaderStartCommand>();
        EventHandler<FaderStartCommand> onFaderStart = (_, command) =>
        {
            lock (commands)
            {
                commands.Add(command);
            }
        };
        Finder.FaderStartCommanded += onFaderStart;
        try
        {
            var port = StartOnEphemeralPort();
            // Channel 1 starts, channel 2 stops, channel 3 is left alone, channel 4 starts.
            ServiceTestHelpers.SendToLoopback(port, FaderStartPacket(0, 1, 2, 0));
            await AwaitBarrierBeat(port);
            lock (commands)
            {
                var command = Assert.Single(commands);
                Assert.Equal(new[] { 1, 4 }, command.PlayersToStart.Order());
                Assert.Equal(new[] { 2 }, command.PlayersToStop.Order());
            }
        }
        finally
        {
            Finder.FaderStartCommanded -= onFaderStart;
        }
    }

    [Fact]
    public async Task IgnoredDeviceNamesFilterBeats()
    {
        DeviceFinder.Instance.AddIgnoredDeviceName("CDJ-900");
        var beats = new List<Beat>();
        EventHandler<Beat> onBeat = (_, beat) =>
        {
            if (beat.DeviceNumber != 15)  // Exclude the ordering-barrier beat sent below.
            {
                lock (beats)
                {
                    beats.Add(beat);
                }
            }
        };
        Finder.BeatReceived += onBeat;
        try
        {
            var port = StartOnEphemeralPort();
            ServiceTestHelpers.SendToLoopback(port, BeatPacket("CDJ-900", 4));
            ServiceTestHelpers.SendToLoopback(port, BeatPacket("CDJ-3000", 2));
            await AwaitBarrierBeat(port);
            lock (beats)
            {
                var beat = Assert.Single(beats);
                Assert.Equal("CDJ-3000", beat.DeviceName);
            }
        }
        finally
        {
            DeviceFinder.Instance.RemoveIgnoredDeviceName("CDJ-900");
            Finder.BeatReceived -= onBeat;
        }
    }

    private sealed class RecordingPriorityListener(string name, List<string> order) : IPriorityBeatListener
    {
        public void OnBeat(Beat beat)
        {
            lock (order)
            {
                order.Add(name);
            }
        }
    }
}
