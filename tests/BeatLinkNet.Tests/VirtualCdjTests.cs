using System.Net;
using System.Text;
using BeatLink.Electro;

namespace BeatLink.Tests;

/// <summary>
/// Shared helpers for the <see cref="VirtualCdj"/> tests: state reset and synthetic packet
/// construction, with all offsets taken from the Java beat-link source.
/// </summary>
internal static class VirtualCdjTestHelpers
{
    internal static readonly byte[] TestMac = [0x11, 0x22, 0x33, 0x44, 0x55, 0x66];

    internal static readonly IPAddress TestAddress = IPAddress.Parse("192.168.1.5");

    /// <summary>
    /// Installs a fabricated matched-interface configuration so packet builders that need our
    /// MAC/IP can run without any sockets.
    /// </summary>
    internal static void InstallTestNetworkDetails() =>
        VirtualCdj.Instance.SetNetworkDetailsForTests(new MatchedAddressInfo(
            TestAddress, 24, IPAddress.Parse("192.168.1.255"), TestMac));

    /// <summary>
    /// Builds a synthetic CDJ status packet, parsed into a <see cref="CdjStatus"/>, with the
    /// fields relevant to the tempo master state machine under direct control.
    /// </summary>
    internal static CdjStatus MakeStatus(int deviceNumber, bool master, int bpm100 = 12850,
        byte yieldingTo = 0xff, int syncNumber = 5)
    {
        var bytes = PacketFixtures.NexusCdjStatusPacket();
        bytes[0x21] = (byte)deviceNumber;
        bytes[0x89] = (byte)(0x40 | 0x10 | (master ? 0x20 : 0));  // playing, synced, maybe master
        bytes[0x9f] = yieldingTo;
        Util.NumberToBytes(syncNumber, bytes, 0x84, 4);
        Util.NumberToBytes(bpm100, bytes, 0x92, 2);
        return new CdjStatus(bytes, AddressFor(deviceNumber));
    }

    /// <summary>
    /// Builds a synthetic beat packet, parsed into a <see cref="Beat"/>, sent from the address
    /// used by <see cref="MakeStatus"/> for the same device number.
    /// </summary>
    internal static Beat MakeBeat(int deviceNumber, int bpm100)
    {
        var bytes = PacketFixtures.NewPacket(0x60, 0x28, "CDJ-2000nexus", 0x0b);
        bytes[0x21] = (byte)deviceNumber;
        Util.NumberToBytes(Util.NeutralPitch, bytes, 0x55, 3);
        Util.NumberToBytes(bpm100, bytes, 0x5a, 2);
        bytes[0x5c] = 1;
        return new Beat(bytes, AddressFor(deviceNumber));
    }

    /// <summary>
    /// A distinct, deterministic source address for each synthetic device.
    /// </summary>
    internal static IPAddress AddressFor(int deviceNumber) => IPAddress.Parse($"192.168.1.{100 + deviceNumber}");
}

/// <summary>
/// Byte-level golden tests for every packet the <see cref="VirtualCdj"/> can construct, asserting
/// layouts against the offsets used by the Java implementation. No sockets are involved. Shares
/// the "Network services" collection because the <see cref="VirtualCdj"/> singleton's mutable
/// state must not be exercised concurrently with the other service tests.
/// </summary>
[Collection("Network services")]
public sealed class VirtualCdjPacketTests : IDisposable
{
    private static VirtualCdj Vcdj => VirtualCdj.Instance;

    public VirtualCdjPacketTests()
    {
        Vcdj.ResetForTests();
        VirtualCdjTestHelpers.InstallTestNetworkDetails();
    }

    public void Dispose() => Vcdj.ResetForTests();

    private static void AssertHeaderAndName(byte[] packet, byte typeByte, int nameOffset)
    {
        Assert.True(packet.AsSpan(0, 10).SequenceEqual(Util.MagicHeader));
        Assert.Equal(typeByte, packet[0x0a]);
        Assert.Equal("beat-link", Util.DecodeAndTrim(packet, nameOffset, 20));
    }

    [Fact]
    public void BuildsHelloPacket()
    {
        var packet = Vcdj.BuildHelloPacket();
        Assert.Equal(0x26, packet.Length);
        AssertHeaderAndName(packet, 0x0a, 0x0c);
        Assert.Equal(new byte[] { 0x01, 0x04, 0x00, 0x26, 0x01, 0x40 }, packet[0x20..0x26]);
    }

