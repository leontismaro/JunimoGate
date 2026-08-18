using JunimoGate.Tests;
using StardewModdingAPI.Mobile;

internal static class AndroidDrawGatedTaskQueueTests
{
    public static void RunsAtMostOneTaskUntilDrawReleasesTheGate()
    {
        var queue = new AndroidDrawGatedTaskQueue();
        var order = new List<int>();
        Task first = queue.Enqueue(() => order.Add(1));
        Task second = queue.Enqueue(() => order.Add(2));

        var firstPump = queue.Pump(TimeSpan.FromSeconds(1));
        var blockedPump = queue.Pump(TimeSpan.FromSeconds(1));

        TestHarness.Equal(1, firstPump.ExecutedItems);
        TestHarness.Equal(0, blockedPump.ExecutedItems);
        TestHarness.True(blockedPump.HasPending);
        TestHarness.Equal(1, order.Count);
        TestHarness.True(first.IsCompletedSuccessfully);
        TestHarness.False(second.IsCompleted);

        queue.ReleaseAfterDraw();
        var secondPump = queue.Pump(TimeSpan.FromSeconds(1));

        TestHarness.Equal(1, secondPump.ExecutedItems);
        TestHarness.False(secondPump.HasPending);
        TestHarness.Equal(2, order.Count);
        TestHarness.Equal(2, order[1]);
        TestHarness.True(second.IsCompletedSuccessfully);
    }

    public static void ResetFaultsPendingWorkAndReleasesTheGate()
    {
        var queue = new AndroidDrawGatedTaskQueue();
        Task first = queue.Enqueue(() => { });
        Task pending = queue.Enqueue(() => { });
        queue.Pump(TimeSpan.FromSeconds(1));

        queue.Reset(new InvalidOperationException("session failed"));
        Task nextSession = queue.Enqueue(() => { });
        var nextPump = queue.Pump(TimeSpan.FromSeconds(1));

        TestHarness.True(first.IsCompletedSuccessfully);
        TestHarness.True(pending.IsFaulted);
        TestHarness.Throws<InvalidOperationException>(() => pending.GetAwaiter().GetResult());
        TestHarness.Equal(1, nextPump.ExecutedItems);
        TestHarness.True(nextSession.IsCompletedSuccessfully);
    }
}
