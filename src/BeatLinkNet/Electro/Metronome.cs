namespace BeatLink.Electro;

/// <summary>
/// <para>A beat management tool. Tell it what BPM you want, and it will compute beat, bar, and
/// phrase timestamps accordingly. If you only need a single piece of information, you can obtain
/// it directly from the metronome. If you need to work with two or more values, you need to call
/// <see cref="GetSnapshot()"/> and ask the snapshot for them or you will see inconsistent results,
/// since time will move on between each question you ask the metronome itself.</para>
///
/// <para>Timestamps are in milliseconds on the monotonic clock provided by
/// <see cref="Util.CurrentTimestampNanos"/> (the port deliberately prefers a monotonic time source
/// over the wall-clock time used by the Java implementation).</para>
///
/// <para>Inspired by Jeff Rose's work in
/// <see href="https://github.com/overtone/overtone/blob/master/src/overtone/music/rhythm.clj">Overtone</see>.
/// Ported from the electro library by James Elliott.</para>
/// </summary>
public class Metronome
{
    /// <summary>
    /// Used to protect access to the metronome's state, playing the role of the synchronized
    /// methods and atomic fields of the Java implementation.
    /// </summary>
    private readonly object _lock = new();

    /// <summary>
    /// The millisecond timestamp at which the beat grid originates.
    /// </summary>
    private long _startTime = CurrentTimeMillis();

    /// <summary>
    /// The number of beats per minute.
    /// </summary>
    private double _tempo = 120.0;

    /// <summary>
    /// The number of beats in a bar.
    /// </summary>
    private int _beatsPerBar = 4;

    /// <summary>
    /// The number of bars in a phrase.
    /// </summary>
    private int _barsPerPhrase = 8;

    /// <summary>
    /// Obtain the current millisecond timestamp on the monotonic clock which underlies the
    /// metronome's timeline, playing the role of Java's <c>System.currentTimeMillis()</c>.
    /// </summary>
    /// <returns>the number of milliseconds elapsed on the monotonic clock</returns>
    private static long CurrentTimeMillis() => Util.CurrentTimestampNanos() / 1_000_000;

    /// <summary>
    /// Create a new metronome with default configuration. Its start time is now, its tempo is
    /// 120.0 beats per minute, counting four beats per bar, and eight bars per phrase.
    /// </summary>
    public Metronome()
    {
    }

    /// <summary>
    /// Create a metronome which is a copy of another metronome, that is sharing the same start
    /// time, tempo, beats per bar, and bars per phrase. Once created, the metronomes are
    /// independent, so changes to one will not affect the other.
    /// </summary>
    /// <param name="template">the metronome whose configuration is to be copied</param>
    public Metronome(Metronome template)
    {
        lock (template._lock)
        {
            _startTime = template._startTime;
            _tempo = template._tempo;
            _beatsPerBar = template._beatsPerBar;
            _barsPerPhrase = template._barsPerPhrase;
        }
    }

    /// <summary>
    /// The time at which this metronome was effectively started, as a millisecond timestamp
    /// (tempo changes will shift this, as will a variety of methods which adjust the timeline).
    /// </summary>
    public long StartTime
    {
        get
        {
            lock (_lock)
            {
                return _startTime;
            }
        }
    }

    /// <summary>
    /// Adds a number of milliseconds to the start time of the metronome. Useful to nudge it back
    /// into synchronization with an external source.
    /// </summary>
    /// <param name="ms">the number of milliseconds to add to the start time</param>
    public void AdjustStart(long ms)
    {
        lock (_lock)
        {
            _startTime += ms;
        }
    }

    /// <summary>
    /// The tempo at which this metronome is running, as a number of beats per minute. Establishing
    /// a new tempo adjusts the start time so that the current beat and phase are unaffected by the
    /// tempo change.
    /// </summary>
    public double Tempo
    {
        get
        {
            lock (_lock)
            {
                return _tempo;
            }
        }
        set
        {
            lock (_lock)
            {
                var instant = CurrentTimeMillis();
                var start = _startTime;
                var interval = BeatsToMilliseconds(1, _tempo);
                var beat = MarkerNumber(instant, start, interval);
                var phase = MarkerPhase(instant, start, interval);
                var newInterval = BeatsToMilliseconds(1, value);
                _startTime = instant - (long)Math.Round(newInterval * (phase + beat - 1), MidpointRounding.AwayFromZero);
                _tempo = value;
            }
        }
    }

