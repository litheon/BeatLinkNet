namespace BeatLink.Data;

/// <summary>
/// Provides notification when the album art associated with a player changes.
/// </summary>
/// <remarks>
/// Ported from Deep Symmetry's beat-link <c>AlbumArtUpdate</c>, by James Elliott.
/// </remarks>
public class AlbumArtUpdate
{
    /// <summary>The player number for which an album art change has occurred.</summary>
    public int Player { get; }

    /// <summary>
    /// The album art which is now associated with the track loaded in the player's main deck.
    /// Will be <c>null</c> if we don't have any art available (including for a brief period after
    /// a new track has been loaded while we are requesting the art).
    /// </summary>
    public AlbumArt? Art { get; }

    /// <summary>
    /// Constructor simply sets the immutable value properties.
    /// </summary>
    /// <param name="player">the player number for which an album art change has occurred</param>
    /// <param name="art">the album art now associated with the track loaded in the player's main
    /// deck, if any</param>
    public AlbumArtUpdate(int player, AlbumArt? art)
    {
        Player = player;
        Art = art;
    }

    /// <inheritdoc/>
    public override string ToString() => $"AlbumArtUpdate[player:{Player}, art:{Art}]";
}
