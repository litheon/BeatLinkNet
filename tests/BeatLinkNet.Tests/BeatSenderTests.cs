using System.Collections.Concurrent;
using BeatLink.Electro;

namespace BeatLink.Tests;

/// <summary>
/// Timing self-tests for the <see cref="BeatSender"/> thread, using an injected send callback so
/// no sockets (and no <see cref="VirtualCdj"/> state) are involved. The tolerances are generous
/// so the tests remain reliable on loaded CI machines; the point is to verify scheduling
/// behavior, not to benchmark.
/// </summary>
[Collection("Network services")]
public sealed class BeatSenderTests
{
    private sealed record RecordedBeat(long Beat, long ErrorMs, long WallMs);

    /// <summary>
    /// Runs a <see cref="BeatSender"/> against the supplied metronome for the given duration,
    /// recording each simulated beat's number, its distance from the moment the metronome said
    /// the beat was due, and the monotonic time at which it was sent.
    /// </summary>
    private static List<RecordedBeat> RecordBeats(Metronome metronome, int runMillis, Action<BeatSender>? midRun = null)
    {
        var recorded = new ConcurrentQueue<RecordedBeat>();
        var sender = new BeatSender(metronome, snapshot =>
        {
            var now = Util.CurrentTimestampNanos() / 1_000_000;
            recorded.Enqueue(new RecordedBeat(snapshot.Beat, now - snapshot.GetTimeOfBeat(snapshot.Beat), now));
            return snapshot.Beat;
        });
        try
        {
            if (midRun == null)
            {
                Thread.Sleep(runMillis);
            }
            else
            {
                Thread.Sleep(runMillis / 2);
                midRun(sender);
                Thread.Sleep(runMillis - runMillis / 2);
            }
        }
        finally
        {
            sender.ShutDown();
        }
        return [.. recorded];
    }

    [Fact]
    public void SendsBeatsCloseToTheirScheduledTimes()
    {
        var metronome = new Metronome { Tempo = 120.0 };  // 500 ms beats.
        metronome.JumpToBeat(1);
        var beats = RecordBeats(metronome, 1800);

        // At 120 BPM over 1.8 seconds we expect beats 1 through ~4; allow one miss for CI noise.
        Assert.True(beats.Count >= 3, $"Expected at least 3 beats, saw {beats.Count}");

        // Each beat is sent only once and beats arrive in order.
        var numbers = beats.Select(b => b.Beat).ToList();
        Assert.Equal(numbers.Distinct(), numbers);
        Assert.Equal(numbers.OrderBy(n => n), numbers);

        // Each beat was sent within a generous window around its scheduled time.
        foreach (var beat in beats)
        {
            Assert.InRange(beat.ErrorMs, -30, 30);
        }
    }

    [Fact]
    public void TimelineChangeReschedulesBeats()
    {
        var metronome = new Metronome { Tempo = 60.0 };  // 1000 ms beats.
        metronome.JumpToBeat(1);

        // After ~425 ms the sender is asleep waiting for beat 2 (due at ~1000 ms). Quadrupling
        // the tempo moves the upcoming beats much closer; without the TimelineChanged interrupt
        // the sender would sleep through them until ~995 ms, which is after this test stops it.
        var beats = RecordBeats(metronome, 850, sender =>
        {
            metronome.Tempo = 240.0;  // 250 ms beats; the current beat and phase are preserved.
            sender.TimelineChanged();
        });

        Assert.True(beats.Count >= 2,
            $"Expected the timeline change to wake the sender for extra beats, saw {beats.Count}");
        Assert.True(beats[^1].Beat >= 2, "Expected beats after the tempo change to be sent");
    }

    [Fact]
    public void ShutDownStopsSendingAndDisablesWakeups()
    {
        var metronome = new Metronome { Tempo = 120.0 };
        metronome.JumpToBeat(1);
        var count = 0;
        var sender = new BeatSender(metronome, snapshot =>
        {
            Interlocked.Increment(ref count);
            return snapshot.Beat;
        });
        Thread.Sleep(100);
        sender.ShutDown();
        Thread.Sleep(50);
        var after = Volatile.Read(ref count);

        // No further beats arrive once shut down, and a late TimelineChanged is tolerated.
        Thread.Sleep(600);
        Assert.Equal(after, Volatile.Read(ref count));
        sender.TimelineChanged();  // Logs a warning; must not throw or restart anything.
        Thread.Sleep(100);
        Assert.Equal(after, Volatile.Read(ref count));
    }
}
