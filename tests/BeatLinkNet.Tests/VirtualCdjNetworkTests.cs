using System.Net;
using System.Net.Sockets;

namespace BeatLink.Tests;

/// <summary>
/// Loopback smoke tests for the <see cref="VirtualCdj"/>: it must never touch the real DJ Link
/// ports (a live rekordbox instance may hold them), so every socket involved uses an ephemeral
/// port via the internal override seams, and the network interface scan is replaced by an
/// injected loopback match.
/// </summary>
[Collection("Network services")]
public sealed class VirtualCdjNetworkTests : IDisposable
{
    private static VirtualCdj Vcdj => VirtualCdj.Instance;

    private static DeviceFinder Finder => DeviceFinder.Instance;

    public VirtualCdjNetworkTests()
    {
        ResetState();
    }

    public void Dispose()
    {
        ResetState();
    }

    private static void ResetState()
    {
        Vcdj.ResetForTests();
        Finder.Stop();
        Finder.PortOverride = null;
        Finder.MaximumAgeMillis = DeviceFinder.MaximumAge;
        Finder.RemoveIgnoredAddress(IPAddress.Loopback);
        // Stale async lifecycle announcements from stopped services must finish before
        // the next test configures singleton state, or they can undo it mid-test.
        LifecycleParticipant.WaitForPendingAnnouncements(TimeSpan.FromSeconds(5));
    }

