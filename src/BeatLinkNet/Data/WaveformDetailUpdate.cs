namespace BeatLink.Data;

/// <summary>
/// Provides notification when the waveform detail associated with a player changes.
/// </summary>
/// <remarks>
/// Ported from Deep Symmetry's beat-link <c>WaveformDetailUpdate</c>, by James Elliott.
/// </remarks>
public class WaveformDetailUpdate
{
    /// <summary>The player number for which a waveform detail change has occurred.</summary>
    public int Player { get; }

    /// <summary>
    /// The waveform detail which is now associated with the track loaded in the player's main
    /// deck. Will be <c>null</c> if we don't have any detail available (including for a brief
    /// period after a new track has been loaded while we are requesting the waveform detail).
    /// </summary>
    public WaveformDetail? Detail { get; }

    /// <summary>
    /// Constructor simply sets the immutable value properties.
    /// </summary>
    /// <param name="player">the player number for which a waveform detail change has occurred</param>
    /// <param name="detail">the waveform detail now associated with the track loaded in the
    /// player's main deck, if any</param>
    public WaveformDetailUpdate(int player, WaveformDetail? detail)
    {
        Player = player;
        Detail = detail;
    }

    /// <inheritdoc/>
    public override string ToString() => $"WaveformDetailUpdate[player:{Player}, detail:{Detail}]";
}
