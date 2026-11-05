namespace BeatLink.Data;

/// <summary>
/// Keeps track of the most recent information we have received from a player from which we have
/// been able to compute a track position.
/// </summary>
/// <remarks>
/// Ported from Deep Symmetry's beat-link <c>TrackPositionUpdate</c>, by James Elliott.
/// </remarks>
public class TrackPositionUpdate
{
    /// <summary>When this update was received, as a monotonic timestamp in nanoseconds
    /// (see <see cref="Util.CurrentTimestampNanos"/>).</summary>
    public long Timestamp { get; }

    /// <summary>How far into the track has the player reached, in milliseconds.</summary>
    public long Milliseconds { get; }

    /// <summary>The beat number that was reported (or incremented) by this update.</summary>
    public int BeatNumber { get; }

    /// <summary>
    /// If <c>true</c>, this was created in response to a beat packet or precise position packet,
    /// so we know exactly where the player was at that point. Otherwise, we infer position based
    /// on how long has elapsed since the previous beat packet, and the intervening playback pitch
    /// and direction.
    /// </summary>
    public bool Definitive { get; }

    /// <summary>
    /// If <c>true</c>, we are receiving precise position packets from this player, so we know
    /// exactly where the player is even when it is not playing, or jumping or looping within beats.
    /// </summary>
    public bool Precise { get; }

    /// <summary>If <c>true</c>, this position update resulted from the receipt of a beat packet.</summary>
    public bool FromBeat { get; }

    /// <summary>If <c>true</c>, the player reported that it was playing when the update was received.</summary>
    public bool Playing { get; }

    /// <summary>The playback pitch when this update was created (where 1.0 is normal speed).</summary>
    public double Pitch { get; }

    /// <summary>If <c>true</c>, the player was playing backwards when this update was created.</summary>
    public bool Reverse { get; }

    /// <summary>
    /// The track beat grid against which this update was calculated, so that if it has changed,
    /// we know to discard the update.
    /// </summary>
    public BeatGrid? BeatGrid { get; }

    /// <summary>
    /// Returns the beat phase (where within a measure the current beat falls), a number from 1 to
    /// 4, corresponding to the beat being played at this playback position, or 0 if no beat grid
    /// was available.
    /// </summary>
    public int BeatWithinBar => BeatGrid?.GetBeatWithinBar(BeatNumber) ?? 0;

    /// <summary>
    /// Constructor simply sets the properties of this immutable value class.
    /// Backwards-compatible version which infers <c>false</c> values for <c>precise</c> and
    /// <c>fromBeat</c>.
    /// </summary>
    /// <param name="timestamp">when this update was received</param>
    /// <param name="milliseconds">how far into the track has the player reached</param>
    /// <param name="beatNumber">the beat number that was reported (or incremented) by this update</param>
    /// <param name="definitive">indicates if this was based on a direct report of track position from the player (i.e. a beat)</param>
    /// <param name="playing">indicates whether the player was actively playing a track when this update was received</param>
    /// <param name="pitch">the playback pitch (where 1.0 is normal speed) when this update was received</param>
    /// <param name="reverse">indicates if the player was playing backwards when this update was received</param>
    /// <param name="beatGrid">the track beat grid that was used to calculate the update</param>
    public TrackPositionUpdate(long timestamp, long milliseconds, int beatNumber, bool definitive,
        bool playing, double pitch, bool reverse, BeatGrid? beatGrid) :
        this(timestamp, milliseconds, beatNumber, definitive, playing, pitch, reverse, beatGrid,
            false, false)
    {
    }

    /// <summary>
    /// Constructor simply sets the properties of this immutable value class.
    /// </summary>
    /// <param name="timestamp">when this update was received</param>
    /// <param name="milliseconds">how far into the track has the player reached</param>
    /// <param name="beatNumber">the beat number that was reported (or incremented) by this update</param>
    /// <param name="definitive">indicates if this was based on a direct report of track position from the player (i.e. a beat)</param>
    /// <param name="playing">indicates whether the player was actively playing a track when this update was received</param>
    /// <param name="pitch">the playback pitch (where 1.0 is normal speed) when this update was received</param>
    /// <param name="reverse">indicates if the player was playing backwards when this update was received</param>
    /// <param name="beatGrid">the track beat grid that was used to calculate the update</param>
    /// <param name="precise">this position was derived from a high-precision position packet, directly or through a beat</param>
    /// <param name="fromBeat">this position was created in response to a beat packet</param>
    public TrackPositionUpdate(long timestamp, long milliseconds, int beatNumber, bool definitive,
        bool playing, double pitch, bool reverse, BeatGrid? beatGrid, bool precise, bool fromBeat)
    {
        Timestamp = timestamp;
        Milliseconds = milliseconds;
        BeatNumber = beatNumber;
        Definitive = definitive;
        Playing = playing;
        Pitch = pitch;
        Reverse = reverse;
        BeatGrid = beatGrid;
        Precise = precise;
        FromBeat = fromBeat;
    }

    /// <inheritdoc/>
    public override string ToString() =>
        $"TrackPositionUpdate[timestamp:{Timestamp}, milliseconds:{Milliseconds}, beatNumber:{BeatNumber}, " +
        $"definitive:{Definitive}, playing:{Playing}, pitch:{Pitch:F2}, reverse:{Reverse}, " +
        $"beatGrid:{BeatGrid}, precise:{Precise}, fromBeat:{FromBeat}]";
}
