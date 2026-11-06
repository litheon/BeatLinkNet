using System.Net;
using System.Text;
using BeatLink.Data;

namespace BeatLink.Tests;

/// <summary>
/// Helpers for building the synthetic protocol packets used by the parsing tests. All offsets are
/// taken from the Java beat-link source (and the Packet Analysis document it encodes).
/// </summary>
internal static class PacketFixtures
{
    internal static readonly IPAddress Address = IPAddress.Parse("192.168.1.42");

    internal static byte[] NewPacket(int length, byte typeByte, string deviceName, int nameOffset)
    {
        var bytes = new byte[length];
        Util.MagicHeader.CopyTo(bytes);
        bytes[Util.PacketTypeOffset] = typeByte;
        WriteName(bytes, nameOffset, deviceName);
        return bytes;
    }

    internal static void WriteName(byte[] bytes, int offset, string name)
    {
        var encoded = Encoding.ASCII.GetBytes(name);
        Array.Clear(bytes, offset, 20);
        encoded.CopyTo(bytes, offset);
    }

    /// <summary>
    /// Builds a plausible 212-byte (0xd4) nexus CDJ status packet with every field the tests
    /// examine populated.
    /// </summary>
    internal static byte[] NexusCdjStatusPacket()
    {
        var bytes = NewPacket(0xd4, 0x0a, "CDJ-2000nexus", 0x0b);
        bytes[0x21] = 2;                                    // device number
        Util.NumberToBytes(0xd4 - 0x24, bytes, 0x22, 2);    // payload length
        bytes[0x27] = 1;                                    // busy
        bytes[0x28] = 2;                                    // track source player
        bytes[0x29] = 3;                                    // track source slot: USB
        bytes[0x2a] = 1;                                    // track type: rekordbox
        Util.NumberToBytes(42, bytes, 0x2c, 4);             // rekordbox id
        Util.NumberToBytes(5, bytes, 0x32, 2);              // track number
        bytes[0x37] = 0x1e;                                 // CD state: disc mounted
        Util.NumberToBytes(12, bytes, 0x46, 2);             // disc track count
        bytes[0x6f] = 0;                                    // USB state: loaded
        bytes[0x73] = 4;                                    // SD state: empty
        bytes[0x75] = 1;                                    // link media available
        bytes[0x7b] = 3;                                    // play state 1: playing
        Encoding.ASCII.GetBytes("1.40").CopyTo(bytes, 0x7c);// firmware version
        Util.NumberToBytes(5, bytes, 0x84, 4);              // sync number
        bytes[0x89] = 0x40 | 0x10 | 0x08;                   // flags: playing, synced, on-air
        bytes[0x8b] = 0x7a;                                 // play state 2: moving
        Util.NumberToBytes(Util.NeutralPitch, bytes, 0x8d, 3);      // pitch 1
        Util.NumberToBytes(12850, bytes, 0x92, 2);          // BPM * 100
        Util.NumberToBytes(524288, bytes, 153, 3);          // pitch 2
        bytes[0x9d] = 9;                                    // play state 3: forward vinyl
        bytes[0x9f] = 0xff;                                 // not handing off master
        Util.NumberToBytes(128, bytes, 0xa0, 4);            // beat number
        Util.NumberToBytes(511, bytes, 0xa4, 2);            // cue countdown
        bytes[0xa6] = 4;                                    // beat within bar
        Util.NumberToBytes(786432, bytes, 193, 3);          // pitch 3
        Util.NumberToBytes(Util.NeutralPitch, bytes, 197, 3);       // pitch 4
        Util.NumberToBytes(173, bytes, 0xc8, 4);            // packet number
        return bytes;
    }
}

