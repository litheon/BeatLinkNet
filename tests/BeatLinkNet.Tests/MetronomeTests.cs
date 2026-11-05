using BeatLink.Electro;

namespace BeatLink.Tests;

public class MetronomeTests
{
    [Theory]
    [InlineData(1, 120.0, 500.0)]
    [InlineData(1, 60.0, 1000.0)]
    [InlineData(1, 128.0, 468.75)]
    [InlineData(4, 120.0, 2000.0)]
    [InlineData(32, 120.0, 16000.0)]
    [InlineData(1, 240.0, 250.0)]
    [InlineData(0, 120.0, 0.0)]
    public void BeatsToMillisecondsMatchesExpectedDurations(long beats, double tempo, double expected)
    {
        Assert.Equal(expected, Metronome.BeatsToMilliseconds(beats, tempo), 10);
    }

    [Fact]
    public void DefaultConfigurationMatchesJavaImplementation()
    {
        var metronome = new Metronome();
        Assert.Equal(120.0, metronome.Tempo, 10);
        Assert.Equal(4, metronome.BeatsPerBar);
        Assert.Equal(8, metronome.BarsPerPhrase);
        Assert.Equal(500.0, metronome.BeatInterval, 10);
        Assert.Equal(2000.0, metronome.BarInterval, 10);
        Assert.Equal(16000.0, metronome.PhraseInterval, 10);
    }

    [Fact]
    public void SnapshotArithmeticAtSyntheticTimestamp()
    {
        var metronome = new Metronome();
        var start = metronome.StartTime;

        // 1250ms past the start of the grid at 120 BPM: halfway through beat 3.
        var snapshot = metronome.GetSnapshot(start + 1250);
        Assert.Equal(start, snapshot.StartTime);
        Assert.Equal(start + 1250, snapshot.Instant);
        Assert.Equal(3, snapshot.Beat);
        Assert.Equal(0.5, snapshot.BeatPhase, 10);
        Assert.Equal(1, snapshot.Bar);
        Assert.Equal(0.625, snapshot.BarPhase, 10);
        Assert.Equal(3, snapshot.BeatWithinBar);
        Assert.False(snapshot.IsDownBeat);
        Assert.Equal(1, snapshot.Phrase);
        Assert.Equal(3, snapshot.BeatWithinPhrase);
        Assert.Equal(1, snapshot.BarWithinPhrase);
        Assert.False(snapshot.IsPhraseStart);
        Assert.Equal("1.1.3", snapshot.Marker);
        Assert.Equal(start + 1000, snapshot.GetTimeOfBeat(3));
        Assert.Equal(start + 2000, snapshot.GetTimeOfBar(2));
        Assert.Equal(start + 16000, snapshot.GetTimeOfPhrase(2));
    }

    [Fact]
    public void SnapshotCrossesBarAndPhraseBoundaries()
    {
        var metronome = new Metronome();
        var start = metronome.StartTime;

        // 18500ms into the grid at 120 BPM: beat 38, bar 10, phrase 2.
        var snapshot = metronome.GetSnapshot(start + 18500);
        Assert.Equal(38, snapshot.Beat);
        Assert.Equal(10, snapshot.Bar);
        Assert.Equal(2, snapshot.Phrase);
        Assert.Equal(2, snapshot.BeatWithinBar);
        Assert.Equal(6, snapshot.BeatWithinPhrase);
        Assert.Equal(2, snapshot.BarWithinPhrase);
        Assert.Equal("2.2.2", snapshot.Marker);
    }

    [Fact]
    public void SnapshotAtExactStartIsDownBeatAndPhraseStart()
    {
        var metronome = new Metronome();
        var snapshot = metronome.GetSnapshot(metronome.StartTime);
        Assert.Equal(1, snapshot.Beat);
        Assert.Equal(0.0, snapshot.BeatPhase, 10);
        Assert.True(snapshot.IsDownBeat);
        Assert.True(snapshot.IsPhraseStart);
        Assert.Equal("1.1.1", snapshot.Marker);
    }

    [Fact]
    public void SnapshotBeforeStartTimeCountsNegativeMarkers()
    {
        var metronome = new Metronome();
        var start = metronome.StartTime;

        // 250ms before the grid origin: beat 0, three quarters of the way through it.
        var snapshot = metronome.GetSnapshot(start - 250);
        Assert.Equal(0, snapshot.Beat);
        Assert.Equal(0.5, snapshot.BeatPhase, 10);
    }

    [Fact]
    public void SnapshotIntervalMathReflectsConfiguration()
    {
        var metronome = new Metronome
        {
            BeatsPerBar = 3,
            BarsPerPhrase = 4,
        };
        metronome.Tempo = 60.0;
        var snapshot = metronome.GetSnapshot(metronome.StartTime);
        Assert.Equal(60.0, snapshot.Tempo, 10);
        Assert.Equal(3, snapshot.BeatsPerBar);
        Assert.Equal(4, snapshot.BarsPerPhrase);
        Assert.Equal(1000.0, snapshot.BeatInterval, 10);
        Assert.Equal(3000.0, snapshot.BarInterval, 10);
        Assert.Equal(12000.0, snapshot.PhraseInterval, 10);
    }

