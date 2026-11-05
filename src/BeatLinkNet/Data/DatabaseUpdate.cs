using Database = BeatLink.CrateDigger.Database;

namespace BeatLink.Data;

/// <summary>
/// The payload of the <see cref="CrateDigger.DatabaseMounted"/> and
/// <see cref="CrateDigger.DatabaseUnmounted"/> events, identifying a parsed DeviceSQL
/// (<c>export.pdb</c>) database export and the media slot from which it was downloaded.
/// </summary>
/// <remarks>
/// Stands in for the arguments of Deep Symmetry's beat-link <c>DatabaseListener</c> callback
/// methods, by James Elliott.
/// </remarks>
public sealed class DatabaseUpdate
{
    /// <summary>The media slot whose database availability has changed.</summary>
    public SlotReference Slot { get; }

    /// <summary>The parsed database export whose relevance has changed.</summary>
    public Database Database { get; }

    /// <summary>
    /// Creates the event payload.
    /// </summary>
    /// <param name="slot">the media slot whose database availability has changed</param>
    /// <param name="database">the parsed database export whose relevance has changed</param>
    /// <exception cref="ArgumentNullException">if either argument is <c>null</c></exception>
    public DatabaseUpdate(SlotReference slot, Database database)
    {
        ArgumentNullException.ThrowIfNull(slot);
        ArgumentNullException.ThrowIfNull(database);
        Slot = slot;
        Database = database;
    }

    /// <inheritdoc/>
    public override string ToString() => $"DatabaseUpdate[slot:{Slot}, database:{Database}]";
}
