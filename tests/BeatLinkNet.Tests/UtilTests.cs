using System.Net;

namespace BeatLink.Tests;

public class UtilTests
{
    [Fact]
    public void MagicHeaderSpellsQspt1WmJOL()
    {
        Assert.Equal("Qspt1WmJOL"u8.ToArray(), Util.MagicHeader.ToArray());
    }

    [Theory]
    [InlineData(Util.NeutralPitch, 0.0)]
    [InlineData(0, -100.0)]
    [InlineData(Util.NeutralPitch * 2, 100.0)]
    public void PitchToPercentageMatchesProtocol(long pitch, double expected)
    {
        Assert.Equal(expected, Util.PitchToPercentage(pitch), 10);
    }

    [Theory]
    [InlineData(0.0, Util.NeutralPitch)]
    [InlineData(-100.0, 0)]
    [InlineData(100.0, Util.NeutralPitch * 2)]
    public void PercentageToPitchMatchesProtocol(double percentage, long expected)
    {
        Assert.Equal(expected, Util.PercentageToPitch(percentage));
    }

    [Fact]
    public void PitchRoundTrips()
    {
        for (var percentage = -100.0; percentage <= 100.0; percentage += 7.3)
        {
            Assert.Equal(percentage, Util.PitchToPercentage(Util.PercentageToPitch(percentage)), 4);
        }
    }

    [Theory]
    [InlineData(Util.NeutralPitch, 1.0)]
    [InlineData(0, 0.0)]
    [InlineData(Util.NeutralPitch * 2, 2.0)]
    public void PitchToMultiplierMatchesProtocol(long pitch, double expected)
    {
        Assert.Equal(expected, Util.PitchToMultiplier(pitch), 10);
    }

    [Fact]
    public void BytesToNumberBigEndian()
    {
        byte[] buffer = [0x00, 0x01, 0x02, 0x03, 0xff];
        Assert.Equal(0x010203L, Util.BytesToNumber(buffer, 1, 3));
        Assert.Equal(0x010203ffL, Util.BytesToNumber(buffer, 1, 4));
        Assert.Equal(0xffL, Util.BytesToNumber(buffer, 4, 1));
    }

    [Fact]
    public void BytesToNumberLittleEndian()
    {
        byte[] buffer = [0x00, 0x01, 0x02, 0x03, 0xff];
        Assert.Equal(0x030201L, Util.BytesToNumberLittleEndian(buffer, 1, 3));
        Assert.Equal(0xff030201L, Util.BytesToNumberLittleEndian(buffer, 1, 4));
    }

    [Fact]
    public void NumberToBytesRoundTrips()
    {
        var buffer = new byte[4];
        Util.NumberToBytes(0x12345678L, buffer, 0, 4);
        Assert.Equal(new byte[] { 0x12, 0x34, 0x56, 0x78 }, buffer);
        Assert.Equal(0x12345678L, Util.BytesToNumber(buffer, 0, 4));

        // Values too large for the field only write low-order bytes.
        var small = new byte[2];
        Util.NumberToBytes(0x12345678L, small, 0, 2);
        Assert.Equal(new byte[] { 0x56, 0x78 }, small);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(150, 1000)]  // one second is 150 half-frames
    [InlineData(15, 100)]
    public void HalfFrameToTimeMatchesProtocol(long halfFrame, long expectedMs)
    {
        Assert.Equal(expectedMs, Util.HalfFrameToTime(halfFrame));
    }

    [Theory]
    [InlineData(1000, 150)]
    [InlineData(0, 0)]
    [InlineData(999, 149)]  // truncates
    public void TimeToHalfFrameMatchesProtocol(long ms, int expected)
    {
        Assert.Equal(expected, Util.TimeToHalfFrame(ms));
    }

    [Fact]
    public void TimeToHalfFrameRoundedRounds()
    {
        Assert.Equal(150, Util.TimeToHalfFrameRounded(999));
        Assert.Equal(150, Util.TimeToHalfFrameRounded(1000));
    }

    [Fact]
    public void ValidateHeaderRecognizesKnownPackets()
    {
        var packet = new byte[0x25];
        Util.MagicHeader.CopyTo(packet);
        packet[Util.PacketTypeOffset] = 0x28;
        Assert.Same(PacketType.Beat, Util.ValidateHeader(packet, Ports.BeatPort));

        // The same type byte means different things on different ports.
        packet[Util.PacketTypeOffset] = 0x0a;
        Assert.Same(PacketType.DeviceHello, Util.ValidateHeader(packet, Ports.AnnouncementPort));
        Assert.Same(PacketType.CdjStatus, Util.ValidateHeader(packet, Ports.UpdatePort));
    }

