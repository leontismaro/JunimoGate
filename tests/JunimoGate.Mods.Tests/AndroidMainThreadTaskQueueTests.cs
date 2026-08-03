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
        var firstPump = queue.Pump(TimeSpan.FromSeconds(1), maxItems: 1);
        TestHarness.Equal(1, firstPump.ExecutedItems);
        TestHarness.True(firstPump.HasPending);
        TestHarness.Equal(1, order.Count);
        TestHarness.Equal(1, order[0]);
        TestHarness.True(first.IsCompletedSuccessfully);
        TestHarness.False(second.IsCompleted);

        var secondPump = queue.Pump(TimeSpan.FromSeconds(1), maxItems: 1);
        TestHarness.Equal(1, secondPump.ExecutedItems);
        TestHarness.False(secondPump.HasPending);
        TestHarness.Equal(2, order.Count);
        TestHarness.Equal(2, order[1]);
        TestHarness.True(second.IsCompletedSuccessfully);
        TestHarness.Equal(0, queue.Pump(TimeSpan.FromSeconds(1), maxItems: 1).ExecutedItems);
    }

    public static void PreservesTaskFailureForTheWaitingProducer()
    {
        var queue = new AndroidMainThreadTaskQueue();
        var task = queue.Enqueue(() => throw new InvalidOperationException("expected"));

        TestHarness.Equal(1, queue.Pump(TimeSpan.FromSeconds(1), maxItems: 1).ExecutedItems);
        TestHarness.True(task.IsFaulted);
        TestHarness.Throws<InvalidOperationException>(() => task.GetAwaiter().GetResult());
    }

    public static void ExecutesInlineWhenAlreadyOnTheGameThread()
    {
        var queue = new AndroidMainThreadTaskQueue();
        queue.Pump(TimeSpan.FromSeconds(1), maxItems: 1);
        int calls = 0;

        Task task = queue.Enqueue(() => calls++);

        TestHarness.Equal(1, calls);
        TestHarness.True(task.IsCompletedSuccessfully);
    }

    public static void ResetFaultsPendingProducers()
    {
        var queue = new AndroidMainThreadTaskQueue();
        Task task = queue.Enqueue(() => { });

        queue.Reset(new InvalidOperationException("session failed"));

        TestHarness.True(task.IsFaulted);
        TestHarness.Throws<InvalidOperationException>(() => task.GetAwaiter().GetResult());
    }
}
