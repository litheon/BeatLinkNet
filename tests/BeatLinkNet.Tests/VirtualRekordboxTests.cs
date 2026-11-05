// These tests exercise the experimental Opus Quad API surface deliberately.
#pragma warning disable BLNET001

using System.Net;
using System.Net.Sockets;
using BeatLink.Data;
using BeatLink.Tests.CrateDigger;

namespace BeatLink.Tests;

/// <summary>
/// Exercises the <see cref="VirtualRekordbox"/> rekordbox-lighting emulation: golden tests for
/// the packets it sends (built from the Java template offsets), parsing of Opus Quad status
/// packets with player-number translation and status-flag recovery, multi-packet OPUS_METADATA
/// (PSSI) reassembly against an attached metadata archive, and a loopback smoke test of the full
/// <see cref="VirtualCdj"/> proxy mode. No real DJ Link port is ever touched.
/// </summary>
[Collection("Network services")]
public sealed class VirtualRekordboxTests : IDisposable
{
    private static VirtualRekordbox Vrb => VirtualRekordbox.Instance;

    private static VirtualCdj Vcdj => VirtualCdj.Instance;

    private static DeviceFinder Finder => DeviceFinder.Instance;

    private readonly List<string> _tempFiles = [];

    public VirtualRekordboxTests()
    {
        ResetState();
    }

    public void Dispose()
    {
        ResetState();
        foreach (var file in _tempFiles)
        {
            File.Delete(file);
        }
    }

    private static void ResetState()
    {
        Vrb.ResetForTests();
        Vcdj.ResetForTests();
        OpusProvider.Instance.ResetForTests();
        OpusProvider.DatabaseKey = null;
        MetadataFinder.Instance.Stop();
        BeatFinder.Instance.Stop();
        BeatFinder.Instance.PortOverride = null;
        Finder.Stop();
        Finder.PortOverride = null;
        Finder.RemoveIgnoredAddress(IPAddress.Loopback);
        // Stale async lifecycle announcements from stopped services must finish before
        // the next test configures singleton state, or they can undo it mid-test.
        LifecycleParticipant.WaitForPendingAnnouncements(TimeSpan.FromSeconds(5));
    }

    /// <summary>
    /// Builds a plausible Opus Quad CDJ status packet (device name "OPUS-QUAD", raw device
    /// numbers 9-12) for the update port.
    /// </summary>
    private static byte[] OpusStatusPacket(byte rawDevice, int rekordboxId, byte statusFlags)
    {
        var bytes = PacketFixtures.NewPacket(0xd4, 0x0a, "OPUS-QUAD", 0x0b);
        bytes[0x21] = rawDevice;
        Util.NumberToBytes(0xd4 - 0x24, bytes, 0x22, 2);       // Payload length.
        bytes[0x28] = rawDevice;                               // Track source: this Opus deck.
        bytes[0x2a] = 1;                                       // Track type: rekordbox.
        Util.NumberToBytes(rekordboxId, bytes, 0x2c, 4);
        bytes[CdjStatus.StatusFlags] = statusFlags;
        Util.NumberToBytes(0x100000, bytes, 0x8d, 3);          // Neutral pitch.
        Util.NumberToBytes(12000, bytes, 0x92, 2);             // 120 BPM.
        return bytes;
    }

    /// <summary>
    /// Builds one packet of a multi-packet OPUS_METADATA PSSI response.
    /// </summary>
    private static byte[] OpusMetadataPacket(byte rawPlayer, int rekordboxId, byte packetNumber,
        byte totalPacketsPlusOne, byte[] chunk)
    {
        var bytes = PacketFixtures.NewPacket(0x34 + chunk.Length, 0x56, "OPUS-QUAD", 0x0b);
        bytes[0x21] = rawPlayer;
        bytes[0x25] = 10;  // Metadata type identifier: PSSI.
        Util.NumberToBytes(rekordboxId, bytes, 0x28, 4);
        bytes[0x31] = packetNumber;
        bytes[0x33] = totalPacketsPlusOne;
        chunk.CopyTo(bytes, 0x34);
        return bytes;
    }

