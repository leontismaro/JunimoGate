using JunimoGate.Tests;
using StardewModdingAPI.Mobile;

internal static class AndroidBackgroundTaskTrackerTests
{
    public static void TracksBlockedWorkUntilCompletion()
    {
        var tracker = new AndroidBackgroundTaskTracker();
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var task = tracker.Start(() =>
        {
            started.Set();
            release.Wait();
        });

        TestHarness.True(started.Wait(TimeSpan.FromSeconds(5)), "The background task did not start.");
        TestHarness.True(tracker.HasPending);
        TestHarness.Equal(1, tracker.PendingCount);
        release.Set();
        TestHarness.True(task.Wait(TimeSpan.FromSeconds(5)), "The background task did not complete.");
        TestHarness.False(tracker.HasPending);
        TestHarness.Equal(0, tracker.PendingCount);
    }

    public static void ReleasesFailedWork()
    {
        var tracker = new AndroidBackgroundTaskTracker();
        var task = tracker.Start(() => throw new InvalidOperationException("expected"));

        TestHarness.Throws<AggregateException>(() => task.Wait(TimeSpan.FromSeconds(5)));
        TestHarness.False(tracker.HasPending);
        TestHarness.Equal(0, tracker.PendingCount);
    }
}