    /// <summary>
    /// The number of beats per bar in the metronome's beat grid. The default value is four.
    /// </summary>
    /// <exception cref="ArgumentException">if the value assigned is not greater than zero</exception>
    public int BeatsPerBar
    {
        get
        {
            lock (_lock)
            {
                return _beatsPerBar;
            }
        }
        set
        {
            if (value <= 0)
            {
                throw new ArgumentException("BeatsPerBar must be greater than zero", nameof(value));
            }
            lock (_lock)
            {
                _beatsPerBar = value;
            }
        }
    }

    /// <summary>
    /// The number of bars per phrase in the metronome's beat grid. The default value is eight.
    /// </summary>
    /// <exception cref="ArgumentException">if the value assigned is not greater than zero</exception>
    public int BarsPerPhrase
    {
        get
        {
            lock (_lock)
            {
                return _barsPerPhrase;
            }
        }
        set
        {
            if (value <= 0)
            {
                throw new ArgumentException("BarsPerPhrase must be greater than zero", nameof(value));
            }
            lock (_lock)
            {
                _barsPerPhrase = value;
            }
        }
    }

    /// <summary>
    /// Calculate the number of milliseconds taken by the specified number of beats at the
    /// specified tempo.
    /// </summary>
    /// <param name="beats">the number of beats to time</param>
    /// <param name="tempo">the number of those beats which play in a minute</param>
    /// <returns>the number of milliseconds that would pass while that many beats play at that tempo</returns>
    public static double BeatsToMilliseconds(long beats, double tempo) => 60000.0 / tempo * beats;

    /// <summary>
    /// Helper function to calculate the beat, bar, or phrase number in effect at a given instant
    /// (in milliseconds) given a timeline starting point (start, also in milliseconds), and the
    /// interval (also in milliseconds) between beats, bars, or phrases.
    /// </summary>
    /// <param name="instant">the time (in milliseconds) for which a marker number is desired</param>
    /// <param name="start">the time (in milliseconds) at which the metronome started counting</param>
    /// <param name="interval">the time (in milliseconds) between markers</param>
    /// <returns>the marker number currently in effect for the metronome at the specified instant</returns>
    public static long MarkerNumber(long instant, long start, double interval) =>
        (long)Math.Floor((instant - start) / interval) + 1;

    /// <summary>
    /// Helper function to calculate the beat, bar, or phrase phase at a given instant (in
    /// milliseconds), given a timeline starting point (start, also in milliseconds), and the
    /// interval (also in milliseconds) between beats, bars, or phrases. A marker phase starts at
    /// 0.0 at the beginning of the beat, bar, or phrase, rises linearly during the beat, bar, or
    /// phrase, but never reaches 1.0, because that is the start of the next beat, bar, or phrase.
    /// </summary>
    /// <param name="instant">the time (in milliseconds) for which a marker phase is desired</param>
    /// <param name="start">the time (in milliseconds) at which the metronome started counting</param>
    /// <param name="interval">the time (in milliseconds) between markers</param>
    /// <returns>the phase in effect for the specified marker at the specified instant, in the range [0.0, 1.0)</returns>
    public static double MarkerPhase(long instant, long start, double interval)
    {
        var ratio = (instant - start) / interval;
        return ratio - Math.Floor(ratio);
    }

    /// <summary>
    /// <para>Helper function to calculate phase with respect to multiples or fractions of a marker
    /// (beat, bar, or phrase), given the phase with respect to that marker, the marker number, and
    /// the desired ratio. A <paramref name="desiredRatio"/> of <c>1.0</c> returns the phase
    /// unchanged; <c>0.5</c> (1/2) oscillates twice as fast, <c>0.75</c> (3/4) oscillates 4 times
    /// every 3 markers...</para>
    ///
    /// <para>See the
    /// <see href="https://afterglow-guide.deepsymmetry.org/afterglow/oscillators.html#ratios">Ratios
    /// illustration</see> in the Afterglow documentation for more details with graphs.</para>
    ///
    /// <para><em>Only positive values were considered for the ratio when writing this algorithm;
    /// the results you'll get if you pass in zero or a negative value are not likely
    /// meaningful.</em></para>
    /// </summary>
    /// <param name="markerNumber">the current marker number being considered, as returned by <see cref="MarkerNumber(long, long, double)"/></param>
    /// <param name="markerPhase">the current phase with respect to the marker, as returned by <see cref="MarkerPhase(long, long, double)"/></param>
    /// <param name="desiredRatio">the ratio by which to oscillate the phase</param>
    /// <returns>the oscillated phase</returns>
    /// <seealso cref="EnhancedPhase(long, double, long, long)"/>
    public static double EnhancedPhase(long markerNumber, double markerPhase, double desiredRatio)
    {
        var (numerator, denominator) = ApproximateAsFraction(desiredRatio, 0.00000002, 10000);
        return EnhancedPhase(markerNumber, markerPhase, numerator, denominator);
    }

