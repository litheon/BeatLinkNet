namespace BeatLink.Data;

/// <summary>
/// Captures the playback state of a single player that has the track loaded, as an immutable
/// value class.
/// </summary>
/// <remarks>
/// Ported from Deep Symmetry's beat-link <c>PlaybackState</c>, by James Elliott.
/// </remarks>
public sealed class PlaybackState : IEquatable<PlaybackState>
{
    /// <summary>The player number whose playback state this represents.</summary>
    public int Player { get; }

    /// <summary>The current playback position of the player in milliseconds.</summary>
    public long Position { get; }

    /// <summary>Whether the player is actively playing the track.</summary>
    public bool Playing { get; }

    private readonly int _hashCode;

    /// <summary>
    /// Create an instance to represent a particular playback state.
    /// </summary>
    /// <param name="player">the player number whose playback state this represents</param>
    /// <param name="position">the current playback position in milliseconds</param>
    /// <param name="playing">whether the player is actively playing the track</param>
    public PlaybackState(int player, long position, bool playing)
    {
        Player = player;
        Position = position;
        Playing = playing;
        _hashCode = HashCode.Combine(player, position, playing);
    }

    /// <inheritdoc/>
    public bool Equals(PlaybackState? other) =>
        other != null && other.Player == Player && other.Position == Position && other.Playing == Playing;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as PlaybackState);

    /// <inheritdoc/>
    public override int GetHashCode() => _hashCode;

    /// <inheritdoc/>
    public override string ToString() =>
        $"PlaybackState[player={Player}, position={Position}, playing={Playing}]";
}