    [Fact]
    public void BuildsClaimStage1Packet()
    {
        var packet = Vcdj.BuildClaimStage1Packet(2);
        Assert.Equal(0x2c, packet.Length);
        AssertHeaderAndName(packet, 0x00, 0x0c);
        Assert.Equal(new byte[] { 0x01, 0x03, 0x00, 0x2c }, packet[0x20..0x24]);
        Assert.Equal(2, packet[0x24]);      // The packet counter overwrites the template's 0x0d.
        Assert.Equal(1, packet[0x25]);
        Assert.Equal(VirtualCdjTestHelpers.TestMac, packet[0x26..0x2c]);  // Our MAC address.
    }

    [Fact]
    public void BuildsClaimStage2Packet()
    {
        var packet = Vcdj.BuildClaimStage2Packet(3, 7, autoAssign: true);
        Assert.Equal(0x32, packet.Length);
        AssertHeaderAndName(packet, 0x02, 0x0c);
        Assert.Equal(new byte[] { 0x01, 0x03, 0x00, 0x32 }, packet[0x20..0x24]);
        Assert.Equal(VirtualCdjTestHelpers.TestAddress.GetAddressBytes(), packet[0x24..0x28]);
        Assert.Equal(VirtualCdjTestHelpers.TestMac, packet[0x28..0x2e]);
        Assert.Equal(7, packet[0x2e]);      // The number we are claiming.
        Assert.Equal(3, packet[0x2f]);      // The packet counter.
        Assert.Equal(1, packet[0x30]);
        Assert.Equal(1, packet[0x31]);      // Auto-assign flag.

        var specific = Vcdj.BuildClaimStage2Packet(1, 4, autoAssign: false);
        Assert.Equal(2, specific[0x31]);    // Specific-number flag.
    }

    [Fact]
    public void BuildsClaimStage3Packet()
    {
        var packet = Vcdj.BuildClaimStage3Packet(1, 7);
        Assert.Equal(0x26, packet.Length);
        AssertHeaderAndName(packet, 0x04, 0x0c);
        Assert.Equal(new byte[] { 0x01, 0x03, 0x00, 0x26 }, packet[0x20..0x24]);
        Assert.Equal(7, packet[0x24]);      // The number we are claiming.
        Assert.Equal(1, packet[0x25]);      // The packet counter.
    }

    [Fact]
    public void BuildsKeepAlivePacket()
    {
        Vcdj.DeviceNumber = 7;
        var packet = Vcdj.BuildKeepAlivePacket();
        Assert.Equal(0x36, packet.Length);
        AssertHeaderAndName(packet, 0x06, 0x0c);
        Assert.Equal(new byte[] { 0x01, 0x02, 0x00, 0x36 }, packet[0x20..0x24]);
        Assert.Equal(7, packet[VirtualCdj.DeviceNumberOffset]);
        Assert.Equal(1, packet[0x25]);
        Assert.Equal(VirtualCdjTestHelpers.TestMac, packet[VirtualCdj.MacAddressOffset..(VirtualCdj.MacAddressOffset + 6)]);
        Assert.Equal(VirtualCdjTestHelpers.TestAddress.GetAddressBytes(), packet[44..48]);  // Our IPv4 address at offset 44.
        Assert.Equal(new byte[] { 0x02, 0x00, 0x00, 0x00, 0x01, 0x64 }, packet[0x30..0x36]);

        // A keep-alive we build must parse as a device announcement reporting our identity.
        var announcement = new DeviceAnnouncement(packet, VirtualCdjTestHelpers.TestAddress);
        Assert.Equal(7, announcement.DeviceNumber);
        Assert.Equal("beat-link", announcement.DeviceName);
        Assert.Equal(VirtualCdjTestHelpers.TestMac, announcement.GetHardwareAddress());
    }

    [Fact]
    public void BuildsDeviceNumberDefensePacket()
    {
        Vcdj.DeviceNumber = 5;
        var packet = Vcdj.BuildDeviceNumberDefensePacket();
        Assert.Equal(0x29, packet.Length);
        AssertHeaderAndName(packet, 0x08, 0x0c);
        Assert.Equal(new byte[] { 0x01, 0x02, 0x00, 0x29 }, packet[0x20..0x24]);
        Assert.Equal(5, packet[0x24]);      // The number we are defending.
        Assert.Equal(VirtualCdjTestHelpers.TestAddress.GetAddressBytes(), packet[0x25..0x29]);
    }

