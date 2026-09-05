namespace BeatLink.Data;

/// <summary>
/// The frequency bands that are drawn (as layers) for three-band waveforms, along with the color
/// in which each band is drawn.
/// </summary>
/// <remarks>
/// Ported from Deep Symmetry's beat-link <c>WaveformFinder.ThreeBandLayer</c>, by James Elliott.
/// In this port the type is defined standalone (rather than nested inside the finder) so the
/// waveform data models can use it before <c>WaveformFinder</c> itself is ported in a later phase.
/// </remarks>
public sealed class ThreeBandLayer
{
    /// <summary>Low frequencies are drawn in dark blue.</summary>
    public static readonly ThreeBandLayer Low = new(ArgbColor.FromRgb(32, 83, 217), "LOW");

    /// <summary>
    /// The overlap of low and mid-range frequencies are drawn in brown. (The corresponding Java
    /// constant carries the misspelled name <c>LOW_AND_MIO</c>.)
    /// </summary>
    public static readonly ThreeBandLayer LowAndMid = new(ArgbColor.FromRgb(169, 107, 39), "LOW_AND_MID");

    /// <summary>Mid-range frequencies are drawn in amber.</summary>
    public static readonly ThreeBandLayer Mid = new(ArgbColor.FromRgb(242, 170, 60), "MID");

    /// <summary>High frequencies are drawn in white.</summary>
    public static readonly ThreeBandLayer High = new(ArgbColor.FromRgb(255, 255, 255), "HIGH");

    /// <summary>The color with which this band should be drawn.</summary>
    public ArgbColor Color { get; }

    /// <summary>The name of this band, corresponding to the Java enum constant name.</summary>
    public string Name { get; }

    private ThreeBandLayer(ArgbColor color, string name)
    {
        Color = color;
        Name = name;
    }

    /// <summary>All the three-band waveform layers.</summary>
    public static IReadOnlyList<ThreeBandLayer> Values { get; } = [Low, LowAndMid, Mid, High];

    /// <inheritdoc/>
    public override string ToString() => Name;
}