public class DeviceAnnouncementTests
{
    private static byte[] KeepAlivePacket(string name)
    {
        var bytes = PacketFixtures.NewPacket(0x36, 0x06, name, 0x0c);
        bytes[0x24] = 3;                                             // device number
        new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06 }.CopyTo(bytes, 0x26);  // MAC address
        bytes[0x30] = 2;                                             // peer count
        return bytes;
    }

    [Fact]
    public void ParsesKeepAlivePacket()
    {
        var bytes = KeepAlivePacket("CDJ-2000nexus");
        var announcement = new DeviceAnnouncement(bytes, PacketFixtures.Address);
        Assert.Equal("CDJ-2000nexus", announcement.DeviceName);
        Assert.Equal(3, announcement.DeviceNumber);
        Assert.Equal(PacketFixtures.Address, announcement.Address);
        Assert.Equal(new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06 }, announcement.GetHardwareAddress());
        Assert.Equal(2, announcement.PeerCount);
        Assert.False(announcement.IsOpusQuad);
        Assert.False(announcement.IsXdjAz);
        Assert.False(announcement.IsUsingDeviceLibraryPlus);
        Assert.True(announcement.Timestamp > 0);
        Assert.Equal(bytes, announcement.PacketBytes.ToArray());
    }

    [Fact]
    public void RecognizesOpusQuad()
    {
        var announcement = new DeviceAnnouncement(KeepAlivePacket("OPUS-QUAD"), PacketFixtures.Address);
        Assert.True(announcement.IsOpusQuad);
        Assert.False(announcement.IsXdjAz);
        Assert.True(announcement.IsUsingDeviceLibraryPlus);
    }

    [Fact]
    public void RecognizesXdjAz()
    {
        var announcement = new DeviceAnnouncement(KeepAlivePacket("XDJ-AZ"), PacketFixtures.Address);
        Assert.False(announcement.IsOpusQuad);
        Assert.True(announcement.IsXdjAz);
        Assert.True(announcement.IsUsingDeviceLibraryPlus);
    }

    [Fact]
    public void SecondConstructorInjectsSyntheticDeviceNumber()
    {
        var announcement = new DeviceAnnouncement(KeepAlivePacket("OPUS-QUAD"), PacketFixtures.Address, 12);
        Assert.Equal(12, announcement.DeviceNumber);
        Assert.True(announcement.IsOpusQuad);
    }

    [Fact]
    public void RejectsWrongLengthPackets()
    {
        Assert.Throws<ArgumentException>(() => new DeviceAnnouncement(new byte[53], PacketFixtures.Address));
        Assert.Throws<ArgumentException>(() => new DeviceAnnouncement(new byte[55], PacketFixtures.Address));
        Assert.Throws<ArgumentException>(() => new DeviceAnnouncement(new byte[53], PacketFixtures.Address, 5));
    }
}

// In the network-services collection because MasterAndSyncQueriesRequireVirtualCdj (and the
// beat-within-bar guess) depend on the VirtualCdj being stopped, which cannot be guaranteed
// while tests that start it run in parallel.
[Collection("Network services")]
public class BeatTests
{
    private static byte[] BeatPacket(string name, byte deviceNumber)
    {
        var bytes = PacketFixtures.NewPacket(0x60, 0x28, name, 0x0b);
        bytes[0x21] = deviceNumber;
        Util.NumberToBytes(300, bytes, 0x24, 4);   // next beat
        Util.NumberToBytes(600, bytes, 0x28, 4);   // second beat
        Util.NumberToBytes(1200, bytes, 0x2c, 4);  // next bar
        Util.NumberToBytes(1500, bytes, 0x30, 4);  // fourth beat
        Util.NumberToBytes(2400, bytes, 0x34, 4);  // second bar
        Util.NumberToBytes(3600, bytes, 0x38, 4);  // eighth beat
        Util.NumberToBytes(Util.NeutralPitch, bytes, 0x55, 3);
        Util.NumberToBytes(12850, bytes, 0x5a, 2); // BPM * 100
        bytes[0x5c] = 3;                           // beat within bar
        return bytes;
    }

    [Fact]
    public void ParsesBeatPacket()
    {
        var beat = new Beat(BeatPacket("CDJ-3000", 2), PacketFixtures.Address);
        Assert.Equal("CDJ-3000", beat.DeviceName);
        Assert.Equal(2, beat.DeviceNumber);
        Assert.Equal((long)Util.NeutralPitch, beat.Pitch);
        Assert.Equal(12850, beat.Bpm);
        Assert.Equal(3, beat.BeatWithinBar);
        Assert.Equal(300, beat.NextBeat);
        Assert.Equal(600, beat.SecondBeat);
        Assert.Equal(1200, beat.NextBar);
        Assert.Equal(1500, beat.FourthBeat);
        Assert.Equal(2400, beat.SecondBar);
        Assert.Equal(3600, beat.EighthBeat);
        Assert.Equal(128.5, beat.EffectiveTempo, 10);
        Assert.Null(beat.DeviceMasterIsBeingYieldedTo);
        Assert.False(beat.IsFromOpusQuad);
        Assert.False(beat.IsPreNexusCdj);
    }

