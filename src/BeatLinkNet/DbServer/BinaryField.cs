namespace BeatLink.DbServer;

/// <summary>
/// A binary field holds an arbitrary sequence of bytes whose length is determined by the 4-byte big-endian
/// integer that follows the type tag.
/// </summary>
/// <remarks>
/// Ported from Deep Symmetry's beat-link <c>BinaryField</c>, by James Elliott.
/// </remarks>
public sealed class BinaryField : Field
{
    private const byte BinaryTypeTag = 0x14;

    private readonly int _size;

    /// <summary>
    /// Holds the actual bytes used to transmit this field, including the type tag. Handles the special case of
    /// a zero-length blob, for which no bytes at all are sent, not even the tag and length header.
    /// (Really! The protocol requires this!)
    /// </summary>
    private readonly byte[] _buffer;

    /// <summary>
    /// The bytes which represent the payload of this field, without the leading type tag and length header.
    /// </summary>
    public ReadOnlyMemory<byte> Value => _buffer.Length > 0 ? _buffer.AsMemory(5) : ReadOnlyMemory<byte>.Empty;

    /// <summary>
    /// Get the bytes which represent the payload of this field, without the leading type tag and length header,
    /// as a newly-allocated byte array.
    /// </summary>
    /// <returns>a new byte array containing a copy of the bytes this field contains</returns>
    public byte[] GetValueAsArray() => Value.ToArray();

    /// <summary>
    /// Constructor for reading from the network.
    /// </summary>
    /// <param name="stream">the stream on which the field value is to be read</param>
    /// <exception cref="IOException">if there is a problem reading the value</exception>
    public BinaryField(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var sizeBytes = new byte[4];
        ReadFully(stream, sizeBytes, 0, 4);
        _size = (int)Util.BytesToNumber(sizeBytes, 0, 4);
        _buffer = new byte[_size + 5];
        _buffer[0] = BinaryTypeTag;
        Array.Copy(sizeBytes, 0, _buffer, 1, 4);
        ReadFully(stream, _buffer, 5, _size);
    }

    /// <summary>
    /// Constructor for code.
    /// </summary>
    /// <param name="bytes">the value that this field will convey</param>
    public BinaryField(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        _size = bytes.Length;
        if (_size > 0)
        {
            _buffer = new byte[_size + 5];
            _buffer[0] = BinaryTypeTag;
            Util.NumberToBytes(_size, _buffer, 1, 4);
            Array.Copy(bytes, 0, _buffer, 5, _size);
        }
        else
        {
            _buffer = [];  // A zero-length blob sends no bytes at all, not even the tag and length header.
        }
    }

    /// <inheritdoc/>
    public override byte TypeTag => BinaryTypeTag;

    /// <inheritdoc/>
    public override byte ArgumentTag => 0x03;

    /// <inheritdoc/>
    public override ReadOnlyMemory<byte> GetBytes() => _buffer;

    /// <inheritdoc/>
    public override long Size => _size;

    /// <inheritdoc/>
    public override string ToString() => $"BinaryField[ size: {_size}, bytes: {GetHexString()}]";
}