    [Fact]
    public void BuildsAssignmentRequestPacket()
    {
        var packet = Vcdj.BuildAssignmentRequestPacket();  // Device number is 0, so auto-assign.
        Assert.Equal(0x32, packet.Length);
        Assert.Equal(0x02, packet[0x0a]);
        Assert.Equal(0x01, packet[0x0b]);   // The subtype which distinguishes it from claim stage 2.
        Assert.Equal("beat-link", Util.DecodeAndTrim(packet, 0x0c, 20));
        Assert.Equal(new byte[] { 0x01, 0x02, 0x00, 0x32 }, packet[0x20..0x24]);
        Assert.Equal(VirtualCdjTestHelpers.TestAddress.GetAddressBytes(), packet[0x24..0x28]);
        Assert.Equal(VirtualCdjTestHelpers.TestMac, packet[0x28..0x2e]);
        Assert.Equal(1, packet[0x2f]);      // The packet counter.
        Assert.Equal(1, packet[0x31]);      // Auto-assign flag.

        Vcdj.DeviceNumber = 3;
        Assert.Equal(2, Vcdj.BuildAssignmentRequestPacket()[0x31]);  // Specific-number flag.
    }

    [Fact]
    public void BuildsStatusPacket()
    {
        Vcdj.DeviceNumber = 2;
        Vcdj.Tempo = 128.0;

        var metronome = new Metronome { Tempo = 130.0 };
        metronome.JumpToBeat(65);
        var snapshot = metronome.GetSnapshot();

        var packet = Vcdj.BuildStatusPacket(snapshot, playing: true);

        Assert.Equal(0x11c, packet.Length);
        AssertHeaderAndName(packet, 0x0a, 0x0b);           // Status packets carry the name at 0x0b.
        Assert.Equal(0x11c - 0x24, (int)Util.BytesToNumber(packet, 0x22, 2));  // Reported payload length.
        Assert.Equal(2, packet[0x21]);                     // Device number.
        Assert.Equal(2, packet[0x24]);
        Assert.Equal(1, packet[0x27]);                     // The "a" playing flag.
        Assert.Equal(2, packet[0x28]);                     // Dr, the player the track was loaded from.
        Assert.Equal(3, packet[0x7b]);                     // P1, playing.
        Assert.Equal(1, (int)Util.BytesToNumber(packet, 0x84, 4));  // Sync counter.
        Assert.Equal(0x84 + 0x40, packet[0x89]);           // F: base flags plus playing only.
        Assert.Equal(0x7a, packet[0x8b]);                  // P2, playing.
        Assert.Equal(Util.NeutralPitch, Util.BytesToNumber(packet, 0x8d, 3));
        Assert.Equal(12800, (int)Util.BytesToNumber(packet, 0x92, 2));  // BPM * 100 comes from our metronome tempo.
        Assert.Equal(9, packet[0x9d]);                     // P3, playing.
        Assert.Equal(0, packet[0x9e]);                     // Mm: not tempo master.
        Assert.Equal(0xff, packet[0x9f]);                  // Mh: no master handoff in progress.
        // Millisecond rounding in JumpToBeat can legitimately land the snapshot a fraction of a
        // millisecond before the beat-65 boundary, so assert against the snapshot, not a literal.
        Assert.Equal(snapshot.Beat, Util.BytesToNumber(packet, 0xa0, 4));
        Assert.Equal(snapshot.BeatWithinBar, packet[0xa6]);
        Assert.Equal(1, (int)Util.BytesToNumber(packet, 0xc8, 4));    // Packet counter.

        // The packet we build must round-trip through our own CdjStatus parser.
        var status = new CdjStatus(packet, VirtualCdjTestHelpers.TestAddress);
        Assert.Equal(2, status.DeviceNumber);
        Assert.Equal("beat-link", status.DeviceName);
        Assert.Equal(12800, status.Bpm);
        Assert.Equal((int)Util.NeutralPitch, status.Pitch);
        Assert.True(status.IsPlaying);
        Assert.False(status.IsTempoMaster);
        Assert.False(status.IsSynced);
        Assert.False(status.IsOnAir);
        Assert.Equal(1, status.SyncNumber);
        Assert.Null(status.DeviceMasterIsBeingYieldedTo);
        Assert.Equal((int)snapshot.Beat, status.BeatNumber);

        // A second packet increments the packet counter, and reflects flag changes.
        Vcdj.SetOnAir(true);
        var second = Vcdj.BuildStatusPacket(snapshot, playing: false);
        Assert.Equal(2, (int)Util.BytesToNumber(second, 0xc8, 4));
        Assert.Equal(5, second[0x7b]);                     // P1, stopped.
        Assert.Equal(0x84 + 0x08, second[0x89]);           // F: base flags plus on-air only.
        Assert.Equal(0x7e, second[0x8b]);                  // P2, stopped.
        Assert.Equal(1, second[0x9d]);                     // P3, stopped.
    }

