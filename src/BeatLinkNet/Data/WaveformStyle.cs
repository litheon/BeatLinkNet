namespace BeatLink.Data;

/// <summary>
/// The different formats in which players supply track waveform information.
/// </summary>
/// <remarks>
/// Ported from Deep Symmetry's beat-link <c>WaveformFinder.WaveformStyle</c>, by James Elliott.
/// In this port the enum is defined standalone (rather than nested inside the finder) so the
/// waveform data models can use it before <c>WaveformFinder</c> itself is ported in a later phase.
/// </remarks>
public enum WaveformStyle
{
    /// <summary>The blue-and-white format used prior to the nxs2 hardware.</summary>
    Blue,

    /// <summary>The full-color waveforms introduced with the nxs2 players.</summary>
    Rgb,

    /// <summary>The three-band waveforms introduced with the CDJ-3000 players.</summary>
    ThreeBand
}
