namespace BeatLink.CrateDigger.Nfs;

/// <summary>
/// An opaque NFS version 2 file handle (RFC 1094 <c>fhandle</c>): thirty-two bytes which identify
/// a file or directory on the server, obtained from a mount or lookup operation and passed back to
/// the server in subsequent requests. Corresponds to the generated <c>FHandle</c> class in the
/// Java crate-digger.
/// </summary>
public sealed class FileHandle
{
    /// <summary>
    /// The size, in bytes, of every NFS version 2 file handle (RFC 1094 <c>FHSIZE</c>).
    /// </summary>
    public const int Size = 32;

    private readonly byte[] _bytes;

    /// <summary>
    /// Create a file handle wrapping the supplied bytes.
    /// </summary>
    /// <param name="bytes">the thirty-two bytes which the server uses to identify the file</param>
    /// <exception cref="ArgumentException">if the wrong number of bytes is supplied</exception>
    public FileHandle(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != Size)
        {
            throw new ArgumentException($"An NFS file handle must be exactly {Size} bytes long, received {bytes.Length}.", nameof(bytes));
        }
        _bytes = bytes.ToArray();
    }

    /// <summary>
    /// The bytes which the server uses to identify the file.
    /// </summary>
    public ReadOnlySpan<byte> Bytes => _bytes;

    /// <summary>
    /// Write this file handle to an XDR stream as fixed-length opaque data.
    /// </summary>
    /// <param name="writer">the stream being assembled</param>
    internal void WriteTo(XdrWriter writer) => writer.WriteFixedOpaque(_bytes);

    /// <summary>
    /// Read a file handle from an XDR stream.
    /// </summary>
    /// <param name="reader">the stream being decoded</param>
    internal static FileHandle ReadFrom(XdrReader reader) => new(reader.ReadFixedOpaque(Size));

    /// <inheritdoc/>
    public override string ToString() => $"FileHandle[{Convert.ToHexStringLower(_bytes)}]";
}