    [Fact]
    public void EffectiveTempoReflectsPitch()
    {
        var bytes = BeatPacket("CDJ-3000", 2);
        Util.NumberToBytes(Util.PercentageToPitch(5.0), bytes, 0x55, 3);
        var beat = new Beat(bytes, PacketFixtures.Address);
        Assert.Equal(128.5 * 1.05, beat.EffectiveTempo, 3);
    }

    [Fact]
    public void BeatWithinBarMeaningfulGuessedByDeviceNumberWhenVirtualCdjStopped()
    {
        VirtualCdj.Instance.ResetForTests();  // The guess is only used while it is stopped.
        Assert.True(new Beat(BeatPacket("CDJ-3000", 2), PacketFixtures.Address).IsBeatWithinBarMeaningful);
        Assert.False(new Beat(BeatPacket("DJM-900nexus", 33), PacketFixtures.Address).IsBeatWithinBarMeaningful);
    }

    [Fact]
    public void MasterAndSyncQueriesRequireVirtualCdj()
    {
        VirtualCdj.Instance.ResetForTests();  // These properties throw only while it is stopped.
        var beat = new Beat(BeatPacket("CDJ-3000", 2), PacketFixtures.Address);
        Assert.Throws<InvalidOperationException>(() => beat.IsTempoMaster);
        Assert.Throws<InvalidOperationException>(() => beat.IsSynced);
    }

    [Fact]
    public void TranslatesOpusDeviceNumbers()
    {
        var beat = new Beat(BeatPacket("OPUS-QUAD", 11), PacketFixtures.Address);
        Assert.True(beat.IsFromOpusQuad);
        Assert.Equal(3, beat.DeviceNumber);
    }

    [Fact]
    public void DetectsPreNexusCdjs()
    {
        Assert.True(new Beat(BeatPacket("CDJ-2000", 2), PacketFixtures.Address).IsPreNexusCdj);
        Assert.True(new Beat(BeatPacket("CDJ-900", 2), PacketFixtures.Address).IsPreNexusCdj);
        Assert.False(new Beat(BeatPacket("CDJ-2000nexus", 2), PacketFixtures.Address).IsPreNexusCdj);
    }

    [Fact]
    public void RejectsWrongLengthPackets()
    {
        Assert.Throws<ArgumentException>(() => new Beat(new byte[0x5f], PacketFixtures.Address));
        Assert.Throws<ArgumentException>(() => new Beat(new byte[0x61], PacketFixtures.Address));
    }
}

public class CdjStatusTests
{
    [Fact]
    public void ParsesNexusStatusPacket()
    {
        var status = new CdjStatus(PacketFixtures.NexusCdjStatusPacket(), PacketFixtures.Address);
        Assert.Equal("CDJ-2000nexus", status.DeviceName);
        Assert.Equal(2, status.DeviceNumber);
        Assert.Equal(2, status.TrackSourcePlayer);
        Assert.Same(CdjStatus.TrackSourceSlot.UsbSlot, status.SourceSlot);
        Assert.Same(CdjStatus.TrackType.Rekordbox, status.LoadedTrackType);
        Assert.Equal(42, status.RekordboxId);
        Assert.Equal(5, status.TrackNumber);
        Assert.Equal(12, status.DiscTrackCount);
        Assert.Equal(5, status.SyncNumber);
        Assert.Equal("1.40", status.FirmwareVersion);
        Assert.Equal(173, status.PacketNumber);
        Assert.Equal(128, status.BeatNumber);
        Assert.Equal(4, status.BeatWithinBar);
        Assert.True(status.IsBeatWithinBarMeaningful);
        Assert.Equal((long)Util.NeutralPitch, status.Pitch);
        Assert.Equal(12850, status.Bpm);
        Assert.Equal(128.5, status.EffectiveTempo, 10);
        Assert.False(status.StatusFlagsWereReplayed);
    }

    [Fact]
    public void ReportsPlayStates()
    {
        var status = new CdjStatus(PacketFixtures.NexusCdjStatusPacket(), PacketFixtures.Address);
        Assert.Same(CdjStatus.PlayState1.Playing, status.FirstPlayState);
        Assert.Same(CdjStatus.PlayState2.Moving, status.SecondPlayState);
        Assert.Same(CdjStatus.PlayState3.ForwardVinyl, status.ThirdPlayState);
        Assert.True(status.IsPlaying);
        Assert.True(status.IsPlayingForwards);
        Assert.False(status.IsPlayingBackwards);
        Assert.True(status.IsPlayingVinylMode);
        Assert.False(status.IsPlayingCdjMode);
        Assert.True(status.IsTrackLoaded);
        Assert.False(status.IsLooping);
        Assert.False(status.IsPaused);
        Assert.False(status.IsCued);
        Assert.False(status.IsSearching);
        Assert.False(status.IsAtEnd);
        Assert.True(status.IsBusy);
    }