    /// <summary>
    /// Convert a double to the closest simple rational approximation within the specified
    /// tolerance, using a continued fraction expansion. Plays the role of the Apache Commons Math
    /// <c>BigFraction(double, double, int)</c> constructor used by the Java implementation.
    /// </summary>
    /// <param name="value">the value whose rational approximation is desired</param>
    /// <param name="epsilon">the maximum allowable difference between the value and the approximation</param>
    /// <param name="maxIterations">the maximum number of continued fraction convergents to compute</param>
    /// <returns>the numerator and denominator of the approximating fraction</returns>
    /// <exception cref="ArithmeticException">if the value cannot be approximated within the iteration limit</exception>
    private static (long Numerator, long Denominator) ApproximateAsFraction(double value, double epsilon, int maxIterations)
    {
        const long overflow = int.MaxValue;
        var r0 = value;
        var a0 = (long)Math.Floor(r0);
        if (Math.Abs(a0) > overflow)
        {
            throw new ArithmeticException($"Cannot convert {value} to a fraction: overflow");
        }

        if (Math.Abs(a0 - value) < epsilon)
        {
            return (a0, 1);
        }

        long p0 = 1;
        long q0 = 0;
        var p1 = a0;
        long q1 = 1;
        long p2;
        long q2;
        var n = 0;
        bool stop;
        do
        {
            ++n;
            var r1 = 1.0 / (r0 - a0);
            var a1 = (long)Math.Floor(r1);
            p2 = a1 * p1 + p0;
            q2 = a1 * q1 + q0;
            if (Math.Abs(p2) > overflow || Math.Abs(q2) > overflow)
            {
                throw new ArithmeticException($"Cannot convert {value} to a fraction: overflow");
            }

            var convergent = p2 / (double)q2;
            stop = n >= maxIterations || Math.Abs(convergent - value) <= epsilon;
            if (!stop)
            {
                p0 = p1;
                p1 = p2;
                q0 = q1;
                q1 = q2;
                a0 = a1;
                r0 = r1;
            }
        } while (!stop);

        if (n >= maxIterations)
        {
            throw new ArithmeticException($"Cannot convert {value} to a fraction: too many iterations");
        }

        return (p2, q2);
    }

    /// <summary>
    /// <para>Helper function to calculate phase with respect to multiples or fractions of a marker
    /// (beat, bar, or phrase), given the phase with respect to that marker, the marker number, and
    /// the desired ratio. A ratio of 1/1 returns the phase unchanged; 1/2 oscillates twice as
    /// fast, 3/4 oscillates 4 times every 3 markers...</para>
    ///
    /// <para>See the
    /// <see href="https://afterglow-guide.deepsymmetry.org/afterglow/oscillators.html#ratios">Ratios
    /// illustration</see> in the Afterglow documentation for more details with graphs.</para>
    ///
    /// <para><em>Only positive values were considered for the numerator and denominator when
    /// writing this algorithm; the results you'll get if you pass in zero or a negative value are
    /// not likely meaningful.</em></para>
    /// </summary>
    /// <param name="markerNumber">the current marker number being considered, as returned by <see cref="MarkerNumber(long, long, double)"/></param>
    /// <param name="markerPhase">the current phase with respect to the marker, as returned by <see cref="MarkerPhase(long, long, double)"/></param>
    /// <param name="numerator">over how many markers should an oscillation cycle span</param>
    /// <param name="denominator">how many oscillations should occur in that span</param>
    /// <returns>the oscillated phase</returns>
    /// <seealso cref="EnhancedPhase(long, double, double)"/>
    public static double EnhancedPhase(long markerNumber, double markerPhase, long numerator, long denominator)
    {
        var basePhase = numerator > 1
            ? (markerNumber - 1) % numerator + markerPhase / numerator
            : markerPhase;
        var adjustedPhase = basePhase * denominator;
        return adjustedPhase - Math.Floor(adjustedPhase);
    }

