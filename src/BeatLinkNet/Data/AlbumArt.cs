namespace BeatLink.Data;

/// <summary>
/// Represents album artwork associated with tracks loaded into players on a DJ Link network.
/// Unlike the Java library, no image-decoding convenience is offered; consumers decode the raw
/// bytes (which hold an ordinary image file, generally JPEG or PNG) using whatever imaging
/// framework suits their platform.
/// </summary>
/// <remarks>
/// Ported from Deep Symmetry's beat-link <c>AlbumArt</c>, by James Elliott.
/// </remarks>
public class AlbumArt
{
    /// <summary>
    /// The unique artwork identifier that was used to request this album art. Even though it is
    /// not a track, the same pieces of information are used.
    /// </summary>
    public DataReference ArtReference { get; }

    /// <summary>The raw bytes of the artwork as loaded from the player.</summary>
    private readonly byte[] _rawBytes;

    /// <summary>
    /// Get the raw bytes of the artwork image as returned by the player.
    /// </summary>
    public ReadOnlyMemory<byte> RawBytes => _rawBytes;

    /// <summary>
    /// Constructor used with a file, for example one downloaded via NFS by Crate Digger.
    /// </summary>
    /// <param name="artReference">the unique database reference that was used to request this artwork</param>
    /// <param name="path">the path of the file of image data as loaded from the player</param>
    /// <exception cref="ArgumentNullException">if any argument is <c>null</c></exception>
    /// <exception cref="IOException">if there is a problem reading the file</exception>
    public AlbumArt(DataReference artReference, string path)
    {
        ArgumentNullException.ThrowIfNull(artReference);
        ArgumentNullException.ThrowIfNull(path);
        ArtReference = artReference;
        _rawBytes = File.ReadAllBytes(path);
    }

    /// <summary>
    /// Constructor usable by caching mechanisms simply sets the immutable value properties.
    /// </summary>
    /// <param name="artReference">the unique database reference that was used to request this artwork</param>
    /// <param name="rawBytes">the bytes of image data as loaded from the player or media export</param>
    /// <exception cref="ArgumentNullException">if <paramref name="artReference"/> is <c>null</c></exception>
    public AlbumArt(DataReference artReference, ReadOnlyMemory<byte> rawBytes)
    {
        ArgumentNullException.ThrowIfNull(artReference);
        ArtReference = artReference;
        _rawBytes = rawBytes.ToArray();
    }

    /// <inheritdoc/>
    public override string ToString() => $"AlbumArt[artReference={ArtReference}, size={_rawBytes.Length} bytes]";
}
