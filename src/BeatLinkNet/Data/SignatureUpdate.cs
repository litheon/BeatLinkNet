namespace BeatLink.Data;

/// <summary>
/// Provides notification when the track signature associated with a player changes.
/// </summary>
/// <remarks>
/// Ported from Deep Symmetry's beat-link <c>SignatureUpdate</c>, by James Elliott.
/// </remarks>
public class SignatureUpdate
{
    /// <summary>The player number for which a signature change has occurred.</summary>
    public int Player { get; }

    /// <summary>
    /// The track signature now associated with the track loaded in the player. Will be
    /// <c>null</c> if we don't yet have enough metadata about the track to compute its signature
    /// (including for a brief period after a new track has been loaded while the various metadata
    /// finders are retrieving the information they need).
    /// </summary>
    public string? Signature { get; }

    /// <summary>
    /// Constructor simply sets the immutable value properties.
    /// </summary>
    /// <param name="player">the player number for which a signature change has occurred</param>
    /// <param name="signature">the track signature now associated with the track loaded in the
    /// player, if known</param>
    public SignatureUpdate(int player, string? signature)
    {
        Player = player;
        Signature = signature;
    }

    /// <inheritdoc/>
    public override string ToString() => $"TrackSignatureUpdate[player:{Player}, signature:{Signature}]";
}