    /// <summary>
    /// Ensure that a phase falls in the range [0.0, 1.0). Values outside the range will have their
    /// non-fractional part discarded.
    /// </summary>
    /// <param name="phase">a phase value that may require normalization to within the unit range</param>
    /// <returns>the normalized phase</returns>
    public static double NormalizePhase(double phase)
    {
        if (phase < 0.0)
        {
            return phase - (long)phase + 1.0;
        }
        return phase - (long)phase;
    }

    /// <summary>
    /// The number of milliseconds a beat lasts given the current tempo.
    /// </summary>
    public double BeatInterval
    {
        get
        {
            lock (_lock)
            {
                return BeatsToMilliseconds(1, _tempo);
            }
        }
    }

    /// <summary>
    /// The number of milliseconds a bar lasts given the current configuration and tempo.
    /// </summary>
    public double BarInterval
    {
        get
        {
            lock (_lock)
            {
                return BeatsToMilliseconds(_beatsPerBar, _tempo);
            }
        }
    }

    /// <summary>
    /// The number of milliseconds a phrase lasts given the current configuration and tempo.
    /// </summary>
    public double PhraseInterval
    {
        get
        {
            lock (_lock)
            {
                return BeatsToMilliseconds(_beatsPerBar * _barsPerPhrase, _tempo);
            }
        }
    }

    /// <summary>
    /// Get the current beat being played.
    /// </summary>
    /// <returns>the current beat number, which starts at 1</returns>
    public long GetBeat()
    {
        lock (_lock)
        {
            return MarkerNumber(CurrentTimeMillis(), _startTime, BeatsToMilliseconds(1, _tempo));
        }
    }

    /// <summary>
    /// Restarts the metronome at the beginning of the specified beat number.
    /// </summary>
    /// <param name="beat">the beat to which the metronome should jump; the first beat is beat 1</param>
    public void JumpToBeat(long beat)
    {
        lock (_lock)
        {
            _startTime = CurrentTimeMillis() -
                (long)Math.Round((beat - 1) * BeatsToMilliseconds(1, _tempo), MidpointRounding.AwayFromZero);
        }
    }

    /// <summary>
    /// Determine the millisecond timestamp at which a particular beat will occur.
    /// </summary>
    /// <param name="beat">the number of the beat whose start time is desired</param>
    /// <returns>the time at which the specified beat begins, to the nearest millisecond</returns>
    public long GetTimeOfBeat(long beat)
    {
        lock (_lock)
        {
            return (long)Math.Round((beat - 1) * BeatsToMilliseconds(1, _tempo), MidpointRounding.AwayFromZero) + _startTime;
        }
    }

    /// <summary>
    /// Determine the distance traveled into the current beat as a phase number in the range
    /// [0.0, 1.0).
    /// </summary>
    /// <returns>the current beat phase</returns>
    public double GetBeatPhase()
    {
        lock (_lock)
        {
            return MarkerPhase(CurrentTimeMillis(), _startTime, BeatsToMilliseconds(1, _tempo));
        }
    }

    /// <summary>
    /// Figures out the least disruptive phase shift that ends up in a target phase.
    /// </summary>
    /// <param name="delta">the amount to be added to our current phase to achieve a desired phase</param>
    /// <returns>an amount that will yield the same phase while changing our position the least</returns>
    public static double FindClosestDelta(double delta) =>
        delta > 0.5 ? delta - 1.0 : delta < -0.5 ? delta + 1.0 : delta;