    [Fact]
    public void KeepAlivePacketMatchesJavaTemplate()
    {
        var packet = Vrb.BuildKeepAlivePacket();
        Assert.Equal(0x36, packet.Length);
        Assert.True(packet.AsSpan(0, 10).SequenceEqual(Util.MagicHeader));
        Assert.Equal(0x06, packet[Util.PacketTypeOffset]);  // Device keep-alive.
        Assert.Equal(0x00, packet[0x0b]);
        Assert.Equal("rekordbox", Util.DecodeAndTrim(packet, VirtualRekordbox.DeviceNameOffset, VirtualRekordbox.DeviceNameLength));
        Assert.Equal("rekordbox", Vrb.DeviceName);
        Assert.Equal(0x01, packet[0x20]);
        Assert.Equal(0x03, packet[0x21]);  // Device type: rekordbox.
        Assert.Equal(0x00, packet[0x22]);
        Assert.Equal(0x36, packet[0x23]);  // Payload length.
        Assert.Equal(0x17, packet[VirtualRekordbox.DeviceNumberOffset]);  // Default virtual rekordbox number.
        Assert.Equal(0x17, Vrb.DeviceNumber);
        // The template MAC and IP address placeholders, replaced during startup.
        Assert.Equal(new byte[] { 0x18, 0x3e, 0xef, 0xda, 0x5b, 0xca }, packet[VirtualRekordbox.MacAddressOffset..(VirtualRekordbox.MacAddressOffset + 6)]);
        Assert.Equal(new byte[] { 0xc0, 0xa8, 0x02, 0x0b }, packet[44..48]);
        Assert.Equal(new byte[] { 0x04, 0x01, 0x00, 0x00, 0x04, 0x08 }, packet[48..]);
    }

    [Fact]
    public void LightingRequestPacketMatchesJavaTemplate()
    {
        var packet = Vrb.BuildLightingRequestPacket();
        Assert.Equal(296, packet.Length);
        Assert.True(packet.AsSpan(0, 10).SequenceEqual(Util.MagicHeader));
        Assert.Equal(0x11, packet[Util.PacketTypeOffset]);  // Lighting request announcement.
        Assert.Equal("rekordbox", Util.DecodeAndTrim(packet, 0x0b, 0x14));
        Assert.Equal(0x01, packet[0x1f]);
        Assert.Equal(0x01, packet[0x20]);
        Assert.Equal(0x17, packet[0x21]);
        Assert.Equal(0x01, packet[0x22]);
        Assert.Equal(0x04, packet[0x23]);
        Assert.Equal(0x17, packet[0x24]);
        Assert.Equal(0x01, packet[0x25]);
        // The hard-coded UTF-16BE computer name, "macbook pro".
        Assert.Equal("macbook pro", System.Text.Encoding.BigEndianUnicode.GetString(packet, 0x28, 22));
    }

    [Fact]
    public void RequestPssiPacketMatchesJavaTemplate()
    {
        var packet = VirtualRekordbox.RequestPssiPacketForTests;
        Assert.Equal(44, packet.Length);
        Assert.True(packet.AsSpan(0, 10).SequenceEqual(Util.MagicHeader));
        Assert.Equal(0x55, packet[Util.PacketTypeOffset]);
        Assert.Equal("rekordbox", Util.DecodeAndTrim(packet, 0x0b, 0x14));
        Assert.Equal(new byte[] { 0x00, 0x17, 0x00, 0x08, 0x36, 0x00, 0x00, 0x00, 0x0a, 0x02, 0x03, 0x01 },
            packet[0x20..]);
    }

    [Fact]
    public void StatusParsingTranslatesPlayerNumbersAndRecoversStatusFlags()
    {
        var address = PacketFixtures.Address;

        // Raw Opus device numbers 9 through 12 are reported as players 1 through 4.
        for (byte raw = 9; raw <= 12; raw++)
        {
            var update = Vrb.BuildUpdate(OpusStatusPacket(raw, 42, 0x84), 0xd4, address);
            var status = Assert.IsType<CdjStatus>(update);
            Assert.Equal(raw - 8, status.DeviceNumber);
            Assert.True(status.IsFromOpusQuad);
            Assert.False(status.StatusFlagsWereReplayed);
            // With no metadata archive matched yet, the source is reported as the (translated)
            // deck's USB slot with no known rekordbox ID.
            Assert.Equal(raw - 8, status.TrackSourcePlayer);
            Assert.Same(CdjStatus.TrackSourceSlot.UsbSlot, status.SourceSlot);
            Assert.Equal(0, status.RekordboxId);
        }

        // A zero status-flag byte from a device we have never heard from is discarded.
        Assert.Null(Vrb.BuildUpdate(OpusStatusPacket(13, 42, 0), 0xd4, address));

        // But once a valid packet has been seen, the last valid flags are replayed.
        var recovered = Assert.IsType<CdjStatus>(Vrb.BuildUpdate(OpusStatusPacket(9, 42, 0), 0xd4, address));
        Assert.True(recovered.StatusFlagsWereReplayed);
        Assert.Equal(0x84, recovered.PacketBytes.Span[CdjStatus.StatusFlags]);

        // Truncated packets are rejected.
        Assert.Null(Vrb.BuildUpdate(OpusStatusPacket(9, 42, 0x84), 0x40, address));
    }

