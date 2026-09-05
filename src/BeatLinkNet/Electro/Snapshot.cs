namespace BeatLink.Electro;

/// <summary>
/// <para>Supports multiple coherent computations about a fixed point in time on a metronome's
/// timeline. The <see cref="Metronome"/> class provides answers with respect to the moment at which
/// you called each function, but if you need to work with more than one piece of information at a
/// time, be sure to use <see cref="Metronome.GetSnapshot()"/> so that each of your calculations
/// refer to the same moment. Otherwise you risk getting misleading results, such as when you ask
/// for a beat number in one call, and then the beat phase, and you have moved on to a different
/// beat in between the calls.</para>
///
/// <para>Snapshots in Afterglow also extend the notions of beat phase to enable oscillators with
/// frequencies that are fractions or multiples of a beat. This concept has been ported from the
/// Clojure version, and is available as
/// <see cref="Metronome.EnhancedPhase(long, double, double)"/> rather than additional overloads of
/// the snapshot members.</para>
///
/// <para>Ported from the <c>Snapshot</c> interface and <c>MetronomeSnapshot</c> class of Deep
/// Symmetry's electro library, by James Elliott.</para>
/// </summary>
public sealed class Snapshot
{
    /// <summary>
    /// Get the metronome's timeline origin: the point in time at which the metronome from which
    /// this snapshot was taken started counting, as a millisecond timestamp on the monotonic clock
    /// provided by <see cref="Util.CurrentTimestampNanos"/>.
    /// </summary>
    public long StartTime { get; }

    /// <summary>
    /// Get the metronome's tempo: the number of beats per minute at which the metronome from which
    /// this snapshot was taken was running.
    /// </summary>
    public double Tempo { get; }

    /// <summary>
    /// Get the metronome's bar length in beats: the number of beats which made up a bar in the
    /// metronome from which this snapshot was taken.
    /// </summary>
    public int BeatsPerBar { get; }

    /// <summary>
    /// Get the metronome's phrase length in bars: the number of bars which made up a phrase in the
    /// metronome from which this snapshot was taken.
    /// </summary>
    public int BarsPerPhrase { get; }

    /// <summary>
    /// Get the point in time with respect to which the snapshot is computed, as a millisecond
    /// timestamp. The difference between this and <see cref="StartTime"/>, along with
    /// <see cref="Tempo"/>, determine the other snapshot values.
    /// </summary>
    public long Instant { get; }

    /// <summary>
    /// Get the metronome's beat length in time: the duration of a beat, in milliseconds, at the
    /// tempo when the snapshot was taken.
    /// </summary>
    public double BeatInterval { get; }

    /// <summary>
    /// Get the metronome's bar length in time: the duration of a bar, in milliseconds, at the
    /// tempo when the snapshot was taken.
    /// </summary>
    public double BarInterval { get; }

    /// <summary>
    /// Get the metronome's phrase length in time: the duration of a phrase, in milliseconds, at
    /// the tempo when the snapshot was taken.
    /// </summary>
    public double PhraseInterval { get; }

    /// <summary>
    /// Create a snapshot of the state of a metronome at a particular instant in time.
    /// </summary>
    /// <param name="startTime">the millisecond timestamp at which the metronome's beat grid originated</param>
    /// <param name="tempo">the number of beats per minute at which the metronome was running</param>
    /// <param name="beatsPerBar">the number of beats which made up a bar in the metronome</param>
    /// <param name="barsPerPhrase">the number of bars which made up a phrase in the metronome</param>
    /// <param name="instant">the millisecond timestamp at which the state of the metronome is to be considered</param>
    internal Snapshot(long startTime, double tempo, int beatsPerBar, int barsPerPhrase, long instant)
    {
        StartTime = startTime;
        Tempo = tempo;
        BeatsPerBar = beatsPerBar;
        BarsPerPhrase = barsPerPhrase;
        Instant = instant;
        BeatInterval = Metronome.BeatsToMilliseconds(1, tempo);
        BarInterval = Metronome.BeatsToMilliseconds(beatsPerBar, tempo);
        PhraseInterval = Metronome.BeatsToMilliseconds(beatsPerBar * barsPerPhrase, tempo);
    }

    /// <summary>
    /// Get the metronome's beat number: the beat being played at the time represented by the
    /// snapshot. Beats start at 1.
    /// </summary>
    public long Beat => Metronome.MarkerNumber(Instant, StartTime, BeatInterval);

    /// <summary>
    /// Get the metronome's bar number: the bar being played at the time represented by the
    /// snapshot. Bars start at 1.
    /// </summary>
    public long Bar => Metronome.MarkerNumber(Instant, StartTime, BarInterval);

    /// <summary>
    /// Get the metronome's phrase number: the phrase being played at the time represented by the
    /// snapshot. Phrases start at 1.
    /// </summary>
    public long Phrase => Metronome.MarkerNumber(Instant, StartTime, PhraseInterval);

    /// <summary>
    /// Get the metronome's beat phase at the time of the snapshot, a value which starts at 0.0 at
    /// the very start of the beat, but never quite reaches 1.0, because that would be the start of
    /// the next beat.
    /// </summary>
    public double BeatPhase => Metronome.MarkerPhase(Instant, StartTime, BeatInterval);

    /// <summary>
    /// Get the metronome's bar phase at the time of the snapshot, a value which starts at 0.0 at
    /// the very start of the bar, but never quite reaches 1.0, because that would be the start of
    /// the next bar.
    /// </summary>
    public double BarPhase => Metronome.MarkerPhase(Instant, StartTime, BarInterval);