    [Fact]
    public void ReportsStatusFlags()
    {
        var status = new CdjStatus(PacketFixtures.NexusCdjStatusPacket(), PacketFixtures.Address);
        Assert.True(status.IsPlaying);
        Assert.True(status.IsSynced);
        Assert.True(status.IsOnAir);
        Assert.False(status.IsTempoMaster);
        Assert.False(status.IsBpmOnlySynced);

        var masterBytes = PacketFixtures.NexusCdjStatusPacket();
        masterBytes[0x89] = 0x20 | 0x02;  // master and bpm-sync flags only
        var master = new CdjStatus(masterBytes, PacketFixtures.Address);
        Assert.True(master.IsTempoMaster);
        Assert.True(master.IsBpmOnlySynced);
        Assert.False(master.IsPlaying);
        Assert.False(master.IsSynced);
        Assert.False(master.IsOnAir);
    }

    [Fact]
    public void ReportsMediaSlotStates()
    {
        var status = new CdjStatus(PacketFixtures.NexusCdjStatusPacket(), PacketFixtures.Address);
        Assert.True(status.IsLocalUsbLoaded);
        Assert.False(status.IsLocalUsbUnloading);
        Assert.False(status.IsLocalUsbEmpty);
        Assert.False(status.IsLocalSdLoaded);
        Assert.False(status.IsLocalSdUnloading);
        Assert.True(status.IsLocalSdEmpty);
        Assert.False(status.IsDiscSlotEmpty);
        Assert.False(status.IsDiscSlotAsleep);
        Assert.True(status.IsLinkMediaAvailable);
    }

    [Fact]
    public void ReportsAllFourPitches()
    {
        var status = new CdjStatus(PacketFixtures.NexusCdjStatusPacket(), PacketFixtures.Address);
        Assert.Equal((long)Util.NeutralPitch, status.GetPitch(1));
        Assert.Equal(524288, status.GetPitch(2));
        Assert.Equal(786432, status.GetPitch(3));
        Assert.Equal((long)Util.NeutralPitch, status.GetPitch(4));
        Assert.Throws<ArgumentException>(() => status.GetPitch(0));
        Assert.Throws<ArgumentException>(() => status.GetPitch(5));
    }

    [Fact]
    public void ReportsMasterHandoff()
    {
        var status = new CdjStatus(PacketFixtures.NexusCdjStatusPacket(), PacketFixtures.Address);
        Assert.Null(status.DeviceMasterIsBeingYieldedTo);

        var yielding = PacketFixtures.NexusCdjStatusPacket();
        yielding[0x9f] = 3;
        Assert.Equal(3, new CdjStatus(yielding, PacketFixtures.Address).DeviceMasterIsBeingYieldedTo);
    }

    [Fact]
    public void ReportsUnknownBeatNumberAsMinusOne()
    {
        var bytes = PacketFixtures.NexusCdjStatusPacket();
        Util.NumberToBytes(0xffffffffL, bytes, 0xa0, 4);
        bytes[0xa6] = 0;
        var status = new CdjStatus(bytes, PacketFixtures.Address);
        Assert.Equal(-1, status.BeatNumber);
        Assert.False(status.IsBeatWithinBarMeaningful);
    }

    [Theory]
    [InlineData(511, "--.-")]
    [InlineData(256, "63.4")]
    [InlineData(5, "01.1")]
    [InlineData(1, "00.1")]
    [InlineData(0, "00.0")]
    [InlineData(400, "??.?")]
    public void FormatsCueCountdown(int countdown, string expected)
    {
        var bytes = PacketFixtures.NexusCdjStatusPacket();
        Util.NumberToBytes(countdown, bytes, 0xa4, 2);
        var status = new CdjStatus(bytes, PacketFixtures.Address);
        Assert.Equal(countdown, status.CueCountdown);
        Assert.Equal(expected, status.FormatCueCountdown());
    }