    [Fact]
    public void SnapshotDistanceFromMarkers()
    {
        var metronome = new Metronome();
        var start = metronome.StartTime;

        // 100ms after the start of beat 2 the beat is behind us, and beat 3 is 400ms ahead.
        var early = metronome.GetSnapshot(start + 600);
        Assert.Equal(100.0, early.DistanceFromBeat, 6);

        // 400ms into a beat, the closest beat is the next one, 100ms in the future.
        var late = metronome.GetSnapshot(start + 900);
        Assert.Equal(-100.0, late.DistanceFromBeat, 6);

        // 300ms into a bar the bar boundary is behind us; 1900ms in, the next one is 100ms ahead.
        Assert.Equal(300.0, metronome.GetSnapshot(start + 2300).DistanceFromBar, 6);
        Assert.Equal(-100.0, metronome.GetSnapshot(start + 1900).DistanceFromBar, 6);

        // Similarly for phrases: 15s in, the next phrase boundary is 1s ahead.
        Assert.Equal(-1000.0, metronome.GetSnapshot(start + 15000).DistanceFromPhrase, 6);
    }

    [Fact]
    public void TempoChangePreservesBeatAndPhase()
    {
        var metronome = new Metronome();
        metronome.JumpToBeat(10);
        metronome.Tempo = 60.0;

        // The tempo setter anchors the timeline so the beat and phase are unchanged at the moment
        // of the change; only microseconds have elapsed, so the phase must still be very small.
        Assert.Equal(60.0, metronome.Tempo, 10);
        Assert.Equal(10, metronome.GetBeat());
        Assert.True(metronome.GetBeatPhase() < 0.1, $"Beat phase should be preserved, was {metronome.GetBeatPhase()}");

        // The snapshot at the time of beat 10 confirms the anchor mathematically.
        var snapshot = metronome.GetSnapshot(metronome.GetTimeOfBeat(10));
        Assert.Equal(10, snapshot.Beat);
        Assert.Equal(0.0, snapshot.BeatPhase, 10);
    }

    [Fact]
    public void JumpToBeatRestartsAtRequestedBeat()
    {
        var metronome = new Metronome();
        metronome.JumpToBeat(15);
        Assert.Equal(15, metronome.GetBeat());

        var snapshot = metronome.GetSnapshot(metronome.GetTimeOfBeat(15));
        Assert.Equal(15, snapshot.Beat);
        Assert.Equal(0.0, snapshot.BeatPhase, 10);

        // Beat 16 starts one beat interval later.
        Assert.Equal(snapshot.GetTimeOfBeat(15) + 500, snapshot.GetTimeOfBeat(16));
    }

    [Fact]
    public void JumpToBarAndPhraseRestartAtRequestedMarkers()
    {
        var metronome = new Metronome();
        metronome.JumpToBar(3);
        Assert.Equal(3, metronome.GetBar());

        metronome.JumpToPhrase(2);
        Assert.Equal(2, metronome.GetPhrase());
    }

    [Theory]
    [InlineData(0.25, 0.25)]
    [InlineData(0.75, 0.75)]
    [InlineData(3.25, 0.25)]  // Whole part discarded.
    [InlineData(-0.75, 0.25)] // Negative values wrap into the unit range.
    public void SetBeatPhaseReachesRequestedPhase(double requested, double expected)
    {
        var metronome = new Metronome();
        metronome.SetBeatPhase(requested);
        Assert.Equal(expected, metronome.GetBeatPhase(), 2);
    }