    [Fact]
    public void BuildsBeatPacket()
    {
        Vcdj.DeviceNumber = 3;

        var metronome = new Metronome { Tempo = 120.0 };  // 500 ms beats.
        metronome.JumpToBeat(3);
        var snapshot = metronome.GetSnapshot();
        Assert.Equal(3, snapshot.BeatWithinBar);

        var packet = Vcdj.BuildBeatPacket(snapshot);

        Assert.Equal(0x60, packet.Length);
        AssertHeaderAndName(packet, 0x28, 0x0b);
        Assert.Equal(3, packet[0x21]);                                 // Device number.
        Assert.Equal(500, (int)Util.BytesToNumber(packet, 0x24, 4));   // Next beat.
        Assert.Equal(1000, (int)Util.BytesToNumber(packet, 0x28, 4));  // 2nd beat.
        Assert.Equal(1000, (int)Util.BytesToNumber(packet, 0x2c, 4));  // Next bar (2 beats away).
        Assert.Equal(2000, (int)Util.BytesToNumber(packet, 0x30, 4));  // 4th beat.
        Assert.Equal(3000, (int)Util.BytesToNumber(packet, 0x34, 4));  // 2nd bar.
        Assert.Equal(4000, (int)Util.BytesToNumber(packet, 0x38, 4));  // 8th beat.
        Assert.Equal(Util.NeutralPitch, Util.BytesToNumber(packet, 0x55, 3));
        Assert.Equal(12000, (int)Util.BytesToNumber(packet, 0x5a, 2)); // BPM * 100.
        Assert.Equal(3, packet[0x5c]);                                 // Beat within bar.
        Assert.Equal(3, packet[0x5f]);                                 // Device number again.

        // The packet we build must round-trip through our own Beat parser.
        var beat = new Beat(packet, VirtualCdjTestHelpers.TestAddress);
        Assert.Equal(3, beat.DeviceNumber);
        Assert.Equal(12000, beat.Bpm);
        Assert.Equal((int)Util.NeutralPitch, beat.Pitch);
        Assert.Equal(3, beat.BeatWithinBar);
        Assert.Equal(500, beat.NextBeat);
        Assert.Equal(120.0, beat.EffectiveTempo, 3);
    }

    [Fact]
    public void BuildsLoadSettingsPayload()
    {
        Vcdj.DeviceNumber = 2;
        var target = VirtualCdjTestHelpers.MakeStatus(3, master: false);
        var settings = new PlayerSettings();

        var payload = Vcdj.BuildLoadSettingsPayload(target, settings);

        Assert.Equal(0x55, payload.Length);
        Assert.Equal(0x02, payload[0]);
        Assert.Equal(2, payload[0x20 - 0x1f]);   // Our device number.
        Assert.Equal(3, payload[0x21 - 0x1f]);   // The target device.
        Assert.Equal(settings.OnAirDisplay.ProtocolValue, payload[0x2c - 0x1f]);
        Assert.Equal(settings.MainLcdBrightness.ProtocolValue, payload[0x2d - 0x1f]);
        Assert.Equal(settings.Quantize.ProtocolValue, payload[0x2e - 0x1f]);
        Assert.Equal(settings.InitialAutoCueLevel.ProtocolValue, payload[0x2f - 0x1f]);
        Assert.Equal(settings.InterfaceLanguage.ProtocolValue, payload[0x30 - 0x1f]);
        Assert.Equal(settings.Sync.ProtocolValue, payload[0x3a - 0x1f]);
        Assert.Equal(settings.TimeDisplay.ProtocolValue, payload[0x44 - 0x1f]);
        Assert.Equal(settings.TempoFaderRange.ProtocolValue, payload[0x48 - 0x1f]);
        Assert.Equal(settings.JogWheelLcdBrightness.ProtocolValue, payload[0x4f - 0x1f]);
    }

    [Fact]
    public void DeviceNameAndNumberValidation()
    {
        Assert.Equal("beat-link", Vcdj.DeviceName);
        Vcdj.DeviceName = "My Virtual CDJ";
        Assert.Equal("My Virtual CDJ", Vcdj.DeviceName);
        Assert.Equal("My Virtual CDJ", Util.DecodeAndTrim(Vcdj.BuildKeepAlivePacket(), 0x0c, 20));
        Assert.Throws<ArgumentException>(() => Vcdj.DeviceName = "this name is way, way too long");

        Assert.Throws<ArgumentException>(() => Vcdj.AnnounceInterval = 199);
        Assert.Throws<ArgumentException>(() => Vcdj.AnnounceInterval = 2001);
        Vcdj.AnnounceInterval = 200;
        Assert.Equal(200, Vcdj.AnnounceInterval);

        Assert.Throws<ArgumentException>(() => Vcdj.StatusInterval = 19);
        Assert.Throws<ArgumentException>(() => Vcdj.StatusInterval = 2001);
        Vcdj.StatusInterval = 50;
        Assert.Equal(50, Vcdj.StatusInterval);

        Assert.Throws<ArgumentException>(() => Vcdj.Tempo = 0.0);
    }
}

