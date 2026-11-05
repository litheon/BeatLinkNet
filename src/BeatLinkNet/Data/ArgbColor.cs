namespace BeatLink.Data;

/// <summary>
/// A platform-neutral ARGB color value, used wherever the original beat-link library exposed
/// <c>java.awt.Color</c> (waveform segment colors, cue colors, phrase colors, track color labels),
/// so that any .NET UI framework can consume the values.
/// </summary>
/// <param name="A">the alpha (opacity) component, 0-255</param>
/// <param name="R">the red component, 0-255</param>
/// <param name="G">the green component, 0-255</param>
/// <param name="B">the blue component, 0-255</param>
public readonly record struct ArgbColor(byte A, byte R, byte G, byte B)
{
    /// <summary>Creates a fully opaque color from red, green, and blue components.</summary>
    /// <param name="r">the red component, 0-255</param>
    /// <param name="g">the green component, 0-255</param>
    /// <param name="b">the blue component, 0-255</param>
    /// <returns>the opaque color</returns>
    public static ArgbColor FromRgb(int r, int g, int b) => new(255, (byte)r, (byte)g, (byte)b);

    /// <summary>Creates a color from alpha, red, green, and blue components.</summary>
    /// <param name="a">the alpha (opacity) component, 0-255</param>
    /// <param name="r">the red component, 0-255</param>
    /// <param name="g">the green component, 0-255</param>
    /// <param name="b">the blue component, 0-255</param>
    /// <returns>the color</returns>
    public static ArgbColor FromArgb(int a, int r, int g, int b) => new((byte)a, (byte)r, (byte)g, (byte)b);

    /// <summary>Fully opaque white.</summary>
    public static readonly ArgbColor White = FromRgb(255, 255, 255);

    /// <summary>Fully opaque black.</summary>
    public static readonly ArgbColor Black = FromRgb(0, 0, 0);

    /// <summary>
    /// Combines the hue components of one color with the transparency of another, as used when
    /// painting waveform overlays.
    /// </summary>
    /// <param name="hueColor">the color that specifies the actual hue to be drawn</param>
    /// <param name="alphaColor">the color whose transparency will determine the transparency of the result</param>
    /// <returns>a color with hue components from <paramref name="hueColor"/> and alpha from <paramref name="alphaColor"/></returns>
    public static ArgbColor BuildColor(ArgbColor hueColor, ArgbColor alphaColor) =>
        new(alphaColor.A, hueColor.R, hueColor.G, hueColor.B);
}