    [Fact]
    public void SetBarAndPhrasePhaseReachRequestedPhases()
    {
        var metronome = new Metronome();
        metronome.SetBarPhase(0.5);
        Assert.Equal(0.5, metronome.GetBarPhase(), 2);

        metronome.SetPhrasePhase(0.25);
        Assert.Equal(0.25, metronome.GetPhrasePhase(), 3);
    }

    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(0.25, 0.25)]
    [InlineData(1.25, 0.25)]
    [InlineData(2.0, 0.0)]
    [InlineData(-0.25, 0.75)]
    [InlineData(-1.75, 0.25)]
    public void NormalizePhaseWrapsIntoUnitRange(double phase, double expected)
    {
        Assert.Equal(expected, Metronome.NormalizePhase(phase), 10);
    }

    [Theory]
    [InlineData(0.25, 0.25)]
    [InlineData(0.5, 0.5)]
    [InlineData(0.75, -0.25)]
    [InlineData(-0.75, 0.25)]
    [InlineData(-0.5, -0.5)]
    [InlineData(0.0, 0.0)]
    public void FindClosestDeltaMinimizesMovement(double delta, double expected)
    {
        Assert.Equal(expected, Metronome.FindClosestDelta(delta), 10);
    }

    [Theory]
    [InlineData(0, 500.0, 1)]     // At the origin we are in marker 1.
    [InlineData(499, 500.0, 1)]   // Just before the second marker.
    [InlineData(500, 500.0, 2)]   // Exactly at the second marker.
    [InlineData(1250, 500.0, 3)]
    [InlineData(-1, 500.0, 0)]    // Markers before the origin count down from zero.
    public void MarkerNumberCountsFromOne(long instant, double interval, long expected)
    {
        Assert.Equal(expected, Metronome.MarkerNumber(instant, 0, interval));
    }

    [Theory]
    [InlineData(0, 500.0, 0.0)]
    [InlineData(250, 500.0, 0.5)]
    [InlineData(499, 500.0, 0.998)]
    [InlineData(500, 500.0, 0.0)]  // Phase wraps at the start of the next marker.
    [InlineData(1250, 500.0, 0.5)]
    [InlineData(-250, 500.0, 0.5)] // Phase also wraps before the origin.
    public void MarkerPhaseStaysInUnitRange(long instant, double interval, double expected)
    {
        Assert.Equal(expected, Metronome.MarkerPhase(instant, 0, interval), 10);
    }

    [Theory]
    [InlineData(1, 0.5, 1, 1, 0.5)]   // Ratio 1/1 leaves the phase unchanged.
    [InlineData(1, 0.5, 1, 2, 0.0)]   // Twice as fast: half a marker is a whole cycle.
    [InlineData(1, 0.25, 1, 2, 0.5)]
    [InlineData(1, 0.25, 2, 1, 0.125)] // Half as fast: a cycle spans two markers.
    [InlineData(2, 0.25, 2, 1, 0.125)] // The whole-marker part of the base phase cancels out.
    [InlineData(2, 0.5, 3, 4, 2.0 / 3.0)] // 4 cycles every 3 markers.
    public void EnhancedPhaseWithExplicitFraction(long markerNumber, double markerPhase, long numerator, long denominator, double expected)
    {
        Assert.Equal(expected, Metronome.EnhancedPhase(markerNumber, markerPhase, numerator, denominator), 10);
    }

    [Theory]
    [InlineData(1, 0.5, 1.0, 0.5)]
    [InlineData(1, 0.5, 0.5, 0.0)]
    [InlineData(1, 0.25, 2.0, 0.125)]
    [InlineData(2, 0.5, 0.75, 2.0 / 3.0)] // 3/4 recognized from its double representation.
    public void EnhancedPhaseWithDoubleRatioMatchesFraction(long markerNumber, double markerPhase, double ratio, double expected)
    {
        Assert.Equal(expected, Metronome.EnhancedPhase(markerNumber, markerPhase, ratio), 10);
    }

    [Fact]
    public void AdjustStartShiftsTheTimeline()
    {
        var metronome = new Metronome();
        var start = metronome.StartTime;
        metronome.AdjustStart(-1500);
        Assert.Equal(start - 1500, metronome.StartTime);

        // Moving the start 1500ms earlier at 120 BPM puts us three beats further along.
        var snapshot = metronome.GetSnapshot(start);
        Assert.Equal(4, snapshot.Beat);
    }

    [Fact]
    public void ChangingBeatsPerBarChangesBarInterval()
    {
        var metronome = new Metronome();
        metronome.BeatsPerBar = 3;
        Assert.Equal(3, metronome.BeatsPerBar);
        Assert.Equal(1500.0, metronome.BarInterval, 10);
        Assert.Equal(12000.0, metronome.PhraseInterval, 10);

        metronome.BarsPerPhrase = 2;
        Assert.Equal(2, metronome.BarsPerPhrase);
        Assert.Equal(3000.0, metronome.PhraseInterval, 10);
    }

    [Fact]
    public void InvalidConfigurationValuesAreRejected()
    {
        var metronome = new Metronome();
        Assert.Throws<ArgumentException>(() => metronome.BeatsPerBar = 0);
        Assert.Throws<ArgumentException>(() => metronome.BeatsPerBar = -1);
        Assert.Throws<ArgumentException>(() => metronome.BarsPerPhrase = 0);
        Assert.Throws<ArgumentException>(() => metronome.BarsPerPhrase = -3);
    }

    [Fact]
    public void CopyConstructorSharesConfigurationButIsIndependent()
    {
        var original = new Metronome
        {
            BeatsPerBar = 3,
            BarsPerPhrase = 5,
        };
        original.Tempo = 90.0;

        var copy = new Metronome(original);
        Assert.Equal(original.StartTime, copy.StartTime);
        Assert.Equal(original.Tempo, copy.Tempo, 10);
        Assert.Equal(original.BeatsPerBar, copy.BeatsPerBar);
        Assert.Equal(original.BarsPerPhrase, copy.BarsPerPhrase);

        copy.AdjustStart(1000);
        Assert.Equal(original.StartTime + 1000, copy.StartTime);
    }

    [Fact]
    public void GetSnapshotWithoutTimestampUsesCurrentTime()
    {
        var metronome = new Metronome();
        var before = metronome.GetInstant();
        var snapshot = metronome.GetSnapshot();
        var after = metronome.GetInstant();
        Assert.InRange(snapshot.Instant, before, after);
    }
}