    [Fact]
    public void MapsUnknownEnumValues()
    {
        var bytes = PacketFixtures.NexusCdjStatusPacket();
        bytes[0x29] = 9;    // unknown slot
        bytes[0x2a] = 9;    // unknown track type
        bytes[0x7b] = 0x50; // unknown play state 1
        bytes[0x8b] = 0x01; // unknown play state 2
        bytes[0x9d] = 0x50; // unknown play state 3
        var status = new CdjStatus(bytes, PacketFixtures.Address);
        Assert.Same(CdjStatus.TrackSourceSlot.Unknown, status.SourceSlot);
        Assert.Same(CdjStatus.TrackType.Unknown, status.LoadedTrackType);
        Assert.Same(CdjStatus.PlayState1.Unknown, status.FirstPlayState);
        Assert.Same(CdjStatus.PlayState2.Unknown, status.SecondPlayState);
        Assert.Same(CdjStatus.PlayState3.Unknown, status.ThirdPlayState);
    }

    [Theory]
    [InlineData(0x6a)]
    [InlineData(0x7a)]
    [InlineData(0xfa)]
    public void MapsMovingPlayState2Variants(byte value)
    {
        var bytes = PacketFixtures.NexusCdjStatusPacket();
        bytes[0x8b] = value;
        Assert.Same(CdjStatus.PlayState2.Moving, new CdjStatus(bytes, PacketFixtures.Address).SecondPlayState);
    }

    [Theory]
    [InlineData(0x6e)]
    [InlineData(0x7e)]
    [InlineData(0xfe)]
    public void MapsStoppedPlayState2Variants(byte value)
    {
        var bytes = PacketFixtures.NexusCdjStatusPacket();
        bytes[0x8b] = value;
        Assert.Same(CdjStatus.PlayState2.Stopped, new CdjStatus(bytes, PacketFixtures.Address).SecondPlayState);
    }

    [Fact]
    public void InfersPlayStateForPreNexusPackets()
    {
        // Pre-nexus packets are shorter than 0xd4 bytes, so the status flag byte cannot be used.
        var bytes = PacketFixtures.NexusCdjStatusPacket()[..0xd0];
        PacketFixtures.WriteName(bytes, 0x0b, "CDJ-2000");
        Util.NumberToBytes(0xd0 - 0x24, bytes, 0x22, 2);
        bytes[0x89] = 0;  // no flags reported at all
        var status = new CdjStatus(bytes, PacketFixtures.Address);
        Assert.True(status.IsPreNexusCdj);
        Assert.True(status.IsPlaying);  // inferred from play state 1 = playing

        bytes[0x7b] = 5;  // paused
        Assert.False(new CdjStatus(bytes, PacketFixtures.Address).IsPlaying);

        bytes[0x7b] = 9;  // searching, and play state 2 is moving
        Assert.True(new CdjStatus(bytes, PacketFixtures.Address).IsPlaying);
    }

    [Fact]
    public void CannotReportLoopingFromShortPackets()
    {
        var status = new CdjStatus(PacketFixtures.NexusCdjStatusPacket(), PacketFixtures.Address);
        Assert.False(status.CanReportLooping);
        Assert.Equal(0, status.ActiveLoopBeats);
        Assert.Equal(0, status.LoopStart);
        Assert.Equal(0, status.LoopEnd);
    }

    [Fact]
    public void ReportsLoopingFromCdj3000Packets()
    {
        var bytes = new byte[0x1fc];
        PacketFixtures.NexusCdjStatusPacket().CopyTo(bytes, 0);
        PacketFixtures.WriteName(bytes, 0x0b, "CDJ-3000");
        Util.NumberToBytes(0x1fc - 0x24, bytes, 0x22, 2);
        Util.NumberToBytes(2000, bytes, 0x1b6, 4);  // loop start (units of 1/65536 s)
        Util.NumberToBytes(4000, bytes, 0x1be, 4);  // loop end
        Util.NumberToBytes(8, bytes, 0x1c8, 2);     // loop beats
        var status = new CdjStatus(bytes, PacketFixtures.Address);
        Assert.True(status.CanReportLooping);
        Assert.Equal(8, status.ActiveLoopBeats);
        Assert.Equal(2000L * 65536 / 1000, status.LoopStart);
        Assert.Equal(4000L * 65536 / 1000, status.LoopEnd);
    }