    /// <summary>
    /// Nudge the metronome so that it has reached the specified part of its current beat. If the
    /// value supplied is outside the range of a beat phase (less than zero or greater than or
    /// equal to one), it will be normalized to fit into that range by ignoring the non-fractional
    /// part.
    /// </summary>
    /// <param name="phase">the desired beat phase, in the range [0.0, 1.0)</param>
    public void SetBeatPhase(double phase)
    {
        lock (_lock)
        {
            var interval = BeatsToMilliseconds(1, _tempo);
            var current = MarkerPhase(CurrentTimeMillis(), _startTime, interval);
            var delta = FindClosestDelta(NormalizePhase(phase) - current);
            var shift = (long)Math.Round(interval * delta, MidpointRounding.AwayFromZero);
            _startTime -= shift;
        }
    }

    /// <summary>
    /// Get the current bar being played.
    /// </summary>
    /// <returns>the current bar number, which starts at 1</returns>
    public long GetBar()
    {
        lock (_lock)
        {
            return MarkerNumber(CurrentTimeMillis(), _startTime, BeatsToMilliseconds(_beatsPerBar, _tempo));
        }
    }

    /// <summary>
    /// Restarts the metronome at the start of the specified bar, keeping the beat phase unchanged
    /// in case it is being synchronized to an external source.
    /// </summary>
    /// <param name="bar">the bar to which the metronome should jump; the first bar is bar 1</param>
    /// <remarks>The Java implementation subtracts the bar offset with an inverted sign, which
    /// moves the start time into the future and yields negative bar numbers; this port implements
    /// the documented intent instead.</remarks>
    public void JumpToBar(long bar)
    {
        lock (_lock)
        {
            var beatInterval = BeatsToMilliseconds(1, _tempo);
            var phase = MarkerPhase(CurrentTimeMillis(), _startTime, beatInterval);
            var closestPhase = phase > 0.5 ? phase - 1.0 : phase;
            var shift = beatInterval * closestPhase;
            var barInterval = BeatsToMilliseconds(_beatsPerBar, _tempo);
            _startTime = CurrentTimeMillis() -
                (long)Math.Round(shift + (bar - 1) * barInterval, MidpointRounding.AwayFromZero);
        }
    }

    /// <summary>
    /// Determine the millisecond timestamp at which a particular bar will occur.
    /// </summary>
    /// <param name="bar">the number of the bar whose start time is desired</param>
    /// <returns>the time at which the specified bar begins, rounded to the nearest millisecond</returns>
    public long GetTimeOfBar(long bar)
    {
        lock (_lock)
        {
            return (long)Math.Round((bar - 1) * BeatsToMilliseconds(_beatsPerBar, _tempo), MidpointRounding.AwayFromZero) + _startTime;
        }
    }

    /// <summary>
    /// Determine the distance traveled into the current bar as a phase number in the range
    /// [0.0, 1.0).
    /// </summary>
    /// <returns>the current bar phase</returns>
    public double GetBarPhase()
    {
        lock (_lock)
        {
            return MarkerPhase(CurrentTimeMillis(), _startTime, BeatsToMilliseconds(_beatsPerBar, _tempo));
        }
    }

    /// <summary>
    /// Nudge the metronome so that it has reached the specified part of its current bar. If the
    /// value supplied is outside the range of a bar phase (less than zero or greater than or equal
    /// to one), it will be normalized to fit into that range by ignoring the non-fractional part.
    /// </summary>
    /// <param name="phase">the desired bar phase, in the range [0.0, 1.0)</param>
    public void SetBarPhase(double phase)
    {
        lock (_lock)
        {
            var interval = BeatsToMilliseconds(_beatsPerBar, _tempo);
            var current = MarkerPhase(CurrentTimeMillis(), _startTime, interval);
            var delta = FindClosestDelta(NormalizePhase(phase) - current);
            var shift = (long)Math.Round(interval * delta, MidpointRounding.AwayFromZero);
            _startTime -= shift;
        }
    }

    /// <summary>
    /// Get the current phrase being played.
    /// </summary>
    /// <returns>the current phrase number, which starts at 1</returns>
    public long GetPhrase()
    {
        lock (_lock)
        {
            return MarkerNumber(CurrentTimeMillis(), _startTime, BeatsToMilliseconds(_beatsPerBar * _barsPerPhrase, _tempo));
        }
    }

