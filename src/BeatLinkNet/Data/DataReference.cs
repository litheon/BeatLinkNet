namespace BeatLink.Data;

/// <summary>
/// Uniquely identifies a track, album art, beat grid, waveform, song structure, or other metadata
/// object currently available on the network, by the player, media slot in which it is mounted,
/// track type, and its rekordbox ID. A simple immutable value class.
/// </summary>
/// <remarks>
/// Ported from Deep Symmetry's beat-link <c>DataReference</c>, by James Elliott.
/// </remarks>
public sealed class DataReference : IEquatable<DataReference>
{
    /// <summary>The player in which this track, album art, beat grid, or waveform is mounted.</summary>
    public int Player { get; }

    /// <summary>The slot in which the track, album art, beat grid, or waveform is mounted.</summary>
    public CdjStatus.TrackSourceSlot Slot { get; }

    /// <summary>The unique ID of the track, album art, beat grid, or waveform within that media database.</summary>
    public int RekordboxId { get; }

    /// <summary>
    /// The type of the track. Prior to the CDJ-3000 this was always
    /// <see cref="CdjStatus.TrackType.Rekordbox"/> and was assumed, but since the players can now
    /// perform analysis themselves, this can now also have the value
    /// <see cref="CdjStatus.TrackType.Unanalyzed"/> so it needs to be part of the reference.
    /// </summary>
    public CdjStatus.TrackType TrackType { get; }

    /// <summary>Caches the hash code for performance.</summary>
    private readonly int _hashCode;

    /// <summary>
    /// Create a unique reference to a track, album art, beat grid, waveform, song structure, or
    /// other metadata object that is currently available on the network, assuming it is a rekordbox
    /// track. (This corresponds to a constructor that is deprecated in the Java library, but is
    /// retained as a convenience overload.)
    /// </summary>
    /// <param name="player">the player in which the item is mounted</param>
    /// <param name="slot">the slot in which the item is mounted</param>
    /// <param name="rekordboxId">the unique ID of the item within that media database</param>
    /// <exception cref="ArgumentNullException">if <paramref name="slot"/> is <c>null</c></exception>
    public DataReference(int player, CdjStatus.TrackSourceSlot slot, int rekordboxId) :
        this(player, slot, rekordboxId, CdjStatus.TrackType.Rekordbox)
    {
    }

    /// <summary>
    /// Create a unique reference to a track, album art, beat grid, waveform, song structure, or
    /// other metadata object that is currently available on the network.
    /// </summary>
    /// <param name="player">the player in which the item is mounted</param>
    /// <param name="slot">the slot in which the item is mounted</param>
    /// <param name="rekordboxId">the unique ID of the item within that media database</param>
    /// <param name="trackType">identifies the type of track (can now be either
    /// <see cref="CdjStatus.TrackType.Rekordbox"/> or <see cref="CdjStatus.TrackType.Unanalyzed"/>
    /// with CDJ-3000s)</param>
    /// <exception cref="ArgumentNullException">if <paramref name="slot"/> or
    /// <paramref name="trackType"/> is <c>null</c></exception>
    public DataReference(int player, CdjStatus.TrackSourceSlot slot, int rekordboxId, CdjStatus.TrackType trackType)
    {
        ArgumentNullException.ThrowIfNull(slot);
        ArgumentNullException.ThrowIfNull(trackType);
        Player = player;
        Slot = slot;
        RekordboxId = rekordboxId;
        TrackType = trackType;
        _hashCode = HashCode.Combine(player, slot, rekordboxId, trackType);
    }

    /// <summary>
    /// Create a unique reference to a track, album art, beat grid, or waveform that is currently
    /// available on the network, assuming it is a rekordbox track. (This corresponds to a
    /// constructor that is deprecated in the Java library, but is retained as a convenience
    /// overload.)
    /// </summary>
    /// <param name="slot">the slot in which the item is mounted</param>
    /// <param name="rekordboxId">the unique ID of the item within that media database</param>
    /// <exception cref="ArgumentNullException">if <paramref name="slot"/> is <c>null</c></exception>
    public DataReference(SlotReference slot, int rekordboxId) :
        this(NonNull(slot).Player, slot.Slot, rekordboxId, CdjStatus.TrackType.Rekordbox)
    {
    }

    /// <summary>
    /// Create a unique reference to a track, album art, beat grid, or waveform that is currently
    /// available on the network.
    /// </summary>
    /// <param name="slot">the slot in which the item is mounted</param>
    /// <param name="rekordboxId">the unique ID of the item within that media database</param>
    /// <param name="trackType">identifies the type of track (can now be either
    /// <see cref="CdjStatus.TrackType.Rekordbox"/> or <see cref="CdjStatus.TrackType.Unanalyzed"/>
    /// with CDJ-3000s)</param>
    /// <exception cref="ArgumentNullException">if <paramref name="slot"/> or
    /// <paramref name="trackType"/> is <c>null</c></exception>
    public DataReference(SlotReference slot, int rekordboxId, CdjStatus.TrackType trackType) :
        this(NonNull(slot).Player, slot.Slot, rekordboxId, trackType)
    {
    }

    /// <summary>
    /// Helper allowing constructor delegation to validate the slot reference before dereferencing it.
    /// </summary>
    private static SlotReference NonNull(SlotReference slot)
    {
        ArgumentNullException.ThrowIfNull(slot);
        return slot;
    }

    /// <summary>
    /// Extract the slot reference portion of this data reference (discarding the rekordbox ID).
    /// </summary>
    /// <returns>the player and slot from which this data can be loaded</returns>
    public SlotReference GetSlotReference() => SlotReference.GetSlotReference(this);

    /// <inheritdoc/>
    public bool Equals(DataReference? other) =>
        other != null && other.Player == Player && ReferenceEquals(other.Slot, Slot) &&
        other.RekordboxId == RekordboxId && ReferenceEquals(other.TrackType, TrackType);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as DataReference);

    /// <inheritdoc/>
    public override int GetHashCode() => _hashCode;

    /// <inheritdoc/>
    public override string ToString() =>
        $"DataReference[player:{Player}, slot:{Slot}, rekordboxId:{RekordboxId}, trackType:{TrackType}]";
}
