using Microsoft.Extensions.Logging.Abstractions;

namespace BeatLink.Tests;

public class EventDispatcherTests
{
    [Fact]
    public async Task DeliversOnThreadPoolWhenNoContext()
    {
        EventDispatcher.Context = null;
        var completion = new TaskCompletionSource<int>();
        EventHandler<int> handler = (_, value) => completion.TrySetResult(value);
        EventDispatcher.Deliver(handler, this, 42, NullLogger.Instance);
        Assert.Equal(42, await completion.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task DeliversThroughAssignedContext()
    {
        var recorded = new RecordingSynchronizationContext();
        EventDispatcher.Context = recorded;
        try
        {
            var completion = new TaskCompletionSource();
            EventHandler<int> handler = (_, _) => completion.TrySetResult();
            EventDispatcher.Deliver(handler, this, 1, NullLogger.Instance);
            await completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(recorded.PostCount > 0);
        }
        finally
        {
            EventDispatcher.Context = null;
        }
    }

    [Fact]
    public void OneFailingHandlerDoesNotBlockOthers()
    {
        var delivered = false;
        EventHandler<int> handlers = (_, _) => throw new InvalidOperationException("boom");
        handlers += (_, _) => delivered = true;
        EventDispatcher.DeliverNow(handlers, this, 7, NullLogger.Instance);
        Assert.True(delivered);
    }

    private sealed class RecordingSynchronizationContext : SynchronizationContext
    {
        public int PostCount;

        public override void Post(SendOrPostCallback d, object? state)
        {
            Interlocked.Increment(ref PostCount);
            ThreadPool.QueueUserWorkItem(_ => d(state));
        }
    }
}
