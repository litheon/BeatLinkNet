namespace BeatLink.Data;

/// <summary>
/// <para>Represents a track color label. This is a specialized <see cref="SearchableItem"/>, since
/// in addition to a specific color, it has an ID and label that can be used as a way to select
/// tracks in a dynamic playlist request, and on which playlists can be sorted.</para>
///
/// <para>A simple immutable value class.</para>
/// </summary>
/// <remarks>
/// Ported from Deep Symmetry's beat-link <c>ColorItem</c>, by James Elliott. Where the Java
/// library exposes <c>java.awt.Color</c>, this port uses the platform-neutral
/// <see cref="ArgbColor"/> struct.
/// </remarks>
public class ColorItem : SearchableItem
{
    /// <summary>The color that is represented by this item.</summary>
    public ArgbColor Color { get; }

    /// <summary>The name of the color represented by this item, for textual display.</summary>
    public string ColorName { get; }

    /// <summary>
    /// Constructor simply sets the immutable value properties, looking up the color and name
    /// associated with the id.
    /// </summary>
    /// <param name="id">the database ID associated with this item, for searches</param>
    /// <param name="label">the text label used to show this item to the user</param>
    /// <exception cref="ArgumentNullException">if <paramref name="label"/> is <c>null</c></exception>
    public ColorItem(int id, string label) : base(id, label)
    {
        Color = ColorForId(id);
        ColorName = ColorNameForId(id);
    }

    /// <inheritdoc/>
    public override string ToString() =>
        $"ColorItem[id:{Id}, label:{Label}, colorName:{ColorName}, color:{Color}]";

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(base.GetHashCode(), Color, ColorName);

    /// <inheritdoc/>
    public override bool Equals(object? obj)
    {
        if (ReferenceEquals(obj, this))
        {
            return true;
        }
        if (obj == null || obj.GetType() != GetType())
        {
            return false;
        }
        var other = (ColorItem)obj;
        return other.Id == Id && other.Label == Label && other.ColorName == ColorName &&
            other.Color == Color;
    }

    /// <summary>
    /// Checks whether a color value corresponds to the "No Color" choice in rekordbox. This is
    /// implemented via transparency; the no-color choice is fully transparent.
    /// </summary>
    /// <param name="color">a color to be examined</param>
    /// <returns><c>true</c> if the color represents the lack of a color assignment</returns>
    public static bool IsNoColor(ArgbColor color) => color.A == 0;

    /// <summary>
    /// Returns the color represented by a color label assigned to a track in rekordbox. This is
    /// also used in the user interface color tint settings that can be set up for an exported
    /// media library (and returned in the <see cref="MediaDetails"/> response). If no color has
    /// been assigned, a fully-transparent black is returned, which can be tested for with
    /// <see cref="IsNoColor"/>.
    /// </summary>
    /// <param name="colorId">the id of the color label assigned to a track or the <i>col</i>
    /// value in the <see cref="MediaDetails"/> packet</param>
    /// <returns>the color that should be displayed (or which the UI should be tinted with)</returns>
    public static ArgbColor ColorForId(int colorId) => colorId switch
    {
        1 => ArgbColor.FromRgb(255, 175, 175),  // Pink
        2 => ArgbColor.FromRgb(255, 0, 0),      // Red
        3 => ArgbColor.FromRgb(255, 200, 0),    // Orange
        4 => ArgbColor.FromRgb(255, 255, 0),    // Yellow
        5 => ArgbColor.FromRgb(0, 255, 0),      // Green
        6 => ArgbColor.FromRgb(0, 255, 255),    // Aqua
        7 => ArgbColor.FromRgb(0, 0, 255),      // Blue
        8 => ArgbColor.FromRgb(128, 0, 128),    // Purple
        _ => ArgbColor.FromArgb(0, 0, 0, 0)     // No color (fully transparent)
    };

    /// <summary>
    /// Returns the name of the color represented by a color label assigned to a track in
    /// rekordbox. This is also used in the user interface color tint settings that can be set up
    /// for an exported media library (and returned in the <see cref="MediaDetails"/> response).
    /// </summary>
    /// <param name="colorId">the id of the color label assigned to a track or the <i>col</i>
    /// value in the <see cref="MediaDetails"/> packet</param>
    /// <returns>the name of the color that should be displayed</returns>
    public static string ColorNameForId(int colorId) => colorId switch
    {
        0 => "No Color",
        1 => "Pink",
        2 => "Red",
        3 => "Orange",
        4 => "Yellow",
        5 => "Green",
        6 => "Aqua",
        7 => "Blue",
        8 => "Purple",
        _ => "Unknown Color"
    };
}
