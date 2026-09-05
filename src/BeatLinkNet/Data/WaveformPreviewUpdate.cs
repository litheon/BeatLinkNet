namespace BeatLink.Data;

/// <summary>
/// Provides notification when the waveform preview associated with a player changes.
/// </summary>
/// <remarks>
/// Ported from Deep Symmetry's beat-link <c>WaveformPreviewUpdate</c>, by James Elliott.
/// </remarks>
public class WaveformPreviewUpdate
{
    /// <summary>The player number for which a waveform preview change has occurred.</summary>
    public int Player { get; }

    /// <summary>
    /// The waveform preview which is now associated with the track loaded in the player's main
    /// deck. Will be <c>null</c> if we don't have any preview available (including for a brief
    /// period after a new track has been loaded while we are requesting the waveform preview).
    /// </summary>
    public WaveformPreview? Preview { get; }

    /// <summary>
    /// Constructor simply sets the immutable value properties.
    /// </summary>
    /// <param name="player">the player number for which a waveform preview change has occurred</param>
    /// <param name="preview">the waveform preview now associated with the track loaded in the
    /// player's main deck, if any</param>
    public WaveformPreviewUpdate(int player, WaveformPreview? preview)
    {
        Player = player;
        Preview = preview;
    }

    /// <inheritdoc/>
    public override string ToString() => $"WaveformPreviewUpdate[player:{Player}, preview:{Preview}]";
}