    /// <summary>
    /// Restarts the metronome at the start of the specified phrase, keeping the beat phase
    /// unchanged in case it is being synchronized to an external source.
    /// </summary>
    /// <param name="phrase">the phrase to which the metronome should jump; the first phrase is phrase 1</param>
    /// <remarks>The Java implementation subtracts the phrase offset with an inverted sign, which
    /// moves the start time into the future and yields negative phrase numbers; this port
    /// implements the documented intent instead.</remarks>
    public void JumpToPhrase(long phrase)
    {
        lock (_lock)
        {
            var beatInterval = BeatsToMilliseconds(1, _tempo);
            var phase = MarkerPhase(CurrentTimeMillis(), _startTime, beatInterval);
            var closestPhase = phase > 0.5 ? phase - 1.0 : phase;
            var shift = beatInterval * closestPhase;
            var phraseInterval = BeatsToMilliseconds(_beatsPerBar * _barsPerPhrase, _tempo);
            _startTime = CurrentTimeMillis() -
                (long)Math.Round(shift + (phrase - 1) * phraseInterval, MidpointRounding.AwayFromZero);
        }
    }

    /// <summary>
    /// Determine the millisecond timestamp at which a particular phrase will occur.
    /// </summary>
    /// <param name="phrase">the number of the phrase whose start time is desired</param>
    /// <returns>the time at which the specified phrase begins, rounded to the nearest millisecond</returns>
    public long GetTimeOfPhrase(long phrase)
    {
        lock (_lock)
        {
            return (long)Math.Round((phrase - 1) * BeatsToMilliseconds(_beatsPerBar * _barsPerPhrase, _tempo), MidpointRounding.AwayFromZero) + _startTime;
        }
    }

    /// <summary>
    /// Determine the distance traveled into the current phrase as a phase number in the range
    /// [0.0, 1.0).
    /// </summary>
    /// <returns>the current phrase phase</returns>
    public double GetPhrasePhase()
    {
        lock (_lock)
        {
            return MarkerPhase(CurrentTimeMillis(), _startTime, BeatsToMilliseconds(_beatsPerBar * _barsPerPhrase, _tempo));
        }
    }

    /// <summary>
    /// Nudge the metronome so that it has reached the specified part of its current phrase. If the
    /// value supplied is outside the range of a phrase phase (less than zero or greater than or
    /// equal to one), it will be normalized to fit into that range by ignoring the non-fractional
    /// part.
    /// </summary>
    /// <param name="phase">the desired phrase phase, in the range [0.0, 1.0)</param>
    public void SetPhrasePhase(double phase)
    {
        lock (_lock)
        {
            var interval = BeatsToMilliseconds(_beatsPerBar * _barsPerPhrase, _tempo);
            var current = MarkerPhase(CurrentTimeMillis(), _startTime, interval);
            var delta = FindClosestDelta(NormalizePhase(phase) - current);
            var shift = (long)Math.Round(interval * delta, MidpointRounding.AwayFromZero);
            _startTime -= shift;
        }
    }

    /// <summary>
    /// Take a snapshot of the current beat, bar, phrase, and phase state, so coherent calculations
    /// about them can be performed with respect to a static point in time.
    /// </summary>
    /// <returns>a representation of the detailed metronome state at the current moment</returns>
    public Snapshot GetSnapshot() => GetSnapshot(CurrentTimeMillis());

    /// <summary>
    /// Take a snapshot of the beat, bar, phrase, and phase state that the metronome would have at
    /// the specified millisecond timestamp, so coherent calculations about them can be performed
    /// with respect to that static point in time.
    /// </summary>
    /// <param name="instant">the point in time which this snapshot should capture</param>
    /// <returns>a representation of the detailed metronome state at the specified moment</returns>
    public Snapshot GetSnapshot(long instant)
    {
        lock (_lock)
        {
            return new Snapshot(_startTime, _tempo, _beatsPerBar, _barsPerPhrase, instant);
        }
    }

    /// <summary>
    /// Returns the current count of the metronome as "phrase.bar.beat".
    /// </summary>
    /// <returns>a concise textual representation of the current metronome position</returns>
    public string GetMarker() => GetSnapshot().Marker;

    /// <summary>
    /// Checks the current point on the metronome's timeline; since you are working with a live
    /// metronome, always returns the current time. If you are doing computations around this, you
    /// probably want to call <see cref="GetSnapshot()"/> and work with that instead.
    /// </summary>
    /// <returns>the current monotonic time in milliseconds</returns>
    public long GetInstant() => CurrentTimeMillis();

