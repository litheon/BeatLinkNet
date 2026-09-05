using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace BeatLink.Tests;

/// <summary>
/// Shared helpers for the loopback-based service tests.
/// </summary>
internal static class ServiceTestHelpers
{
    internal static readonly TimeSpan EventTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Sends a synthetic packet to a service listening on the loopback interface.
    /// </summary>
    internal static void SendToLoopback(int port, byte[] packet)
    {
        using var client = new UdpClient();
        client.Send(packet, packet.Length, new IPEndPoint(IPAddress.Loopback, port));
    }

    /// <summary>
    /// Builds a device keep-alive (announcement) packet as broadcast on port 50000.
    /// </summary>
    internal static byte[] KeepAlivePacket(string name, byte deviceNumber)
    {
        var bytes = PacketFixtures.NewPacket(0x36, 0x06, name, 0x0c);
        bytes[0x24] = deviceNumber;
        new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06 }.CopyTo(bytes, 0x26);
        bytes[0x30] = 1;  // peer count
        return bytes;
    }
}

/// <summary>
/// Exercises the <see cref="DeviceFinder"/> singleton over loopback UDP. These tests share mutable
/// singleton state with the <see cref="BeatFinderTests"/>, so both classes are placed in the same
/// xUnit collection to keep them from running in parallel.
/// </summary>
[Collection("Network services")]
public sealed class DeviceFinderTests : IDisposable
{
    private static DeviceFinder Finder => DeviceFinder.Instance;

    public DeviceFinderTests()
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
        Finder.MaximumAgeMillis = DeviceFinder.MaximumAge;
        Finder.RemoveIgnoredAddress(IPAddress.Loopback);
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
            Assert.Throws<InvalidOperationException>(() => Finder.StartTime);

            var port = StartOnEphemeralPort();
            Assert.True(Finder.IsRunning);
            Assert.True(Finder.StartTime > 0);
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
            Assert.Throws<InvalidOperationException>(() => Finder.CurrentDevices);
            Assert.Throws<InvalidOperationException>(() => Finder.FirstDeviceTime);

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
    public async Task DeviceFoundFiresAndDeviceIsTracked()
    {
        var found = new TaskCompletionSource<DeviceAnnouncement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var foundCount = 0;
        var barrierFound = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<DeviceAnnouncement> onFound = (_, announcement) =>
        {
            if (announcement.DeviceNumber == 3)
            {
                Interlocked.Increment(ref foundCount);
                found.TrySetResult(announcement);
            }
            else if (announcement.DeviceNumber == 4)
            {
                barrierFound.TrySetResult();
            }
        };
        Finder.DeviceFound += onFound;
        try
        {
            var port = StartOnEphemeralPort();
            ServiceTestHelpers.SendToLoopback(port, ServiceTestHelpers.KeepAlivePacket("CDJ-2000nexus", 3));

            var announcement = await found.Task.WaitAsync(ServiceTestHelpers.EventTimeout);
            Assert.Equal("CDJ-2000nexus", announcement.DeviceName);
            Assert.Equal(3, announcement.DeviceNumber);
            Assert.Equal(IPAddress.Loopback, announcement.Address);

            var current = Finder.CurrentDevices;
            Assert.Single(current);
            Assert.Equal(3, current.Single().DeviceNumber);
            Assert.NotNull(Finder.GetLatestAnnouncementFrom(3));
            Assert.Null(Finder.GetLatestAnnouncementFrom(4));
            Assert.True(Finder.FirstDeviceTime > 0);

            // An old CDJ with a low device number counts as metadata-limited.
            Assert.True(Finder.IsAnyDeviceLimitedToThreeDatabaseClients);

            // A repeated announcement from the same device must not fire the event again; a
            // subsequent announcement from a different device acts as an ordering barrier.
            ServiceTestHelpers.SendToLoopback(port, ServiceTestHelpers.KeepAlivePacket("CDJ-2000nexus", 3));
            ServiceTestHelpers.SendToLoopback(port, ServiceTestHelpers.KeepAlivePacket("CDJ-3000", 4));
            await barrierFound.Task.WaitAsync(ServiceTestHelpers.EventTimeout);
            Assert.Equal(1, Volatile.Read(ref foundCount));
            Assert.Equal(2, Finder.CurrentDevices.Count);
        }
        finally
        {
            Finder.DeviceFound -= onFound;
        }
    }