    [Fact]
    public void OpusMetadataReassemblyMatchesTracksAcrossPackets()
    {
        // Attach an archive whose extended analysis holds a synthetic masked PSSI (whose raw body
        // ends with a non-zero byte, so trailing-zero trimming of the padded final packet is
        // lossless), indexed for tracks 1 and 2.
        var (extBytes, rawBody) = OpusTestHelpers.BuildSyntheticPssiExt();
        var archive = OpusTestHelpers.BuildArchive(
            ("export.pdb", PdbBuilder.BuildExportFile(analyzePath: "/PIONEER/USBANLZ/SYNTH000.DAT")),
            ("PIONEER/USBANLZ/SYNTH000.EXT", extBytes));
        _tempFiles.Add(archive);
        Vcdj.SetOpusQuadCompatibilityModeForTests(true);
        OpusProvider.Instance.Start();
        OpusProvider.Instance.AttachMetadataArchive(archive, 3);

        // The complete PSSI as the Opus would send it: the full tagged section (12-byte header
        // plus raw body), split across two packets, with the final packet zero-padded.
        var pssi = new byte[12 + rawBody.Length];
        rawBody.CopyTo(pssi, 12);
        var chunk1 = pssi[..20];
        var chunk2 = pssi[20..].Concat(new byte[8]).ToArray();  // Simulated fixed-size padding.

        // Raw player 11 is deck 3. After only the first packet, no match has been made.
        Assert.Null(Vrb.BuildUpdate(OpusMetadataPacket(11, 2, 1, 3, chunk1), 0x34 + chunk1.Length, PacketFixtures.Address));
        Assert.Equal(0, Vrb.FindDeviceSqlRekordboxIdForPlayer(3));
        Assert.Null(Vrb.FindMatchedTrackSourceSlotForPlayer(3));

        // The final packet completes the message; both tracks share the PSSI, and the ID the Opus
        // reported (2) disambiguates.
        Assert.Null(Vrb.BuildUpdate(OpusMetadataPacket(11, 2, 2, 3, chunk2), 0x34 + chunk2.Length, PacketFixtures.Address));
        Assert.Equal(2, Vrb.FindDeviceSqlRekordboxIdForPlayer(3));
        Assert.Same(SlotReference.GetSlotReference(3, CdjStatus.TrackSourceSlot.UsbSlot),
            Vrb.FindMatchedTrackSourceSlotForPlayer(3));

        // A status packet for that deck now reports the matched slot and DeviceSQL rekordbox ID.
        var status = Assert.IsType<CdjStatus>(Vrb.BuildUpdate(OpusStatusPacket(11, 2, 0x84), 0xd4, PacketFixtures.Address));
        Assert.Equal(3, status.TrackSourcePlayer);  // The USB slot whose archive matched.
        Assert.Equal(2, status.RekordboxId);

        // When the deck loads a different track, the stale match is forgotten.
        Assert.NotNull(Vrb.BuildUpdate(OpusStatusPacket(11, 7, 0x84), 0xd4, PacketFixtures.Address));
        Assert.Equal(0, Vrb.FindDeviceSqlRekordboxIdForPlayer(3));
        Assert.Null(Vrb.FindMatchedTrackSourceSlotForPlayer(3));

        // Archive changes clear the per-slot cache the same way.
        Assert.Null(Vrb.BuildUpdate(OpusMetadataPacket(11, 2, 1, 2, pssi), 0x34 + pssi.Length, PacketFixtures.Address));
        Assert.NotNull(Vrb.FindMatchedTrackSourceSlotForPlayer(3));
        Vrb.ClearPlayerCaches(3);
        Assert.Null(Vrb.FindMatchedTrackSourceSlotForPlayer(3));
    }

    [Fact]
    public async Task ProxyModeStartsVirtualRekordboxAndForwardsStatus()
    {
        // Announce a fake Opus Quad to the DeviceFinder (which fans it out into four deck
        // announcements) on an ephemeral loopback port.
        Finder.PortOverride = 0;
        Finder.Start();
        var finderPort = Finder.LocalPort!.Value;
        var deadline = DateTime.UtcNow + ServiceTestHelpers.EventTimeout;
        while (Finder.CurrentDevices.Count == 0)
        {
            ServiceTestHelpers.SendToLoopback(finderPort, ServiceTestHelpers.KeepAlivePacket("OPUS-QUAD", 9));
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Fake Opus Quad announcement never became visible.");
            }
            Thread.Sleep(20);
        }

