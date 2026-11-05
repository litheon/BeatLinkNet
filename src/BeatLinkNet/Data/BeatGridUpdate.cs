namespace BeatLink.Data;

/// <summary>
/// Provides notification when the beat grid associated with a player changes.
/// </summary>
/// <remarks>
/// Ported from Deep Symmetry's beat-link <c>BeatGridUpdate</c>, by James Elliott.
/// </remarks>
public class BeatGridUpdate
{
    /// <summary>The player number for which a beat grid change has occurred.</summary>
    public int Player { get; }

    /// <summary>
    /// The beat grid which is now associated with the track loaded in the player's main deck.
    /// Will be <c>null</c> if we don't have any beat grid available (including for a brief period
    /// after a new track has been loaded while we are requesting the beat grid).
    /// </summary>
    public BeatGrid? BeatGrid { get; }

    /// <summary>
    /// Constructor simply sets the immutable value properties.
    /// </summary>
    /// <param name="player">the player number for which a beat grid change has occurred</param>
    /// <param name="beatGrid">the beat grid now associated with the track loaded in the player's
    /// main deck, if any</param>
    public BeatGridUpdate(int player, BeatGrid? beatGrid)
    {
        Player = player;
        BeatGrid = beatGrid;
    }

    /// <inheritdoc/>
    public override string ToString() => $"BeatGridUpdate[player:{Player}, beatGrid:{BeatGrid}]";
}
