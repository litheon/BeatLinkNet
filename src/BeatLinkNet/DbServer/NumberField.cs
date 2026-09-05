namespace BeatLink.DbServer;

/// <summary>
/// A number field represents an integer, and can take up 1, 2, or 4 bytes, depending on the tag which
/// introduces it.
/// </summary>
/// <remarks>
/// Ported from Deep Symmetry's beat-link <c>NumberField</c>, by James Elliott.
/// </remarks>
public sealed class NumberField : Field
{
    private readonly byte _typeTag;
    private readonly int _size;

    /// <summary>
    /// Holds the actual bytes used to transmit this field, including the type tag.
    /// </summary>
    private readonly byte[] _buffer;

    /// <summary>
    /// A four-byte field representing the number zero, used in many requests, so they can save the time and
    /// garbage of constructing throwaway versions.
    /// </summary>
    public static readonly NumberField Word0 = new(0);

    /// <summary>
    /// A four-byte field representing the number one, used in some requests, so they can save the time and
    /// garbage of constructing throwaway versions.
    /// </summary>
    public static readonly NumberField Word1 = new(1);

    /// <summary>
    /// The numeric value this field represents. Held in a <see cref="long"/> so there are no sign issues when
    /// the full four-byte version is used.
    /// </summary>
    public long Value { get; }

    /// <summary>
    /// Constructor for reading from the network.
    /// </summary>
    /// <param name="typeTag">the tag which identified this field as a NumberField, and which allows us to
    /// determine the proper size</param>
    /// <param name="stream">the stream on which the field value is to be read</param>
    /// <exception cref="ArgumentException">if <paramref name="typeTag"/> is not a valid number field tag</exception>
    /// <exception cref="IOException">if there is a problem reading the value</exception>
    public NumberField(byte typeTag, Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        _typeTag = typeTag;
        _size = typeTag switch
        {
            0x0f => 1,
            0x10 => 2,
            0x11 => 4,
            _ => throw new ArgumentException($"NumberField cannot have tag {typeTag}", nameof(typeTag))
        };
        _buffer = new byte[_size + 1];
        _buffer[0] = typeTag;
        ReadFully(stream, _buffer, 1, _size);
        Value = Util.BytesToNumber(_buffer, 1, _size);
    }

    /// <summary>
    /// Constructor from code.
    /// </summary>
    /// <param name="value">the desired value to be represented by this field</param>
    /// <param name="size">the number of bytes to be used to hold the value: 1, 2, or 4</param>
    /// <exception cref="ArgumentException">if the specified size is not a supported number field size</exception>
    public NumberField(long value, int size)
    {
        Value = value & 0xffffffffL;
        _size = size;
        _buffer = new byte[size + 1];
        switch (size)
        {
            case 1:
                _typeTag = 0x0f;
                _buffer[1] = (byte)(value & 0xff);
                break;
            case 2:
                _typeTag = 0x10;
                _buffer[1] = (byte)((value & 0xff00) >> 8);
                _buffer[2] = (byte)(value & 0xff);
                break;
            case 4:
                _typeTag = 0x11;
                _buffer[1] = (byte)((value & 0xff000000L) >> 24);
                _buffer[2] = (byte)((value & 0xff0000) >> 16);
                _buffer[3] = (byte)((value & 0xff00) >> 8);
                _buffer[4] = (byte)(value & 0xff);
                break;
            default:
                throw new ArgumentException($"NumberField cannot have size {size}", nameof(size));
        }
        _buffer[0] = _typeTag;
    }

    /// <summary>
    /// Convenience constructor from code, for the most common case of wanting a 4-byte number field.
    /// </summary>
    /// <param name="value">the desired value to be represented by this field</param>
    public NumberField(long value) : this(value, 4)
    {
    }

    /// <inheritdoc/>
    public override byte TypeTag => _typeTag;

    /// <inheritdoc/>
    public override byte ArgumentTag => 0x06;

    /// <inheritdoc/>
    public override long Size => _size;

    /// <inheritdoc/>
    public override ReadOnlyMemory<byte> GetBytes() => _buffer;

    /// <inheritdoc/>
    public override string ToString() => $"NumberField[ size: {_size}, value: {Value}, bytes: {GetHexString()}]";
}