    [Fact]
    public void HandlesOpusQuadStatusPackets()
    {
        var bytes = PacketFixtures.NexusCdjStatusPacket();
        PacketFixtures.WriteName(bytes, 0x0b, "OPUS-QUAD");
        bytes[0x21] = 9;   // Opus decks report 9-12
        bytes[0x28] = 9;   // and use the same range for the track source player
        var status = new CdjStatus(bytes, PacketFixtures.Address);
        Assert.True(status.IsFromOpusQuad);
        Assert.Equal(1, status.DeviceNumber);
        Assert.Equal(1, status.TrackSourcePlayer);
        Assert.Same(CdjStatus.TrackSourceSlot.UsbSlot, status.SourceSlot);
        // No metadata archive is attached (OpusProvider is only a stub), so the USB slot is reported empty,
        // and no DeviceSQL rekordbox ID can be found, so the ID is zeroed.
        Assert.True(status.IsLocalUsbEmpty);
        Assert.Equal(0, status.RekordboxId);
    }

    [Fact]
    public void RejectsTooShortPackets()
    {
        Assert.Throws<ArgumentException>(() => new CdjStatus(new byte[0xcb], PacketFixtures.Address));
    }

    [Fact]
    public void NestedEnumsExposeProtocolValues()
    {
        Assert.Equal(3, CdjStatus.TrackSourceSlot.UsbSlot.ProtocolValue);
        Assert.Equal(7, CdjStatus.TrackSourceSlot.Usb2Slot.ProtocolValue);
        Assert.Equal(5, CdjStatus.TrackType.CdDigitalAudio.ProtocolValue);
        Assert.Equal(0x12, CdjStatus.PlayState1.EmergencyLoop.ProtocolValue);
        Assert.Equal(0xfa, CdjStatus.PlayState2.OpusMoving.ProtocolValue);
        Assert.Equal(0x0b, CdjStatus.PlayState3.Slipping.ProtocolValue);
        Assert.Same(CdjStatus.TrackSourceSlot.Collection, CdjStatus.TrackSourceSlot.ProtocolValueMap[4]);
        Assert.Same(CdjStatus.PlayState1.Cued, CdjStatus.PlayState1.ProtocolValueMap[6]);
        Assert.Same(CdjStatus.PlayState3.ForwardCdj, CdjStatus.PlayState3.ProtocolValueMap[0x0d]);
    }
}

public class MixerStatusTests
{
    private static byte[] MixerPacket()
    {
        var bytes = PacketFixtures.NewPacket(0x38, 0x29, "DJM-900nexus", 0x0b);
        bytes[0x20] = 0;                                   // subtype
        bytes[0x21] = 33;                                  // device number
        Util.NumberToBytes(0x38 - 0x24, bytes, 0x22, 2);   // payload length
        bytes[0x27] = 0x30;                                // master and synced flags
        Util.NumberToBytes(Util.NeutralPitch, bytes, 0x28, 4);
        Util.NumberToBytes(12800, bytes, 0x2e, 2);         // BPM * 100
        bytes[0x36] = 0xff;                                // not handing off master
        bytes[0x37] = 2;                                   // beat within bar
        return bytes;
    }

    [Fact]
    public void ParsesMixerStatusPacket()
    {
        var status = new MixerStatus(MixerPacket(), PacketFixtures.Address);
        Assert.Equal("DJM-900nexus", status.DeviceName);
        Assert.Equal(33, status.DeviceNumber);
        Assert.Equal((long)Util.NeutralPitch, status.Pitch);
        Assert.Equal(12800, status.Bpm);
        Assert.Equal(128.0, status.EffectiveTempo, 10);
        Assert.Equal(2, status.BeatWithinBar);
        Assert.False(status.IsBeatWithinBarMeaningful);
        Assert.True(status.IsTempoMaster);
        Assert.True(status.IsSynced);
        Assert.Null(status.DeviceMasterIsBeingYieldedTo);
    }

    [Fact]
    public void ReportsMasterHandoff()
    {
        var bytes = MixerPacket();
        bytes[0x36] = 4;
        Assert.Equal(4, new MixerStatus(bytes, PacketFixtures.Address).DeviceMasterIsBeingYieldedTo);
    }

    [Fact]
    public void ReportsClearedFlags()
    {
        var bytes = MixerPacket();
        bytes[0x27] = 0;
        var status = new MixerStatus(bytes, PacketFixtures.Address);
        Assert.False(status.IsTempoMaster);
        Assert.False(status.IsSynced);
    }

    [Fact]
    public void RejectsTooShortPackets()
    {
        Assert.Throws<ArgumentException>(() => new MixerStatus(new byte[0x37], PacketFixtures.Address));
    }
}

