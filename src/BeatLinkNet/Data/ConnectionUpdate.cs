using Microsoft.Data.Sqlite;

namespace BeatLink.Data;

/// <summary>
/// The payload of the <see cref="CrateDigger.ConnectionMounted"/> and
/// <see cref="CrateDigger.ConnectionUnmounted"/> events, identifying an open connection to a
/// SQLite Device Library Plus (<c>exportLibrary.db</c>) database export and the media slot from
/// which it was downloaded.
/// </summary>
/// <remarks>
/// Stands in for the arguments of Deep Symmetry's beat-link <c>SQLiteConnectionListener</c>
/// callback methods, by James Elliott. Experimental, like its Java counterpart.
/// </remarks>
public sealed class ConnectionUpdate
{
    /// <summary>The media slot whose database connection availability has changed.</summary>
    public SlotReference Slot { get; }

    /// <summary>
    /// The SQLite connection whose relevance has changed. When received through
    /// <see cref="CrateDigger.ConnectionUnmounted"/> the connection is about to be closed, so it
    /// should no longer be used once the event handler returns.
    /// </summary>
    public SqliteConnection Connection { get; }

    /// <summary>
    /// Creates the event payload.
    /// </summary>
    /// <param name="slot">the media slot whose database connection availability has changed</param>
    /// <param name="connection">the SQLite connection whose relevance has changed</param>
    /// <exception cref="ArgumentNullException">if either argument is <c>null</c></exception>
    public ConnectionUpdate(SlotReference slot, SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(slot);
        ArgumentNullException.ThrowIfNull(connection);
        Slot = slot;
        Connection = connection;
    }

    /// <inheritdoc/>
    public override string ToString() => $"ConnectionUpdate[slot:{Slot}, connection:{Connection}]";
}
