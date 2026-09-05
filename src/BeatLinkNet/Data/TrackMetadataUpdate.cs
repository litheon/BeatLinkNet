namespace BeatLink.Data;

/// <summary>
/// Provides notification when the track metadata associated with a player changes.
/// </summary>
/// <remarks>
/// Ported from Deep Symmetry's beat-link <c>TrackMetadataUpdate</c>, by James Elliott.
/// </remarks>
public class TrackMetadataUpdate
{
    /// <summary>The player number for which a metadata change has occurred.</summary>
    public int Player { get; }

    /// <summary>
    /// The metadata which is now associated with the track loaded in the player's main deck. Will
    /// be <c>null</c> if we don't have any information available (including for a brief period
    /// after a new track has been loaded while we are requesting the metadata).
    /// </summary>
    public TrackMetadata? Metadata { get; }

    /// <summary>
    /// Constructor simply sets the immutable value properties.
    /// </summary>
    /// <param name="player">the player number for which a metadata change has occurred</param>
    /// <param name="metadata">the metadata now associated with the track loaded in the player's
    /// main deck, if any</param>
    public TrackMetadataUpdate(int player, TrackMetadata? metadata)
    {
        Player = player;
        Metadata = metadata;
    }

    /// <inheritdoc/>
    public override string ToString() => $"TrackMetadataUpdate[player:{Player}, metadata:{Metadata}]";
}