/// <summary>
/// Exercises the tempo master state machine of the <see cref="VirtualCdj"/> by injecting
/// synthetic <see cref="CdjStatus"/> and <see cref="Beat"/> objects through the internal
/// <c>ProcessUpdate</c>/<c>ProcessBeat</c> entry points, with no network involvement.
/// </summary>
[Collection("Network services")]
public sealed class VirtualCdjStateMachineTests : IDisposable
{
    private static VirtualCdj Vcdj => VirtualCdj.Instance;

    public VirtualCdjStateMachineTests()
    {
        Vcdj.ResetForTests();
    }

    public void Dispose() => Vcdj.ResetForTests();

    [Fact]
    public void MasterChangedAndTempoChangedFireOnMasterUpdates()
    {
        Vcdj.SetRunningStateForTests(true);
        var masterChanges = new List<DeviceUpdate?>();
        var tempoChanges = new List<double>();
        var updatesSeen = new List<DeviceUpdate>();
        EventHandler<DeviceUpdate?> onMaster = (_, update) => masterChanges.Add(update);
        EventHandler<double> onTempo = (_, tempo) => tempoChanges.Add(tempo);
        EventHandler<DeviceUpdate> onUpdate = (_, update) => updatesSeen.Add(update);
        Vcdj.MasterChanged += onMaster;
        Vcdj.TempoChanged += onTempo;
        Vcdj.DeviceUpdateReceived += onUpdate;
        try
        {
            // A master status packet establishes the master and the master tempo.
            var master = VirtualCdjTestHelpers.MakeStatus(3, master: true, bpm100: 12850);
            Vcdj.ProcessUpdate(master);
            Assert.Same(master, Vcdj.TempoMaster);
            Assert.Same(master, Assert.Single(masterChanges));
            Assert.Equal(128.5, Assert.Single(tempoChanges), 3);
            Assert.Equal(128.5, Vcdj.MasterTempo, 3);
            Assert.Same(master, Assert.Single(updatesSeen));
            Assert.Same(master, Vcdj.GetLatestStatusFor(3));
            Assert.Same(master, Vcdj.GetLatestStatusFor(master));
            Assert.Contains(master, Vcdj.LatestStatus);

            // A new tempo from the same master fires TempoChanged but not MasterChanged.
            Vcdj.ProcessUpdate(VirtualCdjTestHelpers.MakeStatus(3, master: true, bpm100: 13000));
            Assert.Single(masterChanges);
            Assert.Equal(2, tempoChanges.Count);
            Assert.Equal(130.0, tempoChanges[1], 3);

            // An invalid BPM (no track loaded) is ignored for tempo purposes.
            Vcdj.ProcessUpdate(VirtualCdjTestHelpers.MakeStatus(3, master: true, bpm100: 0xffff));
            Assert.Equal(2, tempoChanges.Count);
            Assert.Equal(130.0, Vcdj.MasterTempo, 3);

            // Tiny changes within the tempo epsilon do not fire TempoChanged.
            Vcdj.TempoEpsilon = 1.0;
            Vcdj.ProcessUpdate(VirtualCdjTestHelpers.MakeStatus(3, master: true, bpm100: 13050));
            Assert.Equal(2, tempoChanges.Count);

            // When the master resigns the role and nobody else claims it, master becomes null.
            Vcdj.ProcessUpdate(VirtualCdjTestHelpers.MakeStatus(3, master: false));
            Assert.Null(Vcdj.TempoMaster);
            Assert.Equal(2, masterChanges.Count);
            Assert.Null(masterChanges[1]);
        }
        finally
        {
            Vcdj.MasterChanged -= onMaster;
            Vcdj.TempoChanged -= onTempo;
            Vcdj.DeviceUpdateReceived -= onUpdate;
        }
    }