    /// <summary>
    /// Get the metronome's phrase phase at the time of the snapshot, a value which starts at 0.0
    /// at the very start of the phrase, but never quite reaches 1.0, because that would be the
    /// start of the next phrase.
    /// </summary>
    public double PhrasePhase => Metronome.MarkerPhase(Instant, StartTime, PhraseInterval);

    /// <summary>
    /// Determine the millisecond timestamp at which a particular beat will occur, given the
    /// metronome configuration when the snapshot was taken.
    /// </summary>
    /// <param name="beat">the number of the beat whose start time is desired</param>
    /// <returns>the time at which the specified beat begins, rounded to the nearest millisecond</returns>
    public long GetTimeOfBeat(long beat) =>
        (long)Math.Round((beat - 1) * BeatInterval, MidpointRounding.AwayFromZero) + StartTime;

    /// <summary>
    /// Return the beat number of the snapshot relative to the start of the bar: the down beat is
    /// 1, and the range goes up to the value of <see cref="BeatsPerBar"/>.
    /// </summary>
    public int BeatWithinBar
    {
        get
        {
            var beatSize = 1.0 / BeatsPerBar;
            return 1 + (int)Math.Floor(BarPhase / beatSize);
        }
    }

    /// <summary>
    /// Checks whether the current beat at the time of the snapshot was the first beat in its bar:
    /// <c>true</c> if the snapshot was taken during the first beat of a bar.
    /// </summary>
    public bool IsDownBeat => BeatWithinBar == 1;

    /// <summary>
    /// Return the beat number of the snapshot relative to the start of the phrase: the phrase
    /// starts with beat 1, and the range goes up to the value of <see cref="BeatsPerBar"/> times
    /// <see cref="BarsPerPhrase"/>.
    /// </summary>
    public int BeatWithinPhrase
    {
        get
        {
            var beatSize = 1.0 / (BeatsPerBar * BarsPerPhrase);
            return 1 + (int)Math.Floor(PhrasePhase / beatSize);
        }
    }

    /// <summary>
    /// Checks whether the current beat at the time of the snapshot was the first beat in its
    /// phrase: <c>true</c> if the snapshot was taken during the first beat of a phrase.
    /// </summary>
    public bool IsPhraseStart => BeatWithinPhrase == 1;

    /// <summary>
    /// Determine the millisecond timestamp at which a particular bar will occur, given the
    /// metronome configuration when the snapshot was taken.
    /// </summary>
    /// <param name="bar">the number of the bar whose start time is desired</param>
    /// <returns>the time at which the specified bar begins, rounded to the nearest millisecond</returns>
    public long GetTimeOfBar(long bar) =>
        (long)Math.Round((bar - 1) * BarInterval, MidpointRounding.AwayFromZero) + StartTime;

    /// <summary>
    /// Return the bar number of the snapshot relative to the start of the phrase: the phrase
    /// starts with bar 1, and the range goes up to the value of <see cref="BarsPerPhrase"/>.
    /// </summary>
    public int BarWithinPhrase
    {
        get
        {
            var barSize = 1.0 / BarsPerPhrase;
            return 1 + (int)Math.Floor(PhrasePhase / barSize);
        }
    }

    /// <summary>
    /// Determine the millisecond timestamp at which a particular phrase will occur, given the
    /// metronome configuration when the snapshot was taken.
    /// </summary>
    /// <param name="phrase">the number of the phrase whose start time is desired</param>
    /// <returns>the time at which the specified phrase begins, rounded to the nearest millisecond</returns>
    public long GetTimeOfPhrase(long phrase) =>
        (long)Math.Round((phrase - 1) * PhraseInterval, MidpointRounding.AwayFromZero) + StartTime;

    /// <summary>
    /// Returns the time represented by the snapshot as "phrase.bar.beat": a concise textual
    /// representation of the current metronome position at the time of the snapshot.
    /// </summary>
    public string Marker => $"{Phrase}.{BarWithinPhrase}.{BeatWithinBar}";

    /// <summary>
    /// Determine how far in time the snapshot is from its closest beat. The result will be
    /// positive if the beat has already occurred, and negative if it is coming up.
    /// </summary>
    public double DistanceFromBeat => Metronome.FindClosestDelta(BeatPhase) * BeatInterval;

    /// <summary>
    /// Determine how far in time the snapshot is from its closest bar boundary. The result will be
    /// positive if the bar has already started, and negative if it is coming up.
    /// </summary>
    public double DistanceFromBar => Metronome.FindClosestDelta(BarPhase) * BarInterval;

    /// <summary>
    /// Determine how far in time the snapshot is from its closest phrase boundary. The result will
    /// be positive if the phrase has already started, and negative if it is coming up.
    /// </summary>
    public double DistanceFromPhrase => Metronome.FindClosestDelta(PhrasePhase) * PhraseInterval;

    /// <inheritdoc/>
    public override string ToString() =>
        $"Snapshot[marker: {Marker}, startTime:{StartTime}, instant: {Instant}, beatPhase:{BeatPhase}" +
        $", barPhase:{BarPhase}, phrasePhase:{PhrasePhase}, tempo:{Tempo}, beatsPerBar:{BeatsPerBar}" +
        $", barsPerPhrase:{BarsPerPhrase}, beatInterval:{BeatInterval}, barInterval:{BarInterval}" +
        $", phraseInterval:{PhraseInterval}]";
}
