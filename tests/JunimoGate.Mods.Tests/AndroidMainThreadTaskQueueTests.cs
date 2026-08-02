using JunimoGate.Tests;
using StardewModdingAPI.Mobile;

internal static class AndroidMainThreadTaskQueueTests
{
    public static void RunsOneQueuedTaskPerPumpInFifoOrder()
    {
        var queue = new AndroidMainThreadTaskQueue();
        var order = new List<int>();
        var first = queue.Enqueue(() => order.Add(1));
        var second = queue.Enqueue(() => order.Add(2));

        TestHarness.Equal(0, order.Count);
        TestHarness.True(queue.TryRunNext());
        TestHarness.Equal(1, order.Count);
        TestHarness.Equal(1, order[0]);
        TestHarness.True(first.IsCompletedSuccessfully);
        TestHarness.False(second.IsCompleted);

        TestHarness.True(queue.TryRunNext());
        TestHarness.Equal(2, order.Count);
        TestHarness.Equal(2, order[1]);
        TestHarness.True(second.IsCompletedSuccessfully);
        TestHarness.False(queue.TryRunNext());
    }

    public static void PreservesTaskFailureForTheWaitingProducer()
    {
        var queue = new AndroidMainThreadTaskQueue();
        var task = queue.Enqueue(() => throw new InvalidOperationException("expected"));

        TestHarness.True(queue.TryRunNext());
        TestHarness.True(task.IsFaulted);
        TestHarness.Throws<AggregateException>(() => task.Wait());
        TestHarness.False(queue.TryRunNext());
    }
}