    [Fact]
    public void MasterHandoffToUsSequence()
    {
        Vcdj.DeviceNumber = 2;
        Vcdj.SetRunningStateForTests(true);
        Vcdj.SetSendingStatusStateForTests(true);
        var masterChanges = new List<DeviceUpdate?>();
        EventHandler<DeviceUpdate?> onMaster = (_, update) => masterChanges.Add(update);
        Vcdj.MasterChanged += onMaster;
        try
        {
            // Device 3 is the current master.
            Vcdj.ProcessUpdate(VirtualCdjTestHelpers.MakeStatus(3, master: true));
            Assert.NotNull(Vcdj.TempoMaster);
            Assert.False(Vcdj.IsTempoMaster);

            // We ask to become master; the yield request cannot actually be sent (no socket in
            // this test), but the request state must have been recorded first, as in Java.
            Assert.Throws<InvalidOperationException>(Vcdj.BecomeTempoMaster);
            Assert.Equal(3, Vcdj.RequestingMasterRoleFromPlayerForTests);

            // A yield response from the wrong player is ignored.
            Vcdj.HandleMasterYieldResponse(4, yielded: true);
            Assert.Equal(3, Vcdj.RequestingMasterRoleFromPlayerForTests);
            Assert.Equal(0, Vcdj.MasterYieldedFromForTests);

            // The response from the player we asked arms the handoff.
            Vcdj.HandleMasterYieldResponse(3, yielded: true);
            Assert.Equal(0, Vcdj.RequestingMasterRoleFromPlayerForTests);
            Assert.Equal(3, Vcdj.MasterYieldedFromForTests);

            // When device 3 sends a master packet yielding to our device number, we take over.
            Vcdj.ProcessUpdate(VirtualCdjTestHelpers.MakeStatus(3, master: true, yieldingTo: 2));
            Assert.True(Vcdj.IsTempoMaster);
            Assert.Equal(0, Vcdj.MasterYieldedFromForTests);
            Assert.Null(Vcdj.TempoMaster);       // We are master, so no external master.
            Assert.Null(masterChanges[^1]);      // The change to "no external master" was reported.
        }
        finally
        {
            Vcdj.MasterChanged -= onMaster;
        }
    }

    [Fact]
    public void MasterHandoffFromUsSequence()
    {
        Vcdj.DeviceNumber = 2;
        Vcdj.SetRunningStateForTests(true);
        Vcdj.SetSendingStatusStateForTests(true);

        // With no current master, we can become master immediately.
        Vcdj.BecomeTempoMaster();
        Assert.True(Vcdj.IsTempoMaster);
        Assert.Equal(0, Vcdj.RequestingMasterRoleFromPlayerForTests);
        Assert.Equal(0xff, Vcdj.NextMasterForTests);

        // Device 3 appears (not yet master), so we know where to send the yield acknowledgment.
        Vcdj.ProcessUpdate(VirtualCdjTestHelpers.MakeStatus(3, master: false, syncNumber: 5));
        Assert.True(Vcdj.IsTempoMaster);  // A non-master packet from another player leaves us master.

        // Device 3 asks us to yield the master role. (The acknowledgment packet cannot be sent
        // without a socket, but the handoff state must still be armed, as the Java implementation
        // catches delivery problems the same way.)
        Vcdj.HandleMasterYieldRequest(3);
        Assert.Equal(3, Vcdj.NextMasterForTests);
        Assert.True(Vcdj.IsTempoMaster);  // Still master until the other player asserts the role.

        // When device 3 asserts the master role, we complete the handoff, adopting a sync counter
        // one larger than the largest seen on the network.
        Vcdj.ProcessUpdate(VirtualCdjTestHelpers.MakeStatus(3, master: true, syncNumber: 7));
        Assert.False(Vcdj.IsTempoMaster);
        Assert.Equal(0xff, Vcdj.NextMasterForTests);
        Assert.Equal(8, Vcdj.SyncCounterForTests);
        Assert.NotNull(Vcdj.TempoMaster);
        Assert.Equal(3, Vcdj.TempoMaster!.DeviceNumber);
    }

    [Fact]
    public void YieldRequestIgnoredWhenNotMaster()
    {
        Vcdj.DeviceNumber = 2;
        Vcdj.SetRunningStateForTests(true);
        Vcdj.SetSendingStatusStateForTests(true);
        Vcdj.HandleMasterYieldRequest(3);
        Assert.Equal(0xff, Vcdj.NextMasterForTests);  // Not master, so nothing was armed.

        // A yield response when we never asked for the role is likewise ignored.
        Vcdj.HandleMasterYieldResponse(3, yielded: true);
        Assert.Equal(0, Vcdj.MasterYieldedFromForTests);
    }

