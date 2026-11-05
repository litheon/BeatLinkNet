using System.Text;

namespace BeatLink.DbServer;

/// <summary>
/// All dbserver messages are made up of lists of fields, which are type-tagged values.
/// </summary>
/// <remarks>
/// Ported from Deep Symmetry's beat-link <c>Field</c>, by James Elliott.
/// </remarks>
public abstract class Field
{
    /// <summary>
    /// Get the bytes which represent this field when sent over the network, including the leading type tag.
    /// </summary>
    /// <returns>the full set of bytes which should be transmitted for this field</returns>
    public abstract ReadOnlyMemory<byte> GetBytes();

    /// <summary>
    /// Get the size, in bytes, of the network representation of this field, excluding the leading type tag and
    /// length bytes (if any).
    /// </summary>
    public abstract long Size { get; }

    /// <summary>
    /// Get the value which identifies the start of this field in the network stream.
    /// </summary>
    public abstract byte TypeTag { get; }

    /// <summary>
    /// Get the value which identifies this type of field in a message argument list. This is used instead of
    /// the type tag for some reason when putting together the argument type list for a dbserver message.
    /// </summary>
    public abstract byte ArgumentTag { get; }

    /// <summary>
    /// Read a field from the supplied stream, starting with the tag that identifies the type, and reading enough
    /// to collect the corresponding value.
    /// </summary>
    /// <param name="stream">the stream on which a type tag is expected to be the next byte, followed by the field value</param>
    /// <returns>the field that was found on the stream</returns>
    /// <exception cref="IOException">if there is a problem reading the field, including an unrecognized type tag</exception>
    public static Field Read(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var first = stream.ReadByte();
        if (first < 0)
        {
            throw new EndOfStreamException("Reached end of stream trying to read a field type tag");
        }
        var tag = (byte)first;
        return tag switch
        {
            0x0f or 0x10 or 0x11 => new NumberField(tag, stream),
            0x14 => new BinaryField(stream),
            0x26 => new StringField(stream),
            _ => throw new IOException($"Unable to read a field with type tag {tag}")
        };
    }

    /// <summary>
    /// Reads exactly <paramref name="count"/> bytes into <paramref name="buffer"/> starting at
    /// <paramref name="offset"/>, throwing if the stream ends first. (Replaces Java's
    /// <c>DataInputStream.readFully</c>.)
    /// </summary>
    private protected static void ReadFully(Stream stream, byte[] buffer, int offset, int count) =>
        stream.ReadExactly(buffer, offset, count);

    /// <summary>
    /// Formats the bytes that make up this field as a hex string, for use by subclasses in their
    /// <see cref="object.ToString"/> methods.
    /// </summary>
    /// <returns>the hex representations of the bytes that this field will send over the network</returns>
    internal string GetHexString()
    {
        var bytes = GetBytes().Span;
        var sb = new StringBuilder(bytes.Length * 3);
        foreach (var b in bytes)
        {
            sb.Append(b.ToString("x2")).Append(' ');
        }
        return sb.ToString();
    }

    /// <summary>
    /// Write the field to the specified stream.
    /// </summary>
    /// <param name="stream">the stream to which it should be written</param>
    /// <exception cref="IOException">if there is a problem writing to the stream</exception>
    public void Write(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        stream.Write(GetBytes().Span);
    }
}
