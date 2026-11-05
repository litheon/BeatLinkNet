using Microsoft.Extensions.Logging;

namespace BeatLink;

/// <summary>
/// Provides the abstract skeleton for all the classes that can be started and stopped in Beat Link,
/// and for which other classes may want to be informed when they start or stop.
/// </summary>
public abstract class LifecycleParticipant
{
    /// <summary>
    /// Raised when this component starts. Handlers are invoked asynchronously (on thread-pool
    /// threads) so that a handler which calls back into the component cannot deadlock against
    /// the start operation that is announcing the event.
    /// </summary>
    public event EventHandler? Started;

    /// <summary>
    /// Raised when this component stops. Handlers are invoked asynchronously (on thread-pool
    /// threads) so that a handler which calls back into the component cannot deadlock against
    /// the stop operation that is announcing the event.
    /// </summary>
    public event EventHandler? Stopped;

    /// <summary>
    /// Check whether this component is currently running.
    /// </summary>
    public abstract bool IsRunning { get; }

    /// <summary>
    /// Announce the start or stop of this component to registered lifecycle handlers. Each handler
    /// is invoked on its own thread-pool work item, isolated from the others, mirroring the fresh
    /// delivery threads used by the Java implementation to avoid deadlocks.
    /// </summary>
    /// <param name="logger">the logger to which delivery problems should be reported</param>
    /// <param name="starting"><c>true</c> if the component is starting, <c>false</c> if it is stopping</param>
    protected void DeliverLifecycleAnnouncement(ILogger logger, bool starting)
    {
        var handlers = starting ? Started : Stopped;
        if (handlers == null)
        {
            return;
        }
        foreach (var handler in handlers.GetInvocationList())
        {
            var target = (EventHandler)handler;
            Interlocked.Increment(ref _pendingAnnouncements);
            Task.Run(() =>
            {
                try
                {
                    target(this, EventArgs.Empty);
                }
                catch (Exception e)
                {
                    logger.LogWarning(e, "Problem delivering lifecycle announcement (starting={Starting}) to handler", starting);
                }
                finally
                {
                    Interlocked.Decrement(ref _pendingAnnouncements);
                }
            });
        }
    }

    /// <summary>
    /// Counts lifecycle announcement handlers that have been queued but have not finished
    /// running, across all participants.
    /// </summary>
    private static int _pendingAnnouncements;

    /// <summary>
    /// Test seam: blocks until every queued lifecycle announcement handler (across all
    /// participants) has finished running, so tests that stop services and immediately
    /// reconfigure singletons cannot race against stale asynchronous handlers.
    /// </summary>
    /// <param name="timeout">the longest to wait before giving up</param>
    /// <returns><c>true</c> if the announcements drained, <c>false</c> on timeout</returns>
    internal static bool WaitForPendingAnnouncements(TimeSpan timeout)
    {
        var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        while (Volatile.Read(ref _pendingAnnouncements) > 0)
        {
            if (Environment.TickCount64 > deadline)
            {
                return false;
            }
            Thread.Sleep(5);
        }
        return true;
    }

    /// <summary>
    /// Helper method to ensure this component is active, throwing an exception if it is not.
    /// </summary>
    /// <exception cref="InvalidOperationException">if the component is not running</exception>
    protected void EnsureRunning()
    {
        if (!IsRunning)
        {
            throw new InvalidOperationException($"{GetType().Name} is not running");
        }
    }
}