    [Fact]
    public void MasterBeatsFireAndSlaveOurMetronomeWhenSynced()
    {
        Vcdj.DeviceNumber = 2;
        Vcdj.SetRunningStateForTests(true);
        Vcdj.SetSendingStatusStateForTests(true);
        var masterBeats = new List<Beat>();
        EventHandler<Beat> onBeat = (_, beat) => masterBeats.Add(beat);
        Vcdj.MasterBeat += onBeat;
        try
        {
            Vcdj.ProcessUpdate(VirtualCdjTestHelpers.MakeStatus(3, master: true, bpm100: 12850));

            // A beat from a non-master device is not a master beat.
            Vcdj.ProcessUpdate(VirtualCdjTestHelpers.MakeStatus(4, master: false));
            Vcdj.ProcessBeat(VirtualCdjTestHelpers.MakeBeat(4, 12000));
            Assert.Empty(masterBeats);

            // A beat from the master fires the event and updates the master tempo.
            Vcdj.ProcessBeat(VirtualCdjTestHelpers.MakeBeat(3, 13000));
            Assert.Single(masterBeats);
            Assert.Equal(130.0, Vcdj.MasterTempo, 3);

            // Once we are synced, master tempo changes slave our own metronome.
            Vcdj.SetSynced(true);
            Assert.True(Vcdj.IsSynced);
            Assert.Equal(130.0, Vcdj.Tempo, 3);  // Adopted the master tempo on becoming synced.
            Vcdj.ProcessBeat(VirtualCdjTestHelpers.MakeBeat(3, 14000));
            Assert.Equal(2, masterBeats.Count);
            Assert.Equal(140.0, Vcdj.Tempo, 3);  // Slaved to the new master tempo.
        }
        finally
        {
            Vcdj.MasterBeat -= onBeat;
        }
    }

    [Fact]
    public void BeatsIgnoredWhenNotRunning()
    {
        var masterBeats = new List<Beat>();
        EventHandler<Beat> onBeat = (_, beat) => masterBeats.Add(beat);
        Vcdj.MasterBeat += onBeat;
        try
        {
            Vcdj.ProcessBeat(VirtualCdjTestHelpers.MakeBeat(3, 13000));
            Assert.Empty(masterBeats);
        }
        finally
        {
            Vcdj.MasterBeat -= onBeat;
        }
    }

    [Fact]
    public void SpecialAnnouncementPacketsDriveClaimState()
    {
        // A defense of the number we are trying to claim rejects our claim.
        Vcdj.ClaimingNumberForTests = 7;
        var inUse = PacketFixtures.NewPacket(0x29, 0x08, "CDJ-3000", 0x0c);
        inUse[0x24] = 7;
        Vcdj.HandleSpecialAnnouncementPacket(PacketType.DeviceNumberInUse, inUse,
            VirtualCdjTestHelpers.AddressFor(9));
        Assert.True(Vcdj.ClaimRejectedForTests);

        // A defense of some other number does not.
        Vcdj.ResetForTests();
        Vcdj.ClaimingNumberForTests = 7;
        inUse[0x24] = 5;
        Vcdj.HandleSpecialAnnouncementPacket(PacketType.DeviceNumberInUse, inUse,
            VirtualCdjTestHelpers.AddressFor(9));
        Assert.False(Vcdj.ClaimRejectedForTests);

        // A mixer assignment records the assigned number.
        var assign = PacketFixtures.NewPacket(0x29, 0x03, "DJM-900nexus", 0x0c);
        assign[0x24] = 3;
        Vcdj.HandleSpecialAnnouncementPacket(PacketType.DeviceNumberAssign, assign,
            VirtualCdjTestHelpers.AddressFor(33));
        Assert.Equal(3, Vcdj.MixerAssignedForTests);

        // A mixer assignment confirmation locks in the number we were claiming.
        Vcdj.ClaimingNumberForTests = 4;
        var finished = PacketFixtures.NewPacket(0x26, 0x05, "DJM-900nexus", 0x0c);
        Vcdj.HandleSpecialAnnouncementPacket(PacketType.DeviceNumberAssignmentFinished, finished,
            VirtualCdjTestHelpers.AddressFor(33));
        Assert.Equal(4, Vcdj.MixerAssignedForTests);
    }

    [Fact]
    public void SendingStatusRequiresRunningWithStandardNumber()
    {
        Assert.Throws<InvalidOperationException>(() => Vcdj.SetSendingStatus(true));

        Vcdj.DeviceNumber = 7;
        Vcdj.SetRunningStateForTests(true);
        Assert.Throws<InvalidOperationException>(() => Vcdj.SetSendingStatus(true));
        Assert.False(Vcdj.IsSendingStatus);
    }