// In the network-services collection because QueriesRequireVirtualCdj depends on the VirtualCdj
// being stopped, which cannot be guaranteed while tests that start it run in parallel.
[Collection("Network services")]
public class PrecisePositionTests
{
    private static byte[] PositionPacket(long rawPitch)
    {
        var bytes = PacketFixtures.NewPacket(0x3c, 0x0b, "CDJ-3000", 0x0b);
        bytes[0x21] = 1;                                 // device number
        Util.NumberToBytes(300, bytes, 0x24, 4);         // track length in seconds
        Util.NumberToBytes(45123, bytes, 0x28, 4);       // playback position in ms
        Util.NumberToBytes(rawPitch & 0xffffffffL, bytes, 0x2c, 4);  // pitch percentage * 100
        Util.NumberToBytes(1285, bytes, 0x38, 4);        // BPM * 10
        return bytes;
    }

    [Fact]
    public void ParsesPositionPacket()
    {
        var position = new PrecisePosition(PositionPacket(500), PacketFixtures.Address);
        Assert.Equal("CDJ-3000", position.DeviceName);
        Assert.Equal(1, position.DeviceNumber);
        Assert.Equal(300, position.TrackLength);
        Assert.Equal(45123, position.PlaybackPosition);
        Assert.Equal(128.5, position.EffectiveTempo, 10);
        Assert.Null(position.DeviceMasterIsBeingYieldedTo);
    }

    [Fact]
    public void ConvertsPositivePitchPercentage()
    {
        var position = new PrecisePosition(PositionPacket(500), PacketFixtures.Address);
        Assert.Equal(1101005, position.Pitch);  // +5% above NeutralPitch (1048576)
        Assert.Equal(12238, position.Bpm);      // 12850 divided back down by the pitch multiplier
    }

    [Fact]
    public void SignExtendsNegativePitch()
    {
        // A raw value at or above 0x80000000 is a negative two's-complement effective tempo.
        var position = new PrecisePosition(PositionPacket(-500), PacketFixtures.Address);
        Assert.Equal(996147, position.Pitch);   // -5% below NeutralPitch
        Assert.Equal(13526, position.Bpm);
    }

    [Fact]
    public void QueriesRequireVirtualCdj()
    {
        VirtualCdj.Instance.ResetForTests();  // These properties throw only while it is stopped.
        var position = new PrecisePosition(PositionPacket(0), PacketFixtures.Address);
        Assert.Throws<InvalidOperationException>(() => position.IsTempoMaster);
        Assert.Throws<InvalidOperationException>(() => position.IsSynced);
        Assert.Throws<InvalidOperationException>(() => position.BeatWithinBar);
        Assert.Throws<InvalidOperationException>(() => position.IsBeatWithinBarMeaningful);
    }

    [Fact]
    public void RejectsWrongLengthPackets()
    {
        Assert.Throws<ArgumentException>(() => new PrecisePosition(new byte[0x3b], PacketFixtures.Address));
        Assert.Throws<ArgumentException>(() => new PrecisePosition(new byte[0x3d], PacketFixtures.Address));
    }
}

// OpusProvider is a process-wide singleton that the VirtualRekordbox tests start, and the
// synthetic-constructor test below expects to find it stopped. xUnit runs test classes in
// parallel, so this class joins the collection that serialises the singleton-service tests.
[Collection("Network services")]
public class MediaDetailsTests
{
    private static byte[] MediaResponsePacket(byte hostPlayer)
    {
        var bytes = PacketFixtures.NewPacket(0xc0, 0x06, "CDJ-2000nexus", 0x0b);
        Util.NumberToBytes(0xc0 - 0x24, bytes, 0x22, 2);   // payload length
        bytes[0x27] = hostPlayer;
        bytes[0x2b] = 3;                                   // USB slot
        Encoding.BigEndianUnicode.GetBytes("BEAT LINK").CopyTo(bytes, 0x2c);
        Encoding.BigEndianUnicode.GetBytes("2024-01-15").CopyTo(bytes, 0x6c);
        Util.NumberToBytes(742, bytes, 0xa6, 2);           // track count
        bytes[0xa8] = 5;                                   // color: green
        bytes[0xaa] = 1;                                   // media type: rekordbox
        bytes[0xab] = 1;                                   // has my settings
        Util.NumberToBytes(25, bytes, 0xae, 2);            // playlist count
        Util.NumberToBytes(1_000_000_000, bytes, 0xb0, 8); // total size
        Util.NumberToBytes(250_000_000, bytes, 0xb8, 8);   // free space
        return bytes;
    }