    [Fact]
    public async Task DeviceLostFiresAfterExpiry()
    {
        Finder.MaximumAgeMillis = 200;
        var found = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lost = new TaskCompletionSource<DeviceAnnouncement>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<DeviceAnnouncement> onFound = (_, _) => found.TrySetResult();
        EventHandler<DeviceAnnouncement> onLost = (_, announcement) => lost.TrySetResult(announcement);
        Finder.DeviceFound += onFound;
        Finder.DeviceLost += onLost;
        try
        {
            var port = StartOnEphemeralPort();
            ServiceTestHelpers.SendToLoopback(port, ServiceTestHelpers.KeepAlivePacket("CDJ-2000nexus", 3));
            await found.Task.WaitAsync(ServiceTestHelpers.EventTimeout);
            Assert.True(Finder.IsAnyDeviceLimitedToThreeDatabaseClients);

            var expired = await lost.Task.WaitAsync(ServiceTestHelpers.EventTimeout);
            Assert.Equal(3, expired.DeviceNumber);
            Assert.Empty(Finder.CurrentDevices);
            Assert.Throws<InvalidOperationException>(() => Finder.FirstDeviceTime);
            Assert.False(Finder.IsAnyDeviceLimitedToThreeDatabaseClients);
        }
        finally
        {
            Finder.DeviceFound -= onFound;
            Finder.DeviceLost -= onLost;
        }
    }

    [Fact]
    public async Task StoppingReportsAllDevicesLost()
    {
        var found = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lost = new TaskCompletionSource<DeviceAnnouncement>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<DeviceAnnouncement> onFound = (_, _) => found.TrySetResult();
        EventHandler<DeviceAnnouncement> onLost = (_, announcement) => lost.TrySetResult(announcement);
        Finder.DeviceFound += onFound;
        Finder.DeviceLost += onLost;
        try
        {
            var port = StartOnEphemeralPort();
            ServiceTestHelpers.SendToLoopback(port, ServiceTestHelpers.KeepAlivePacket("CDJ-3000", 2));
            await found.Task.WaitAsync(ServiceTestHelpers.EventTimeout);

            Finder.Stop();
            var flushed = await lost.Task.WaitAsync(ServiceTestHelpers.EventTimeout);
            Assert.Equal(2, flushed.DeviceNumber);
        }
        finally
        {
            Finder.DeviceFound -= onFound;
            Finder.DeviceLost -= onLost;
        }
    }

    [Fact]
    public async Task IgnoredDeviceNamesAreIgnored()
    {
        Finder.AddIgnoredDeviceName("TCS-SHOWKONTROL");
        var found = new TaskCompletionSource<DeviceAnnouncement>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<DeviceAnnouncement> onFound = (_, announcement) => found.TrySetResult(announcement);
        Finder.DeviceFound += onFound;
        try
        {
            Assert.True(Finder.IsDeviceNameIgnored("TCS-SHOWKONTROL"));
            Assert.False(Finder.IsDeviceNameIgnored("CDJ-3000"));

            var port = StartOnEphemeralPort();
            // The ignored device is sent first; the allowed one afterwards serves as an ordering
            // barrier, since the single receive thread processes packets in order.
            ServiceTestHelpers.SendToLoopback(port, ServiceTestHelpers.KeepAlivePacket("TCS-SHOWKONTROL", 5));
            ServiceTestHelpers.SendToLoopback(port, ServiceTestHelpers.KeepAlivePacket("CDJ-3000", 2));

            var announcement = await found.Task.WaitAsync(ServiceTestHelpers.EventTimeout);
            Assert.Equal("CDJ-3000", announcement.DeviceName);
            Assert.Single(Finder.CurrentDevices);
            Assert.Null(Finder.GetLatestAnnouncementFrom(5));

            Finder.RemoveIgnoredDeviceName("TCS-SHOWKONTROL");
            Assert.False(Finder.IsDeviceNameIgnored("TCS-SHOWKONTROL"));
        }
        finally
        {
            Finder.RemoveIgnoredDeviceName("TCS-SHOWKONTROL");
            Finder.DeviceFound -= onFound;
        }
    }

