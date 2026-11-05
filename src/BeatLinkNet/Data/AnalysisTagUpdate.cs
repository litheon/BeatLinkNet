using BeatLink.CrateDigger.Pdb;

namespace BeatLink.Data;

/// <summary>
/// Provides notification when requested track analysis information associated with a player changes.
/// </summary>
/// <remarks>
/// Ported from Deep Symmetry's beat-link <c>AnalysisTagUpdate</c>, by James Elliott.
/// </remarks>
public class AnalysisTagUpdate
{
    /// <summary>The player number for which a track analysis information change has occurred.</summary>
    public int Player { get; }

    /// <summary>
    /// The file extension identifying which specific track analysis file this section came from.
    /// </summary>
    public string FileExtension { get; }

    /// <summary>
    /// The four-character tag type code identifying the specific analysis section which has changed.
    /// </summary>
    public string TypeTag { get; }

    /// <summary>
    /// The parsed track analysis section which is now associated with the track loaded in the
    /// player's main deck. Will be <c>null</c> if we don't have any information available
    /// (including for a brief period after a new track has been loaded while we are requesting
    /// the analysis file). Not all tracks have all tags; for example, only tracks which have had
    /// phrase analysis performed and which have been exported by rekordbox 6 or later will
    /// include a "PSSI" section in their ".EXT" file.
    /// </summary>
    public RekordboxAnlz.TaggedSection? TaggedSection { get; }

    /// <summary>
    /// Constructor simply sets the immutable value properties.
    /// </summary>
    /// <param name="player">the player number for which a track analysis information change has occurred</param>
    /// <param name="fileExtension">the file extension identifying which specific track analysis
    /// file this section came from</param>
    /// <param name="typeTag">the four-character tag type code identifying the specific analysis
    /// section which has changed</param>
    /// <param name="taggedSection">the parsed track analysis section now associated with the
    /// track loaded in the player's main deck, if any</param>
    /// <exception cref="ArgumentNullException">if <paramref name="fileExtension"/> or
    /// <paramref name="typeTag"/> is <c>null</c></exception>
    public AnalysisTagUpdate(int player, string fileExtension, string typeTag,
        RekordboxAnlz.TaggedSection? taggedSection)
    {
        ArgumentNullException.ThrowIfNull(fileExtension);
        ArgumentNullException.ThrowIfNull(typeTag);
        Player = player;
        FileExtension = fileExtension;
        TypeTag = typeTag;
        TaggedSection = taggedSection;
    }

    /// <inheritdoc/>
    public override string ToString() =>
        $"AnalysisTagUpdate[player:{Player}, file extension:{FileExtension}, tag type:{TypeTag}, " +
        $"tagged section:{TaggedSection}]";
}