    /// <summary>
    /// Starts the <see cref="DeviceFinder"/> on an ephemeral port, announces a fake device to it,
    /// and waits until that device is visible, so <see cref="VirtualCdj.Start()"/> can proceed
    /// without its long device-discovery wait.
    /// </summary>
    private static void AnnounceFakeDevice(byte deviceNumber, string name = "CDJ-3000")
    {
        Finder.PortOverride = 0;  // Never bind the real announcement port; rekordbox may hold it.
        Finder.Start();
        var finderPort = Finder.LocalPort!.Value;
        var deadline = DateTime.UtcNow + ServiceTestHelpers.EventTimeout;
        while (true)
        {
            ServiceTestHelpers.SendToLoopback(finderPort, ServiceTestHelpers.KeepAlivePacket(name, deviceNumber));
            if (Finder.CurrentDevices.Any(device => device.DeviceNumber == deviceNumber))
            {
                return;
            }
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Fake device announcement never became visible.");
            }
            Thread.Sleep(20);
        }
    }

    /// <summary>
    /// Configures the loopback seams shared by these tests: our socket binds an ephemeral
    /// loopback port, announcement-family traffic goes to the supplied listener port, and the
    /// interface scan yields the loopback interface.
    /// </summary>
    private static void ConfigureLoopback(int announcementListenerPort)
    {
        Vcdj.PortOverride = 0;
        Vcdj.AnnouncementSendPort = announcementListenerPort;
        Vcdj.ClaimSleepMillis = 25;
        Vcdj.SelfAssignmentWatchMillis = 100;
        Vcdj.InterfaceScanOverride = _ => new MatchedAddressInfo(
            IPAddress.Loopback, 8, IPAddress.Loopback, VirtualCdjTestHelpers.TestMac);
    }

    /// <summary>
    /// Receives packets from the listener until one of the requested type arrives (or the
    /// timeout expires), returning it and recording every announcement-family packet type seen
    /// along the way.
    /// </summary>
    private static byte[] ReceiveUntil(UdpClient listener, byte typeByte, List<byte> typesSeen)
    {
        listener.Client.ReceiveTimeout = (int)ServiceTestHelpers.EventTimeout.TotalMilliseconds;
        var deadline = DateTime.UtcNow + ServiceTestHelpers.EventTimeout;
        var endpoint = new IPEndPoint(IPAddress.Any, 0);
        while (DateTime.UtcNow < deadline)
        {
            var packet = listener.Receive(ref endpoint);
            if (packet.Length > Util.PacketTypeOffset)
            {
                typesSeen.Add(packet[Util.PacketTypeOffset]);
                if (packet[Util.PacketTypeOffset] == typeByte)
                {
                    return packet;
                }
            }
        }
        throw new TimeoutException($"Never received a packet of type 0x{typeByte:x2}; saw: {string.Join(", ", typesSeen)}");
    }

    [Fact]
    public async Task StartsSelfAssignsNumberAndSendsKeepAlives()
    {
        AnnounceFakeDevice(2);

        using var listener = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var listenerPort = ((IPEndPoint)listener.Client.LocalEndPoint!).Port;
        ConfigureLoopback(listenerPort);

        Assert.True(Vcdj.Start());
        try
        {
            Assert.True(Vcdj.IsRunning);
            Assert.Equal(7, Vcdj.DeviceNumber);  // Self-assigned: first candidate above 6 not in use.
            Assert.Equal(IPAddress.Loopback, Vcdj.LocalAddress);
            Assert.Equal(IPAddress.Loopback, Vcdj.BroadcastAddress);
            Assert.True(Finder.IsAddressIgnored(IPAddress.Loopback));  // Our own announcements are filtered.
            Assert.Empty(Vcdj.FindUnreachablePlayers());
            Assert.Empty(Vcdj.MatchingInterfaces);  // Injected scan supplies no real interfaces.

            // The full startup conversation was emitted: hellos, then the three claim stages,
            // then keep-alives.
            var typesSeen = new List<byte>();
            var keepAlive = ReceiveUntil(listener, 0x06, typesSeen);
            Assert.Contains((byte)0x0a, typesSeen);  // Hello.
            Assert.Contains((byte)0x00, typesSeen);  // Claim stage 1.
            Assert.Contains((byte)0x02, typesSeen);  // Claim stage 2.
            Assert.Contains((byte)0x04, typesSeen);  // Claim stage 3.

            // Verify the keep-alive layout on the wire.
            Assert.Equal(0x36, keepAlive.Length);
            Assert.True(keepAlive.AsSpan(0, 10).SequenceEqual(Util.MagicHeader));
            Assert.Equal("beat-link", Util.DecodeAndTrim(keepAlive, 0x0c, 20));
            Assert.Equal(7, keepAlive[VirtualCdj.DeviceNumberOffset]);
            Assert.Equal(VirtualCdjTestHelpers.TestMac,
                keepAlive[VirtualCdj.MacAddressOffset..(VirtualCdj.MacAddressOffset + 6)]);
            Assert.Equal(IPAddress.Loopback.GetAddressBytes(), keepAlive[44..48]);

            // Status packets sent to our update socket are parsed, tracked, and delivered.
            var statusReceived = new TaskCompletionSource<DeviceUpdate>(TaskCreationOptions.RunContinuationsAsynchronously);
            EventHandler<DeviceUpdate> onUpdate = (_, update) => statusReceived.TrySetResult(update);
            Vcdj.DeviceUpdateReceived += onUpdate;
            try
            {
                var status = PacketFixtures.NexusCdjStatusPacket();
                status[0x21] = 2;
                ServiceTestHelpers.SendToLoopback(Vcdj.LocalPort!.Value, status);
                var update = await statusReceived.Task.WaitAsync(ServiceTestHelpers.EventTimeout);
                Assert.Equal(2, update.DeviceNumber);
                Assert.Same(update, Vcdj.GetLatestStatusFor(2));
            }
            finally
            {
                Vcdj.DeviceUpdateReceived -= onUpdate;
            }

            // Stopping the DeviceFinder must automatically stop the VirtualCdj.
            Finder.Stop();
            var deadline = DateTime.UtcNow + ServiceTestHelpers.EventTimeout;
            while (Vcdj.IsRunning && DateTime.UtcNow < deadline)
            {
                Thread.Sleep(20);
            }
            Assert.False(Vcdj.IsRunning);
            Assert.Equal(0, Vcdj.DeviceNumber);  // Reset for self-assignment on restart.
        }
        finally
        {
            Vcdj.ResetForTests();
        }
    }

    [Fact]
    public async Task ClaimOfSpecificNumberFailsWhenDefended()
    {
        AnnounceFakeDevice(2);

        using var listener = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var listenerPort = ((IPEndPoint)listener.Client.LocalEndPoint!).Port;
        ConfigureLoopback(listenerPort);
        Vcdj.ClaimSleepMillis = 50;
        Vcdj.DeviceNumber = 7;

        var startTask = Task.Run(Vcdj.Start);
        try
        {
            // Wait until the claim conversation begins (first claim stage 1 packet), then inject
            // a defense of the number we are trying to claim, as the DeviceFinder would when
            // another device protests.
            var typesSeen = new List<byte>();
            ReceiveUntil(listener, 0x00, typesSeen);
            var inUse = PacketFixtures.NewPacket(0x29, 0x08, "CDJ-3000", 0x0c);
            inUse[0x24] = 7;
            Vcdj.HandleSpecialAnnouncementPacket(PacketType.DeviceNumberInUse, inUse,
                VirtualCdjTestHelpers.AddressFor(9));

            // Since we requested a specific number, the rejected claim fails the whole startup.
            Assert.False(await startTask.WaitAsync(ServiceTestHelpers.EventTimeout));
            Assert.False(Vcdj.IsRunning);
        }
        finally
        {
            try
            {
                await startTask.WaitAsync(ServiceTestHelpers.EventTimeout);
            }
            catch (TimeoutException)
            {
                // Covered by the assertions above; just make sure startup finished before reset.
            }
            Vcdj.ResetForTests();
        }
    }
}
