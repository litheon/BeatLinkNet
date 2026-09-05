using BeatLink.Electro;
using Microsoft.Extensions.Logging;

namespace BeatLink;

/// <summary>
/// Manages the thread that sends beat packets when the <see cref="VirtualCdj"/> is sending status
/// and playing. The thread sleeps until shortly before the next beat is due, then busy-waits on
/// the monotonic clock so the beat packet can be sent at an accurate time. Ported from the
/// beat-link <c>BeatSender</c> class by James Elliott.
/// </summary>
internal sealed class BeatSender
{
    private static readonly ILogger Logger = BeatLinkLogging.CreateLogger<BeatSender>();

    /// <summary>
    /// How far into a beat can we find ourselves, in milliseconds, and still consider it timely
    /// to send the beat packet (assuming we haven't already sent one for this beat).
    /// </summary>
    public const long BeatThreshold = 10;

    /// <summary>
    /// If we are this close to the next beat, in milliseconds, we will busy-wait rather than
    /// sleeping, so we can send it at a more accurate time. We will also aim our sleep to land
    /// this far before the next beat, to try and absorb threading jitter.
    /// </summary>
    public const int SleepThreshold = 5;

    /// <summary>
    /// Becomes <c>false</c> when we are told to shut down, so that the thread knows to stop.
    /// </summary>
    private volatile bool _running = true;

    /// <summary>
    /// Holds the thread that runs the beat-sending loop, so we can interrupt it when the
    /// metronome configuration changes, or when it is time to shut down.
    /// </summary>
    private readonly Thread _thread;

    /// <summary>
    /// The metronome which determines when beats need to be sent.
    /// </summary>
    private readonly Metronome _metronome;

    /// <summary>
    /// Sends a beat for the supplied snapshot, returning the beat number that was sent. Normally
    /// <see cref="VirtualCdj.SendBeat(Snapshot)"/>; injectable so timing behavior can be tested
    /// without any sockets.
    /// </summary>
    private readonly Func<Snapshot, long> _sendBeat;

    /// <summary>
    /// Incremented whenever the timeline changes or we are shut down, so the busy-wait loop can
    /// notice that it must re-evaluate its situation. (Java relies on <c>Thread.interrupted()</c>
    /// for this, but .NET offers no non-throwing way to observe the interrupted state, so the
    /// interrupt only wakes the sleeping phase and this counter wakes the spinning phase.)
    /// </summary>
    private int _wakeSignal;

    /// <summary>
    /// Create and start the beat sending thread. The thread runs slightly above normal priority
    /// (like Java's <c>NORM_PRIORITY + 1</c>) since beat timing matters.
    /// </summary>
    /// <param name="metronome">determines when beats need to be sent</param>
    /// <param name="sendBeat">how a beat should be sent; defaults to
    /// <see cref="VirtualCdj.SendBeat(Snapshot)"/> on the singleton instance</param>
    internal BeatSender(Metronome metronome, Func<Snapshot, long>? sendBeat = null)
    {
        _metronome = metronome;
        _sendBeat = sendBeat ?? (snapshot => VirtualCdj.Instance.SendBeat(snapshot));
        _thread = new Thread(BeatLoop)
        {
            Name = "beat-link VirtualCdj beat sender",
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal
        };
        _thread.Start();
    }

    /// <summary>
    /// Must be called whenever the tempo changes or our position on the timeline is nudged so the
    /// loop can recompute when the next beat is due and react appropriately.
    /// </summary>
    internal void TimelineChanged()
    {
        if (!_running)
        {
            Logger.LogWarning("BeatSender ignoring TimelineChanged() call because it has been shut down.");
            return;
        }
        Interlocked.Increment(ref _wakeSignal);
        _thread.Interrupt();
    }

    /// <summary>
    /// Tells the beat sending thread to shut down. The <see cref="BeatSender"/> can no longer be
    /// used once this is called.
    /// </summary>
    internal void ShutDown()
    {
        _running = false;
        Interlocked.Increment(ref _wakeSignal);
        _thread.Interrupt();
    }

    /// <summary>
    /// The body of the beat-sending thread: sends a beat packet whenever the metronome timeline
    /// crosses a beat boundary, sleeping until <see cref="SleepThreshold"/> milliseconds before
    /// the next beat is due and then busy-waiting on the monotonic clock for accuracy.
    /// </summary>
    private void BeatLoop()
    {
        long? lastBeatSent = null;
        while (_running)
        {
            var snapshot = _metronome.GetSnapshot();

            if (lastBeatSent != null && (snapshot.BeatPhase > 0.5 || snapshot.Beat != lastBeatSent.Value))
            {
                lastBeatSent = null;  // We are no longer at the start of the same beat, so are armed to send.
            }

            // Determine when the current and next beats belong.
            var currentBeatDue = snapshot.GetTimeOfBeat(snapshot.Beat);
            var nextBeatDue = snapshot.GetTimeOfBeat(snapshot.Beat + 1);

            // Is it time to send a beat?
            var distanceIntoCurrentBeat = snapshot.Instant - currentBeatDue;
            if (distanceIntoCurrentBeat < BeatThreshold &&
                (lastBeatSent == null || lastBeatSent.Value != snapshot.Beat))
            {
                lastBeatSent = _sendBeat(snapshot);
            }

            var sleepMilliseconds = nextBeatDue - Util.CurrentTimestampNanos() / 1_000_000;
            if (sleepMilliseconds > SleepThreshold)
            {
                // Long enough to try actually sleeping until we are closer to being due.
                try
                {
                    Thread.Sleep((int)(sleepMilliseconds - SleepThreshold));
                }
                catch (ThreadInterruptedException)
                {
                    Logger.LogDebug("BeatSender thread interrupted, re-evaluating time until next beat.");
                }
            }
            else
            {
                // Close enough to busy-wait until it is time to send the beat.
                var observedSignal = Volatile.Read(ref _wakeSignal);
                var targetTime = Util.CurrentTimestampNanos() + sleepMilliseconds * 1_000_000;
                while (Volatile.Read(ref _wakeSignal) == observedSignal && Util.CurrentTimestampNanos() < targetTime)
                {
                    // Busy-waiting, woo-hoo, hope you didn't need this core!
                }
            }
        }
    }
}
