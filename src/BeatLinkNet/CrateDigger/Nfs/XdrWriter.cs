using System.Buffers.Binary;
using System.Text;

namespace BeatLink.CrateDigger.Nfs;

/// <summary>
/// Serializes values using the XDR (External Data Representation) encoding defined by RFC 4506
/// (originally RFC 1014), as used by the ONC RPC, portmap, mount, and NFS version 2 protocols.
/// All quantities are big-endian and everything is padded to four-byte alignment.
/// </summary>
/// <remarks>
/// This is a minimal hand-written replacement for the XDR layer of the Remote Tea library used by
/// Deep Symmetry's crate-digger, supporting only the types needed to talk to Pioneer players.
/// </remarks>
public sealed class XdrWriter
{
    private readonly MemoryStream _stream = new();

    /// <summary>
    /// The number of bytes that have been written so far.
    /// </summary>
    public int Length => (int)_stream.Length;

    /// <summary>
    /// Write an XDR unsigned integer (four bytes, big-endian).
    /// </summary>
    /// <param name="value">the value to append</param>
    public void WriteUInt32(uint value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(buffer, value);
        _stream.Write(buffer);
    }

    /// <summary>
    /// Write an XDR signed integer (four bytes, big-endian, two's complement).
    /// </summary>
    /// <param name="value">the value to append</param>
    public void WriteInt32(int value) => WriteUInt32(unchecked((uint)value));

    /// <summary>
    /// Write an XDR unsigned hyper integer (eight bytes, big-endian).
    /// </summary>
    /// <param name="value">the value to append</param>
    public void WriteUInt64(ulong value)
    {
        Span<byte> buffer = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(buffer, value);
        _stream.Write(buffer);
    }

    /// <summary>
    /// Write an XDR boolean, which is encoded as an integer holding zero or one.
    /// </summary>
    /// <param name="value">the value to append</param>
    public void WriteBool(bool value) => WriteUInt32(value ? 1u : 0u);

    /// <summary>
    /// Write fixed-length opaque data: the bytes themselves (their length is implied by the protocol),
    /// padded with zeros to a multiple of four bytes.
    /// </summary>
    /// <param name="data">the bytes to append</param>
    public void WriteFixedOpaque(ReadOnlySpan<byte> data)
    {
        _stream.Write(data);
        var remainder = data.Length & 3;
        if (remainder != 0)
        {
            ReadOnlySpan<byte> padding = stackalloc byte[3] { 0, 0, 0 };
            _stream.Write(padding[..(4 - remainder)]);
        }
    }

    /// <summary>
    /// Write variable-length opaque data: a four-byte length followed by the bytes themselves,
    /// padded with zeros to a multiple of four bytes.
    /// </summary>
    /// <param name="data">the bytes to append</param>
    public void WriteOpaque(ReadOnlySpan<byte> data)
    {
        WriteUInt32((uint)data.Length);
        WriteFixedOpaque(data);
    }

    /// <summary>
    /// Write an XDR string, which has the same wire format as variable-length opaque data.
    /// </summary>
    /// <param name="value">the text to append</param>
    /// <param name="encoding">the encoding with which the text should be turned into bytes;
    /// Pioneer players expect UTF-16 little-endian for file paths</param>
    public void WriteString(string value, Encoding encoding)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(encoding);
        WriteOpaque(encoding.GetBytes(value));
    }

    /// <summary>
    /// Return a copy of all the bytes that have been written.
    /// </summary>
    /// <returns>the accumulated XDR data</returns>
    public byte[] ToArray() => _stream.ToArray();
}
