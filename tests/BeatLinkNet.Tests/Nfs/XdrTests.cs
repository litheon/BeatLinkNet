using System.Text;
using BeatLink.CrateDigger.Nfs;

namespace BeatLink.Tests.Nfs;

public class XdrTests
{
    [Fact]
    public void UnsignedIntegerIsBigEndian()
    {
        var writer = new XdrWriter();
        writer.WriteUInt32(0x01020304);
        Assert.Equal(new byte[] { 0x01, 0x02, 0x03, 0x04 }, writer.ToArray());
    }

    [Fact]
    public void SignedIntegerUsesTwosComplement()
    {
        var writer = new XdrWriter();
        writer.WriteInt32(-2);
        Assert.Equal(new byte[] { 0xff, 0xff, 0xff, 0xfe }, writer.ToArray());
        Assert.Equal(-2, new XdrReader(writer.ToArray()).ReadInt32());
    }

    [Fact]
    public void UnsignedHyperIsBigEndian()
    {
        var writer = new XdrWriter();
        writer.WriteUInt64(0x0102030405060708);
        Assert.Equal(new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08 }, writer.ToArray());
        Assert.Equal(0x0102030405060708ul, new XdrReader(writer.ToArray()).ReadUInt64());
    }

    [Theory]
    [InlineData(true, 1u)]
    [InlineData(false, 0u)]
    public void BooleanIsEncodedAsInteger(bool value, uint expected)
    {
        var writer = new XdrWriter();
        writer.WriteBool(value);
        Assert.Equal(expected, new XdrReader(writer.ToArray()).ReadUInt32());
        Assert.Equal(value, new XdrReader(writer.ToArray()).ReadBool());
    }

    [Fact]
    public void VariableOpaqueIsPaddedToFourByteAlignment()
    {
        var writer = new XdrWriter();
        writer.WriteOpaque("abcde"u8);
        Assert.Equal(new byte[]
        {
            0x00, 0x00, 0x00, 0x05,        // Length.
            0x61, 0x62, 0x63, 0x64, 0x65,  // Data.
            0x00, 0x00, 0x00               // Padding to a multiple of four bytes.
        }, writer.ToArray());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(32)]
    public void OpaqueRoundTripsAtEveryAlignment(int length)
    {
        var data = new byte[length];
        Random.Shared.NextBytes(data);

        var writer = new XdrWriter();
        writer.WriteOpaque(data);
        writer.WriteUInt32(0xdeadbeef);  // A sentinel to prove the padding was fully consumed.
        Assert.Equal(0, writer.Length % 4);

        var reader = new XdrReader(writer.ToArray());
        Assert.Equal(data, reader.ReadOpaque());
        Assert.Equal(0xdeadbeef, reader.ReadUInt32());
        Assert.Equal(0, reader.Remaining);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(7)]
    public void FixedOpaqueRoundTripsWithPadding(int length)
    {
        var data = new byte[length];
        Random.Shared.NextBytes(data);

        var writer = new XdrWriter();
        writer.WriteFixedOpaque(data);
        writer.WriteUInt32(0xcafebabe);
        Assert.Equal(0, writer.Length % 4);

        var reader = new XdrReader(writer.ToArray());
        Assert.Equal(data, reader.ReadFixedOpaque(length));
        Assert.Equal(0xcafebabe, reader.ReadUInt32());
        Assert.Equal(0, reader.Remaining);
    }

    [Fact]
    public void StringRoundTripsInPlayerEncoding()
    {
        var writer = new XdrWriter();
        writer.WriteString("PIONEER", Encoding.Unicode);
        var reader = new XdrReader(writer.ToArray());
        Assert.Equal("PIONEER", reader.ReadString(Encoding.Unicode));
        Assert.Equal(0, reader.Remaining);
    }

    [Fact]
    public void StringUsesOpaqueWireFormat()
    {
        var writer = new XdrWriter();
        writer.WriteString("B", Encoding.Unicode);
        Assert.Equal(new byte[]
        {
            0x00, 0x00, 0x00, 0x02,  // Two bytes of UTF-16LE.
            0x42, 0x00,              // 'B' in UTF-16LE.
            0x00, 0x00               // Padding.
        }, writer.ToArray());
    }

    [Fact]
    public void ReadingPastEndThrowsIOException()
    {
        var reader = new XdrReader(new byte[] { 0x00, 0x00 });
        Assert.Throws<IOException>(() => reader.ReadUInt32());
    }

    [Fact]
    public void ImplausibleOpaqueLengthThrowsIOException()
    {
        var reader = new XdrReader(new byte[] { 0xff, 0xff, 0xff, 0xff, 0x00, 0x00, 0x00, 0x00 });
        Assert.Throws<IOException>(() => reader.ReadOpaque());
    }

    /// <summary>
    /// A hand-computed golden vector for a full ONC RPC call message, following RFC 1057 section 8:
    /// a portmapper GETPORT request asking where the NFS program is listening.
    /// </summary>
    [Fact]
    public void RpcCallMessageMatchesRfc1057Layout()
    {
        var message = OncRpcUdpClient.BuildCall(0x12345678, PortmapperClient.Program, PortmapperClient.Version, 3, writer =>
        {
            writer.WriteUInt32(NfsClient.Program);
            writer.WriteUInt32(NfsClient.Version);
            writer.WriteUInt32(PortmapperClient.IpProtocolUdp);
            writer.WriteUInt32(0);
        });

        Assert.Equal(new byte[]
        {
            0x12, 0x34, 0x56, 0x78,  // xid
            0x00, 0x00, 0x00, 0x00,  // msg_type CALL
            0x00, 0x00, 0x00, 0x02,  // rpcvers 2
            0x00, 0x01, 0x86, 0xa0,  // prog 100000 (portmapper)
            0x00, 0x00, 0x00, 0x02,  // vers 2
            0x00, 0x00, 0x00, 0x03,  // proc 3 (GETPORT)
            0x00, 0x00, 0x00, 0x00,  // cred flavor AUTH_NONE
            0x00, 0x00, 0x00, 0x00,  // cred body length 0
            0x00, 0x00, 0x00, 0x00,  // verf flavor AUTH_NONE
            0x00, 0x00, 0x00, 0x00,  // verf body length 0
            0x00, 0x01, 0x86, 0xa3,  // arg: prog 100003 (NFS)
            0x00, 0x00, 0x00, 0x02,  // arg: vers 2
            0x00, 0x00, 0x00, 0x11,  // arg: prot 17 (UDP)
            0x00, 0x00, 0x00, 0x00,  // arg: port (ignored)
        }, message);
    }
}