    [Fact]
    public void ParsesMediaResponsePacket()
    {
        var details = new MediaDetails(MediaResponsePacket(2));
        Assert.Same(SlotReference.GetSlotReference(2, CdjStatus.TrackSourceSlot.UsbSlot), details.SlotReference);
        Assert.Same(CdjStatus.TrackType.Rekordbox, details.MediaType);
        Assert.Equal("BEAT LINK", details.Name);
        Assert.Equal("2024-01-15", details.CreationDate);
        Assert.Equal(742, details.TrackCount);
        Assert.Equal(25, details.PlaylistCount);
        Assert.Equal(ArgbColor.FromRgb(0, 255, 0), details.Color);
        Assert.True(details.HasMySettings);
        Assert.Equal(1_000_000_000, details.TotalSize);
        Assert.Equal(250_000_000, details.FreeSpace);
        Assert.Equal(0xc0, details.RawBytes.Length);
    }

    [Fact]
    public void RecognizesRekordboxMobile()
    {
        var details = new MediaDetails(MediaResponsePacket(41));
        Assert.Equal("rekordbox mobile", details.Name);
        Assert.Equal("", details.CreationDate);
    }

    [Fact]
    public void ClampsUnterminatedStrings()
    {
        var bytes = MediaResponsePacket(2);
        // Fill the whole 0x40-byte name field with non-NUL UTF-16BE characters.
        Encoding.BigEndianUnicode.GetBytes(new string('X', 32)).CopyTo(bytes, 0x2c);
        var details = new MediaDetails(bytes);
        Assert.Equal(new string('X', 32), details.Name);
    }

    [Fact]
    public void ReportsNoColorAsTransparent()
    {
        var bytes = MediaResponsePacket(2);
        bytes[0xa8] = 0;
        Assert.True(ColorItem.IsNoColor(new MediaDetails(bytes).Color));
    }

    [Fact]
    public void ComputesHashKeyAndChangeDetection()
    {
        var details = new MediaDetails(MediaResponsePacket(2));
        Assert.Equal("2024-01-15:REKORDBOX:1000000000:BEAT LINK", details.HashKey());
        Assert.False(details.HasChanged(new MediaDetails(MediaResponsePacket(2))));

        var changed = MediaResponsePacket(2);
        Util.NumberToBytes(26, changed, 0xae, 2);
        Assert.True(details.HasChanged(new MediaDetails(changed)));

        var renamed = MediaResponsePacket(2);
        Encoding.BigEndianUnicode.GetBytes("OTHER\0").CopyTo(renamed, 0x2c);
        Assert.Throws<ArgumentException>(() => details.HasChanged(new MediaDetails(renamed)));
    }

    [Fact]
    public void RejectsBadPackets()
    {
        Assert.Throws<ArgumentException>(() => new MediaDetails(new byte[0xbf]));

        var badSlot = MediaResponsePacket(2);
        badSlot[0x2b] = 9;
        Assert.Throws<ArgumentException>(() => new MediaDetails(badSlot));

        var badType = MediaResponsePacket(2);
        badType[0xaa] = 9;
        Assert.Throws<ArgumentException>(() => new MediaDetails(badType));
    }

    [Fact]
    public void SyntheticConstructorRequiresOpusProvider()
    {
        Assert.Throws<InvalidOperationException>(() => new MediaDetails(
            SlotReference.GetSlotReference(1, CdjStatus.TrackSourceSlot.UsbSlot),
            CdjStatus.TrackType.Rekordbox, "USB", 10, 2, 12345));
    }
}

public class SlotReferenceTests
{
    [Fact]
    public void InternsInstances()
    {
        var first = SlotReference.GetSlotReference(3, CdjStatus.TrackSourceSlot.SdSlot);
        var second = SlotReference.GetSlotReference(3, CdjStatus.TrackSourceSlot.SdSlot);
        Assert.Same(first, second);
        Assert.Equal(3, first.Player);
        Assert.Same(CdjStatus.TrackSourceSlot.SdSlot, first.Slot);
        Assert.NotSame(first, SlotReference.GetSlotReference(4, CdjStatus.TrackSourceSlot.SdSlot));
        Assert.NotSame(first, SlotReference.GetSlotReference(3, CdjStatus.TrackSourceSlot.UsbSlot));
    }
}
