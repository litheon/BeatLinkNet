using System.Text;

namespace BeatLink.DbServer;

/// <summary>
/// A string field holds a UTF-16BE encoded string whose length is determined by the 4-byte big-endian
/// integer that follows the type tag. The length header counts characters, not bytes, and the wire
/// representation carries a trailing NUL character which is stripped from the value.
/// </summary>
/// <remarks>
/// Ported from Deep Symmetry's beat-link <c>StringField</c>, by James Elliott.
/// </remarks>
public sealed class StringField : Field
{
    private const byte StringTypeTag = 0x26;

    private readonly int _size;

    /// <summary>
    /// Holds the actual bytes used to transmit this field, including the type tag.
    /// </summary>
    private readonly byte[] _buffer;

    /// <summary>
    /// The string represented by this field.
    /// </summary>
    public string Value { get; }

    /// <summary>
    /// Constructor for reading from the network.
    /// </summary>
    /// <param name="stream">the stream on which the field value is to be read</param>
    /// <exception cref="IOException">if there is a problem reading the value</exception>
    public StringField(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var sizeBytes = new byte[4];
        ReadFully(stream, sizeBytes, 0, 4);
        _size = (int)Util.BytesToNumber(sizeBytes, 0, 4) * 2;  // Network gets size in characters.
        _buffer = new byte[_size + 5];
        _buffer[0] = StringTypeTag;
        Array.Copy(sizeBytes, 0, _buffer, 1, 4);
        ReadFully(stream, _buffer, 5, _size);
        Value = Encoding.BigEndianUnicode.GetString(_buffer, 5, _size - 2);  // Strip off trailing NUL.
    }

    /// <summary>
    /// Constructor for code.
    /// </summary>
    /// <param name="text">the value that this field will convey</param>
    public StringField(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var delimited = text + '\0';  // Add the trailing NUL the protocol expects.
        var bytes = Encoding.BigEndianUnicode.GetBytes(delimited);
        _size = bytes.Length;
        _buffer = new byte[_size + 5];
        _buffer[0] = StringTypeTag;
        Util.NumberToBytes(_size / 2, _buffer, 1, 4);  // The protocol counts characters, not bytes, for the size header.
        Array.Copy(bytes, 0, _buffer, 5, _size);
        Value = text;
    }

    /// <inheritdoc/>
    public override byte TypeTag => StringTypeTag;

    /// <inheritdoc/>
    public override byte ArgumentTag => 0x02;

    /// <inheritdoc/>
    public override ReadOnlyMemory<byte> GetBytes() => _buffer;

    /// <inheritdoc/>
    public override long Size => _size;

    /// <inheritdoc/>
    public override string ToString() => $"StringField[ size: {_size}, value: \"{Value}\", bytes: {GetHexString()}]";
}