    /// <summary>
    /// Return the current beat number relative to the start of the bar: the down beat is 1, and
    /// the range goes up to the value of <see cref="BeatsPerBar"/>.
    /// </summary>
    /// <returns>the beat number within the current bar being counted</returns>
    public int GetBeatWithinBar()
    {
        lock (_lock)
        {
            var beatSize = 1.0 / _beatsPerBar;
            var barPhase = MarkerPhase(CurrentTimeMillis(), _startTime, BeatsToMilliseconds(_beatsPerBar, _tempo));
            return 1 + (int)Math.Floor(barPhase / beatSize);
        }
    }

    /// <summary>
    /// Checks whether the current beat is the first beat in its bar.
    /// </summary>
    /// <returns><c>true</c> if we are currently in the first beat of a bar</returns>
    public bool IsDownBeat() => GetBeatWithinBar() == 1;

    /// <summary>
    /// Return the current beat number relative to the start of the phrase: the phrase starts with
    /// beat 1, and the range goes up to the value of <see cref="BeatsPerBar"/> times
    /// <see cref="BarsPerPhrase"/>.
    /// </summary>
    /// <returns>the beat number within the current phrase being counted</returns>
    public int GetBeatWithinPhrase()
    {
        lock (_lock)
        {
            var beatSize = 1.0 / (_beatsPerBar * _barsPerPhrase);
            var phrasePhase = MarkerPhase(CurrentTimeMillis(), _startTime, BeatsToMilliseconds(_beatsPerBar * _barsPerPhrase, _tempo));
            return 1 + (int)Math.Floor(phrasePhase / beatSize);
        }
    }

    /// <summary>
    /// Checks whether the current beat is the first beat in its phrase.
    /// </summary>
    /// <returns><c>true</c> if we are currently in the first beat of a phrase</returns>
    public bool IsPhraseStart() => GetBeatWithinPhrase() == 1;

    /// <summary>
    /// Return the current bar number relative to the start of the phrase: the phrase starts with
    /// bar 1, and the range goes up to the value of <see cref="BarsPerPhrase"/>.
    /// </summary>
    /// <returns>the bar number within the current phrase being counted</returns>
    public int GetBarWithinPhrase()
    {
        lock (_lock)
        {
            var barSize = 1.0 / _barsPerPhrase;
            var phrasePhase = MarkerPhase(CurrentTimeMillis(), _startTime, BeatsToMilliseconds(_beatsPerBar * _barsPerPhrase, _tempo));
            return 1 + (int)Math.Floor(phrasePhase / barSize);
        }
    }

    /// <summary>
    /// Determine how far in time the metronome is from the closest beat. The result will be
    /// positive if the beat has already occurred, and negative if it is coming up.
    /// </summary>
    /// <returns>the distance in milliseconds from the closest beat on the metronome's timeline</returns>
    public double DistanceFromBeat()
    {
        lock (_lock)
        {
            var interval = BeatsToMilliseconds(1, _tempo);
            return FindClosestDelta(MarkerPhase(CurrentTimeMillis(), _startTime, interval)) * interval;
        }
    }

    /// <summary>
    /// Determine how far in time the metronome is from its closest bar boundary. The result will
    /// be positive if the bar has already started, and negative if it is coming up.
    /// </summary>
    /// <returns>the distance in milliseconds from the closest bar boundary on the metronome's timeline</returns>
    public double DistanceFromBar()
    {
        lock (_lock)
        {
            var interval = BeatsToMilliseconds(_beatsPerBar, _tempo);
            return FindClosestDelta(MarkerPhase(CurrentTimeMillis(), _startTime, interval)) * interval;
        }
    }

    /// <summary>
    /// Determine how far in time the metronome is from its closest phrase boundary. The result
    /// will be positive if the phrase has already started, and negative if it is coming up.
    /// </summary>
    /// <returns>the distance in milliseconds from the closest phrase boundary on the metronome's timeline</returns>
    public double DistanceFromPhrase()
    {
        lock (_lock)
        {
            var interval = BeatsToMilliseconds(_beatsPerBar * _barsPerPhrase, _tempo);
            return FindClosestDelta(MarkerPhase(CurrentTimeMillis(), _startTime, interval)) * interval;
        }
    }

    /// <inheritdoc/>
    public override string ToString() => $"Metronome[{GetSnapshot()}]";
}
