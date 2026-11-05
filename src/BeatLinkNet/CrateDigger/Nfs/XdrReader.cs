using System.Buffers.Binary;
using System.Text;

namespace BeatLink.CrateDigger.Nfs;

/// <summary>
/// Deserializes values from the XDR (External Data Representation) encoding defined by RFC 4506
/// (originally RFC 1014), as used by the ONC RPC, portmap, mount, and NFS version 2 protocols.
/// All quantities are big-endian and everything is padded to four-byte alignment.
/// </summary>
/// <remarks>
/// This is a minimal hand-written replacement for the XDR layer of the Remote Tea library used by
/// Deep Symmetry's crate-digger, supporting only the types needed to talk to Pioneer players.
/// </remarks>
public sealed class XdrReader
{
    private readonly ReadOnlyMemory<byte> _data;
    private int _position;

    /// <summary>
    /// Create a reader which will decode the supplied XDR data.
    /// </summary>
    /// <param name="data">the encoded bytes to be read</param>
    public XdrReader(ReadOnlyMemory<byte> data)
    {
        _data = data;
    }

    /// <summary>
    /// The offset within the data at which the next value will be read.
    /// </summary>
    public int Position => _position;

    /// <summary>
    /// The number of bytes which have not yet been consumed.
    /// </summary>
    public int Remaining => _data.Length - _position;

    /// <summary>
    /// Consume the specified number of raw bytes, throwing if the data is exhausted first.
    /// </summary>
    private ReadOnlySpan<byte> Take(int count)
    {
        if (count < 0 || count > Remaining)
        {
            throw new IOException($"Attempt to read {count} bytes from XDR data with only {Remaining} bytes remaining.");
        }
        var result = _data.Span.Slice(_position, count);
        _position += count;
        return result;
    }

    /// <summary>
    /// Read an XDR unsigned integer (four bytes, big-endian).
    /// </summary>
    /// <returns>the decoded value</returns>
    /// <exception cref="IOException">if the data is exhausted</exception>
    public uint ReadUInt32() => BinaryPrimitives.ReadUInt32BigEndian(Take(4));

    /// <summary>
    /// Read an XDR signed integer (four bytes, big-endian, two's complement).
    /// </summary>
    /// <returns>the decoded value</returns>
    /// <exception cref="IOException">if the data is exhausted</exception>
    public int ReadInt32() => unchecked((int)ReadUInt32());

    /// <summary>
    /// Read an XDR unsigned hyper integer (eight bytes, big-endian).
    /// </summary>
    /// <returns>the decoded value</returns>
    /// <exception cref="IOException">if the data is exhausted</exception>
    public ulong ReadUInt64() => BinaryPrimitives.ReadUInt64BigEndian(Take(8));

    /// <summary>
    /// Read an XDR boolean, which is encoded as an integer holding zero or one; any nonzero
    /// value is treated as true.
    /// </summary>
    /// <returns>the decoded value</returns>
    /// <exception cref="IOException">if the data is exhausted</exception>
    public bool ReadBool() => ReadUInt32() != 0;

    /// <summary>
    /// Read fixed-length opaque data of a length implied by the protocol, consuming (and discarding)
    /// the zero padding which aligns it to a multiple of four bytes.
    /// </summary>
    /// <param name="length">the number of meaningful bytes to read</param>
    /// <returns>the decoded bytes, without padding</returns>
    /// <exception cref="IOException">if the data is exhausted</exception>
    public byte[] ReadFixedOpaque(int length)
    {
        var result = Take(length).ToArray();
        var remainder = length & 3;
        if (remainder != 0)
        {
            Take(4 - remainder);
        }
        return result;
    }

    /// <summary>
    /// Read variable-length opaque data: a four-byte length followed by the bytes themselves and
    /// zero padding to a multiple of four bytes.
    /// </summary>
    /// <returns>the decoded bytes, without padding</returns>
    /// <exception cref="IOException">if the data is exhausted or the length is implausible</exception>
    public byte[] ReadOpaque()
    {
        var length = ReadUInt32();
        if (length > (uint)Remaining)
        {
            throw new IOException($"XDR opaque data claims length {length} but only {Remaining} bytes remain.");
        }
        return ReadFixedOpaque((int)length);
    }

    /// <summary>
    /// Read an XDR string, which has the same wire format as variable-length opaque data.
    /// </summary>
    /// <param name="encoding">the encoding with which the bytes should be turned into text;
    /// Pioneer players use UTF-16 little-endian for file paths</param>
    /// <returns>the decoded text</returns>
    /// <exception cref="IOException">if the data is exhausted or the length is implausible</exception>
    public string ReadString(Encoding encoding)
    {
        ArgumentNullException.ThrowIfNull(encoding);
        return encoding.GetString(ReadOpaque());
    }
}
