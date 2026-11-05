using Microsoft.Extensions.Logging;

namespace BeatLink;

/// <summary>
/// Controls the delivery context for the Beat Link Net events that the original Java library
/// delivered on the Swing event dispatch thread (device found/lost announcements and all of the
/// metadata-related events in <c>BeatLink.Data</c>). When <see cref="Context"/> is <c>null</c>
/// (the default), such events are delivered on thread-pool threads; a UI application can assign
/// its framework's <see cref="SynchronizationContext"/> to have them delivered on the UI thread.
/// <para>
/// Time-sensitive events (beats, status updates, precise positions) are always delivered
/// synchronously on the network receive thread and are unaffected by this setting.
/// </para>
/// </summary>
public static class EventDispatcher
{
    /// <summary>
    /// The synchronization context to which UI-friendly events are posted, or <c>null</c> to
    /// deliver them on thread-pool threads.
    /// </summary>
    public static SynchronizationContext? Context { get; set; }

    /// <summary>
    /// Runs an action in the configured delivery context: posted to <see cref="Context"/> when one
    /// has been assigned, otherwise queued to the thread pool.
    /// </summary>
    /// <param name="action">the action to run</param>
    internal static void Post(Action action)
    {
        var context = Context;
        if (context != null)
        {
            context.Post(static state => ((Action)state!)(), action);
        }
        else
        {
            Task.Run(action);
        }
    }

    /// <summary>
    /// Delivers an event to every subscribed handler in the configured delivery context, isolating
    /// each handler so that an exception thrown by one cannot prevent delivery to the others (the
    /// same guarantee the Java library made for its listener sets).
    /// </summary>
    /// <typeparam name="TArgs">the event payload type</typeparam>
    /// <param name="handlers">the multicast delegate holding the subscribed handlers, may be null</param>
    /// <param name="sender">the object raising the event</param>
    /// <param name="args">the event payload</param>
    /// <param name="logger">used to report handlers that throw</param>
    internal static void Deliver<TArgs>(EventHandler<TArgs>? handlers, object sender, TArgs args, ILogger logger)
    {
        if (handlers == null)
        {
            return;
        }
        Post(() => DeliverNow(handlers, sender, args, logger));
    }

    /// <summary>
    /// Delivers an event to every subscribed handler synchronously on the calling thread, isolating
    /// each handler so that an exception thrown by one cannot prevent delivery to the others. Used
    /// for the time-sensitive events that must be delivered on the network receive thread.
    /// </summary>
    /// <typeparam name="TArgs">the event payload type</typeparam>
    /// <param name="handlers">the multicast delegate holding the subscribed handlers, may be null</param>
    /// <param name="sender">the object raising the event</param>
    /// <param name="args">the event payload</param>
    /// <param name="logger">used to report handlers that throw</param>
    internal static void DeliverNow<TArgs>(EventHandler<TArgs>? handlers, object sender, TArgs args, ILogger logger)
    {
        if (handlers == null)
        {
            return;
        }
        foreach (var handler in handlers.GetInvocationList())
        {
            try
            {
                ((EventHandler<TArgs>)handler)(sender, args);
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "Problem delivering {EventType} event to handler", typeof(TArgs).Name);
            }
        }
    }
}