    [Fact]
    public async Task IgnoredAddressesAreIgnored()
    {
        var found = new TaskCompletionSource<DeviceAnnouncement>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<DeviceAnnouncement> onFound = (_, announcement) => found.TrySetResult(announcement);
        Finder.DeviceFound += onFound;
        try
        {
            Finder.AddIgnoredAddress(IPAddress.Loopback);
            Assert.True(Finder.IsAddressIgnored(IPAddress.Loopback));
            Assert.True(Finder.IsAddressIgnored(IPAddress.Loopback.MapToIPv6()));

            var port = StartOnEphemeralPort();
            ServiceTestHelpers.SendToLoopback(port, ServiceTestHelpers.KeepAlivePacket("CDJ-3000", 2));
            // Give the packet time to arrive and be (silently) dropped before checking. This is an
            // inherently negative assertion, so a short delay is unavoidable.
            await Task.Delay(500);
            Assert.Empty(Finder.CurrentDevices);

            Finder.RemoveIgnoredAddress(IPAddress.Loopback);
            Assert.False(Finder.IsAddressIgnored(IPAddress.Loopback));
            ServiceTestHelpers.SendToLoopback(port, ServiceTestHelpers.KeepAlivePacket("CDJ-3000", 2));
            var announcement = await found.Task.WaitAsync(ServiceTestHelpers.EventTimeout);
            Assert.Equal(2, announcement.DeviceNumber);
        }
        finally
        {
            Finder.RemoveIgnoredAddress(IPAddress.Loopback);
            Finder.DeviceFound -= onFound;
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
    public async Task OpusAnnouncementsFanOutToFourDevices()
    {
        var foundNumbers = new ConcurrentQueue<int>();
        var allFound = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<DeviceAnnouncement> onFound = (_, announcement) =>
        {
            foundNumbers.Enqueue(announcement.DeviceNumber);
            if (foundNumbers.Count >= 4)
            {
                allFound.TrySetResult();
            }
        };
        Finder.DeviceFound += onFound;
        try
        {
            var port = StartOnEphemeralPort();
            ServiceTestHelpers.SendToLoopback(port, ServiceTestHelpers.KeepAlivePacket("OPUS-QUAD", 9));

            await allFound.Task.WaitAsync(ServiceTestHelpers.EventTimeout);
            Assert.Equal(new[] { 1, 2, 3, 4 }, foundNumbers.OrderBy(n => n));
            var current = Finder.CurrentDevices;
            Assert.Equal(4, current.Count);
            Assert.All(current, announcement => Assert.True(announcement.IsOpusQuad));
        }
        finally
        {
            Finder.DeviceFound -= onFound;
        }
    }

    [Fact]
    public void UnwrapsV6WrappedV4Addresses()
    {
        Assert.Equal(IPAddress.Parse("192.168.1.5"),
            Finder.UnwrapV6MappedV4Address(IPAddress.Parse("::ffff:192.168.1.5")));
        Assert.Equal(IPAddress.Parse("10.1.2.3"),
            Finder.UnwrapV6MappedV4Address(IPAddress.Parse("::10.1.2.3")));
        var regularV6 = IPAddress.Parse("2001:db8::1");
        Assert.Equal(regularV6, Finder.UnwrapV6MappedV4Address(regularV6));
        Assert.Equal(IPAddress.Loopback, Finder.UnwrapV6MappedV4Address(IPAddress.Loopback));
    }

    [Fact]
    public void ReportsMetadataLimitedDevices()
    {
        Assert.True(Finder.IsDeviceMetadataLimited(
            new DeviceAnnouncement(ServiceTestHelpers.KeepAlivePacket("CDJ-2000nexus", 3), PacketFixtures.Address)));
        Assert.False(Finder.IsDeviceMetadataLimited(
            new DeviceAnnouncement(ServiceTestHelpers.KeepAlivePacket("CDJ-3000", 2), PacketFixtures.Address)));
        Assert.False(Finder.IsDeviceMetadataLimited(
            new DeviceAnnouncement(ServiceTestHelpers.KeepAlivePacket("DJM-900nexus", 33), PacketFixtures.Address)));
        Assert.Contains("CDJ-3000", (IReadOnlySet<string>)DeviceFinder.MetadataFlexibleDevices);
        Assert.Contains("XDJ-AZ", (IReadOnlySet<string>)DeviceFinder.MetadataFlexibleDevices);
    }
}