        // Route all the VirtualRekordbox traffic through loopback seams.
        using var listener = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var listenerPort = ((IPEndPoint)listener.Client.LocalEndPoint!).Port;
        Vrb.PortOverride = 0;
        Vrb.AnnouncementSendPort = listenerPort;
        Vrb.UpdateSendPort = listenerPort;
        Vrb.SelfAssignmentWatchMillis = 100;
        Vrb.InterfaceScanOverride = _ => new MatchedAddressInfo(
            IPAddress.Loopback, 8, IPAddress.Loopback, VirtualCdjTestHelpers.TestMac);

        // Starting the VirtualCdj with an Opus Quad on the network (and no database key) must
        // route through the rekordbox lighting proxy mode instead of opening its own socket.
        Assert.True(Vcdj.Start());
        try
        {
            Assert.True(Vcdj.InOpusQuadCompatibilityMode);
            Assert.True(Vcdj.IsRunning);
            Assert.True(Vrb.IsRunning);
            Assert.True(OpusProvider.Instance.IsRunning);
            Assert.Equal(IPAddress.Loopback, Vcdj.LocalAddress);       // Proxied from VirtualRekordbox.
            Assert.Equal(IPAddress.Loopback, Vcdj.BroadcastAddress);
            Assert.Equal(IPAddress.Loopback, Vrb.LocalAddress);
            Assert.Equal(0x17, Vrb.DeviceNumber);  // The default number was free (decks are 1-4).

            // Both announcement packets are emitted: the rekordbox keep-alive (with our MAC and
            // IP patched in) and the lighting status request.
            listener.Client.ReceiveTimeout = (int)ServiceTestHelpers.EventTimeout.TotalMilliseconds;
            var endpoint = new IPEndPoint(IPAddress.Any, 0);
            byte[]? keepAlive = null;
            byte[]? lighting = null;
            var receiveDeadline = DateTime.UtcNow + ServiceTestHelpers.EventTimeout;
            while ((keepAlive == null || lighting == null) && DateTime.UtcNow < receiveDeadline)
            {
                var packet = listener.Receive(ref endpoint);
                if (packet.Length > Util.PacketTypeOffset)
                {
                    switch (packet[Util.PacketTypeOffset])
                    {
                        case 0x06:
                            keepAlive = packet;
                            break;
                        case 0x11:
                            lighting = packet;
                            break;
                    }
                }
            }
            Assert.NotNull(keepAlive);
            Assert.NotNull(lighting);
            Assert.Equal("rekordbox", Util.DecodeAndTrim(keepAlive, VirtualRekordbox.DeviceNameOffset, VirtualRekordbox.DeviceNameLength));
            Assert.Equal(0x17, keepAlive[VirtualRekordbox.DeviceNumberOffset]);
            Assert.Equal(VirtualCdjTestHelpers.TestMac,
                keepAlive[VirtualRekordbox.MacAddressOffset..(VirtualRekordbox.MacAddressOffset + 6)]);
            Assert.Equal(IPAddress.Loopback.GetAddressBytes(), keepAlive[44..48]);
            Assert.Equal(VirtualCdjTestHelpers.TestMac,
                lighting[VirtualRekordbox.MacAddressOffset..(VirtualRekordbox.MacAddressOffset + 6)]);
            Assert.Equal(IPAddress.Loopback.GetAddressBytes(), lighting[44..48]);

            // A status packet from the Opus sent to our socket is parsed, translated, and
            // forwarded into the VirtualCdj's processing, where clients expect to see it.
            var statusReceived = new TaskCompletionSource<DeviceUpdate>(TaskCreationOptions.RunContinuationsAsynchronously);
            EventHandler<DeviceUpdate> onUpdate = (_, update) => statusReceived.TrySetResult(update);
            Vcdj.DeviceUpdateReceived += onUpdate;
            try
            {
                ServiceTestHelpers.SendToLoopback(Vrb.LocalPort!.Value, OpusStatusPacket(9, 42, 0x84));
                var update = await statusReceived.Task.WaitAsync(ServiceTestHelpers.EventTimeout);
                Assert.Equal(1, update.DeviceNumber);  // Translated from raw device 9.
                Assert.Same(update, Vrb.GetLatestStatusFor(1));
                Assert.Same(update, Vcdj.GetLatestStatusFor(1));
            }
            finally
            {
                Vcdj.DeviceUpdateReceived -= onUpdate;
            }

            // Stopping the VirtualCdj tears the whole proxy stack down.
            Vcdj.Stop();
            Assert.False(Vcdj.IsRunning);
            Assert.False(Vrb.IsRunning);
            Assert.False(OpusProvider.Instance.IsRunning);
            Assert.Equal(0, Vrb.DeviceNumber);  // Reset for self-assignment on restart.
        }
        finally
        {
            Vcdj.ResetForTests();
        }
    }
}
