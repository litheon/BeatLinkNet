using System.Collections.Concurrent;

namespace BeatLink.Data;

/// <summary>
/// Uniquely identifies a media slot on the network from which tracks can be loaded, by the player
/// and slot type. A simple immutable value class, with the property that all instances are
/// interned, such that any instances with the same value will actually be the same object, for
/// fast comparison (value equality is also implemented, so interning is a performance nicety
/// rather than a correctness requirement).
/// </summary>
public sealed class SlotReference : IEquatable<SlotReference>
{
    /// <summary>The player in which this slot is found.</summary>
    public int Player { get; }

    /// <summary>The specific type of the slot.</summary>
    public CdjStatus.TrackSourceSlot Slot { get; }

    private readonly int _hashCode;

    private SlotReference(int player, CdjStatus.TrackSourceSlot slot)
    {
        Player = player;
        Slot = slot;
        _hashCode = HashCode.Combine(player, slot);
    }

    /// <summary>
    /// Holds all the instances of this class as they get created by the static factory method.
    /// </summary>
    private static readonly ConcurrentDictionary<(int, CdjStatus.TrackSourceSlot), SlotReference> Instances = new();

    /// <summary>
    /// Get a unique reference to a media slot on the network from which tracks can be loaded.
    /// </summary>
    /// <param name="player">the player in which the slot is found</param>
    /// <param name="slot">the specific type of the slot</param>
    /// <returns>the instance that will always represent the specified slot</returns>
    /// <exception cref="ArgumentNullException">if <paramref name="slot"/> is <c>null</c></exception>
    public static SlotReference GetSlotReference(int player, CdjStatus.TrackSourceSlot slot)
    {
        ArgumentNullException.ThrowIfNull(slot);
        return Instances.GetOrAdd((player, slot), static key => new SlotReference(key.Item1, key.Item2));
    }

    /// <summary>
    /// Get a unique reference to the media slot on the network from which the specified data was loaded.
    /// </summary>
    /// <param name="dataReference">the data whose media slot is of interest</param>
    /// <returns>the instance that will always represent the slot associated with the specified data</returns>
    /// <exception cref="ArgumentNullException">if <paramref name="dataReference"/> is <c>null</c></exception>
    public static SlotReference GetSlotReference(DataReference dataReference)
    {
        ArgumentNullException.ThrowIfNull(dataReference);
        return GetSlotReference(dataReference.Player, dataReference.Slot);
    }

    /// <inheritdoc/>
    public bool Equals(SlotReference? other) =>
        other != null && other.Player == Player && ReferenceEquals(other.Slot, Slot);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as SlotReference);

    /// <inheritdoc/>
    public override int GetHashCode() => _hashCode;

    /// <inheritdoc/>
    public override string ToString() => $"SlotReference[player:{Player}, slot:{Slot}]";
}
