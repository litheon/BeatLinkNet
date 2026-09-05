using System.Collections.Concurrent;

namespace BeatLink.Data;

/// <summary>
/// Uniquely identifies a place where a track can be currently loaded on the network, either the
/// visible deck of one of the players, or one of the hot cues on a player. Used to keep track of
/// which tracks may suddenly start playing. All instances are interned, such that any instances
/// with the same value will actually be the same object, for fast comparison (value equality is
/// also implemented, so interning is a performance nicety rather than a correctness requirement).
/// </summary>
/// <remarks>
/// Ported from Deep Symmetry's beat-link <c>DeckReference</c>, by James Elliott.
/// </remarks>
public sealed class DeckReference : IEquatable<DeckReference>
{
    /// <summary>The player in which this track is loaded.</summary>
    public int Player { get; }

    /// <summary>
    /// The hot cue number in which the track is loaded, or 0 if it is actively loaded on the
    /// playback deck.
    /// </summary>
    public int HotCue { get; }

    private readonly int _hashCode;

    private DeckReference(int player, int hotCue)
    {
        Player = player;
        HotCue = hotCue;
        _hashCode = HashCode.Combine(player, hotCue);
    }

    /// <summary>
    /// Holds all the instances of this class as they get created by the static factory method.
    /// </summary>
    private static readonly ConcurrentDictionary<(int, int), DeckReference> Instances = new();

    /// <summary>
    /// Get a unique reference to a place where a track is currently loaded in a player.
    /// </summary>
    /// <param name="player">the player in which the track is loaded</param>
    /// <param name="hotCue">hot cue number in which the track is loaded, or 0 if it is actively
    /// loaded on the playback deck</param>
    /// <returns>the instance that will always represent a reference to the specified player and hot cue</returns>
    public static DeckReference GetDeckReference(int player, int hotCue) =>
        Instances.GetOrAdd((player, hotCue), static key => new DeckReference(key.Item1, key.Item2));

    /// <inheritdoc/>
    public bool Equals(DeckReference? other) =>
        other != null && other.Player == Player && other.HotCue == HotCue;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as DeckReference);

    /// <inheritdoc/>
    public override int GetHashCode() => _hashCode;

    /// <inheritdoc/>
    public override string ToString() => $"DeckReference[player:{Player}, hotCue:{HotCue}]";
}
