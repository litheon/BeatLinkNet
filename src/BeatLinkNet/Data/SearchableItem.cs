namespace BeatLink.Data;

/// <summary>
/// <para>Represents an item with an ID and label that can be used as a way to select tracks in a
/// dynamic playlist request, and on which playlists can be sorted. Many track metadata entries,
/// like artist, genre, and the color labels, are represented this way.</para>
///
/// <para>A simple immutable value class.</para>
/// </summary>
/// <remarks>
/// Ported from Deep Symmetry's beat-link <c>SearchableItem</c>, by James Elliott.
/// </remarks>
public class SearchableItem
{
    /// <summary>The database ID associated with this item, for searches.</summary>
    public int Id { get; }

    /// <summary>The text label used to show this item to the user.</summary>
    public string Label { get; }

    /// <summary>
    /// Constructor simply sets the immutable value properties.
    /// </summary>
    /// <param name="id">the database ID associated with this item, for searches</param>
    /// <param name="label">the text label used to show this item to the user</param>
    /// <exception cref="ArgumentNullException">if <paramref name="label"/> is <c>null</c></exception>
    public SearchableItem(int id, string label)
    {
        ArgumentNullException.ThrowIfNull(label);
        Id = id;
        Label = label;
    }

    /// <inheritdoc/>
    public override string ToString() => $"SearchableItem[id:{Id}, label:{Label}]";

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Id, Label);

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
        var other = (SearchableItem)obj;
        return Id == other.Id && Label == other.Label;
    }
}