    [Fact]
    public void PlaybackPositionControls()
    {
        // While stopped, jumping to a beat is reflected in the saved position.
        Vcdj.JumpToBeat(64);
        Assert.Equal(64, Vcdj.GetPlaybackPosition().Beat);
        Assert.False(Vcdj.IsPlaying);

        // Beats beyond the maximum wrap around.
        Vcdj.JumpToBeat(VirtualCdj.MaxBeat + 5);
        Assert.Equal(5, Vcdj.GetPlaybackPosition().Beat);

        // Values below one clamp to the first beat.
        Vcdj.JumpToBeat(-3);
        Assert.Equal(1, Vcdj.GetPlaybackPosition().Beat);

        // Playing resumes from where we stopped.
        Vcdj.JumpToBeat(17);
        Vcdj.SetPlaying(true);
        Assert.True(Vcdj.IsPlaying);
        var before = Vcdj.GetPlaybackPosition();
        Assert.Equal(17, before.Beat);
        Vcdj.AdjustPlaybackPosition(250);  // Nudge forward half a beat at 120 BPM.
        var after = Vcdj.GetPlaybackPosition();
        // Position moved forward by ~half a beat (tolerant of scheduling delays between calls).
        var beatsMoved = after.Beat + after.BeatPhase - (before.Beat + before.BeatPhase);
        Assert.InRange(beatsMoved, 0.45, 1.5);
        Vcdj.SetPlaying(false);
        Assert.InRange(Vcdj.GetPlaybackPosition().Beat, 17, 19);
    }

    [Fact]
    public void MediaDetailsEventFiresFromMediaResponsePackets()
    {
        var received = new List<MediaDetails>();
        EventHandler<MediaDetails> onDetails = (_, details) => received.Add(details);
        Vcdj.MediaDetailsReceived += onDetails;
        try
        {
            var bytes = PacketFixtures.NewPacket(0xc0, 0x06, "CDJ-2000nexus", 0x0b);
            Util.NumberToBytes(0xc0 - 0x24, bytes, 0x22, 2);   // Payload length.
            bytes[0x27] = 2;                                   // Host player.
            bytes[0x2b] = 3;                                   // USB slot.
            Encoding.BigEndianUnicode.GetBytes("BEAT LINK").CopyTo(bytes, 0x2c);
            bytes[0xaa] = 1;                                   // Media type: rekordbox.
            var update = Vcdj.BuildUpdate(bytes, VirtualCdjTestHelpers.AddressFor(2));
            Assert.Null(update);  // Media responses are delivered via the event, not as updates.
            var details = Assert.Single(received);
            Assert.Equal("BEAT LINK", details.Name);
            Assert.Equal(2, details.SlotReference.Player);
        }
        finally
        {
            Vcdj.MediaDetailsReceived -= onDetails;
        }
    }

    [Fact]
    public void BuildUpdateRoutesStatusPackets()
    {
        var cdjBytes = PacketFixtures.NexusCdjStatusPacket();
        var cdj = Vcdj.BuildUpdate(cdjBytes, VirtualCdjTestHelpers.AddressFor(2));
        Assert.IsType<CdjStatus>(cdj);

        var mixerBytes = PacketFixtures.NewPacket(0x38, 0x29, "DJM-900nexus", 0x0b);
        mixerBytes[0x21] = 33;
        Util.NumberToBytes(0x38 - 0x24, mixerBytes, 0x22, 2);
        Util.NumberToBytes(Util.NeutralPitch, mixerBytes, 0x28, 3);
        Util.NumberToBytes(12000, mixerBytes, 0x2e, 2);
        var mixer = Vcdj.BuildUpdate(mixerBytes, VirtualCdjTestHelpers.AddressFor(33));
        Assert.IsType<MixerStatus>(mixer);

        // Garbage is rejected without throwing.
        Assert.Null(Vcdj.BuildUpdate(new byte[40], VirtualCdjTestHelpers.AddressFor(2)));
    }

    [Fact]
    public void StateGuardsWhenNotRunning()
    {
        Assert.False(Vcdj.IsRunning);
        Assert.Throws<InvalidOperationException>(() => Vcdj.TempoMaster);
        Assert.Throws<InvalidOperationException>(() => Vcdj.MasterTempo);
        Assert.Throws<InvalidOperationException>(() => Vcdj.LatestStatus);
        Assert.Throws<InvalidOperationException>(() => Vcdj.GetLatestStatusFor(2));
        Assert.Throws<InvalidOperationException>(() => Vcdj.MatchingInterfaces);
        Assert.Throws<InvalidOperationException>(Vcdj.FindUnreachablePlayers);
        Assert.Throws<InvalidOperationException>(Vcdj.BecomeTempoMaster);

        // Defending a device number while offline logs a warning without throwing, and special
        // announcement packets are tolerated, matching the Java behavior when its socket is
        // closed.
        Vcdj.DefendDeviceNumber(VirtualCdjTestHelpers.AddressFor(9));
        var claim = PacketFixtures.NewPacket(0x32, 0x02, "CDJ-3000", 0x0c);
        Vcdj.HandleSpecialAnnouncementPacket(PacketType.DeviceNumberStage2, claim,
            VirtualCdjTestHelpers.AddressFor(9));
    }
}