    [Fact]
    public void ValidateHeaderRejectsBadPackets()
    {
        Assert.Null(Util.ValidateHeader(new byte[4], Ports.BeatPort));  // too short

        var wrongMagic = new byte[0x25];
        wrongMagic[0] = 0x99;
        Assert.Null(Util.ValidateHeader(wrongMagic, Ports.BeatPort));

        var unknownType = new byte[0x25];
        Util.MagicHeader.CopyTo(unknownType);
        unknownType[Util.PacketTypeOffset] = 0x7f;
        Assert.Null(Util.ValidateHeader(unknownType, Ports.BeatPort));
        Assert.Null(Util.ValidateHeader(unknownType, 12345));  // unknown port
    }

    [Fact]
    public void BuildPacketProducesCorrectLayout()
    {
        var deviceName = new byte[0x14];
        "beat-link"u8.CopyTo(deviceName);
        byte[] payload = [0x01, 0x02, 0x03];
        var packet = Util.BuildPacket(PacketType.Beat, deviceName, payload);

        Assert.Equal(0x1f + 3, packet.Length);
        Assert.Equal(Util.MagicHeader.ToArray(), packet[..10]);
        Assert.Equal(0x28, packet[Util.PacketTypeOffset]);
        Assert.Equal(deviceName, packet[0x0b..0x1f]);
        Assert.Equal(payload, packet[0x1f..]);
    }

    [Fact]
    public void SetPayloadByteAdjustsForHeader()
    {
        var payload = new byte[8];
        Util.SetPayloadByte(payload, 0x21, 0x42);
        Assert.Equal(0x42, payload[2]);
    }

    [Fact]
    public void SameNetworkComparesPrefixes()
    {
        var a = IPAddress.Parse("192.168.1.25");
        var b = IPAddress.Parse("192.168.1.200");
        var c = IPAddress.Parse("192.168.2.25");
        Assert.True(Util.SameNetwork(24, a, b));
        Assert.False(Util.SameNetwork(24, a, c));
        Assert.True(Util.SameNetwork(16, a, c));
    }

    [Fact]
    public void AddressToLongConverts()
    {
        Assert.Equal(0xC0A80119L, Util.AddressToLong(IPAddress.Parse("192.168.1.25")));
    }

    [Fact]
    public void IndexOfBytesFindsPatterns()
    {
        byte[] buffer = [0x01, 0x02, 0x03, 0x04, 0x05];
        Assert.Equal(2, Util.IndexOfBytes(buffer, [0x03, 0x04]));
        Assert.Equal(-1, Util.IndexOfBytes(buffer, [0x06]));
        Assert.Equal(-1, Util.IndexOfBytes(buffer, []));
    }

    [Fact]
    public void HighResolutionPathInsertsSuffix()
    {
        Assert.Equal("/PIONEER/ARTWORK/00001/a_m.jpg", Util.HighResolutionPath("/PIONEER/ARTWORK/00001/a.jpg"));
    }

    [Theory]
    [InlineData(9, 1)]
    [InlineData(10, 2)]
    [InlineData(11, 3)]
    [InlineData(12, 4)]
    public void TranslateOpusPlayerNumbersMapsToDecks(int reported, int expected)
    {
        Assert.Equal(expected, Util.TranslateOpusPlayerNumbers(reported));
    }

    [Fact]
    public void NamedLocksAreSharedWhileInUse()
    {
        var first = Util.AllocateNamedLock("test-resource");
        var second = Util.AllocateNamedLock("test-resource");
        Assert.Same(first, second);
        Util.FreeNamedLock("test-resource");
        Util.FreeNamedLock("test-resource");
        var third = Util.AllocateNamedLock("test-resource");
        Assert.NotSame(first, third);
        Util.FreeNamedLock("test-resource");
    }

    [Fact]
    public void DeviceReferenceInternsInstances()
    {
        var address = IPAddress.Parse("192.168.1.25");
        var first = DeviceReference.GetDeviceReference(2, address);
        var second = DeviceReference.GetDeviceReference(2, IPAddress.Parse("192.168.1.25"));
        Assert.Same(first, second);
        Assert.NotSame(first, DeviceReference.GetDeviceReference(3, address));
    }
}
