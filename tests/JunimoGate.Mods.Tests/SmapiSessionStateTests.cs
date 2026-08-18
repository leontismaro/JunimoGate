using JunimoGate.GameHost;
using JunimoGate.Tests;

internal static class SmapiSessionStateTests
{
    public static void PreservesTheFirstStartupFailure()
    {
        using var state = new SmapiSessionState();
        var firstFailure = new SmapiSessionFailure("specific_failure", "specific", null);
        var first = state.Apply(SmapiSessionStatus.Failed, firstFailure);
        var duplicate = state.Apply(
            SmapiSessionStatus.Failed,
            new SmapiSessionFailure("session_start_failed", "generic", null));
        var lateRunning = state.Apply(SmapiSessionStatus.Running);
        var startup = state.StartupCompletion.GetAwaiter().GetResult();

        TestHarness.True(first.Accepted);
        TestHarness.False(duplicate.Accepted);
        TestHarness.False(lateRunning.Accepted);
        TestHarness.Equal(SmapiSessionStatus.Failed, startup.Status);
        TestHarness.Equal("specific_failure", startup.Failure?.Code);
        TestHarness.Equal("specific_failure", state.Snapshot.Failure?.Code);
    }

    public static void AcceptsOneFailureAfterRunning()
    {
        using var state = new SmapiSessionState();
        var running = state.Apply(SmapiSessionStatus.Running);
        var startup = state.StartupCompletion.GetAwaiter().GetResult();
        var failure = state.Apply(
            SmapiSessionStatus.Failed,
            new SmapiSessionFailure("game_update_failed", "update", null));
        var duplicate = state.Apply(
            SmapiSessionStatus.Failed,
            new SmapiSessionFailure("game_loop_failed", "duplicate", null));

        TestHarness.True(running.Accepted);
        TestHarness.Equal(SmapiSessionStatus.Running, startup.Status);
        TestHarness.True(failure.Accepted);
        TestHarness.Equal(SmapiSessionStatus.Running, failure.PreviousStatus);
        TestHarness.Equal("game_update_failed", failure.Snapshot.Failure?.Code);
        TestHarness.False(duplicate.Accepted);
        TestHarness.Equal("game_update_failed", state.Snapshot.Failure?.Code);
    }

    public static void RejectsLateTransitionsAfterDisposal()
    {
        var state = new SmapiSessionState();
        state.Dispose();
        var startup = state.StartupCompletion.GetAwaiter().GetResult();
        var running = state.Apply(SmapiSessionStatus.Running);
        var failure = state.Apply(
            SmapiSessionStatus.Failed,
            new SmapiSessionFailure("late_failure", "late", null));

        TestHarness.Equal(SmapiSessionStatus.Disposed, startup.Status);
        TestHarness.False(running.Accepted);
        TestHarness.False(failure.Accepted);
        TestHarness.Equal(SmapiSessionStatus.Disposed, state.Snapshot.Status);
    }

    public static void SerializesConcurrentRunningAndFailure()
    {
        for (int iteration = 0; iteration < 100; iteration++)
        {
            using var state = new SmapiSessionState();
            using var start = new Barrier(2);
            Task<SmapiSessionStateChange> runningTask = Task.Run(() =>
            {
                start.SignalAndWait();
                return state.Apply(SmapiSessionStatus.Running);
            });
            Task<SmapiSessionStateChange> failureTask = Task.Run(() =>
            {
                start.SignalAndWait();
                return state.Apply(
                    SmapiSessionStatus.Failed,
                    new SmapiSessionFailure("concurrent_failure", "failure", null));
            });

            Task.WaitAll(runningTask, failureTask);
            SmapiSessionSnapshot startup = state.StartupCompletion.GetAwaiter().GetResult();
            SmapiSessionSnapshot final = state.Snapshot;

            TestHarness.True(failureTask.Result.Accepted);
            TestHarness.Equal(SmapiSessionStatus.Failed, final.Status);
            TestHarness.Equal("concurrent_failure", final.Failure?.Code);
            if (startup.Status == SmapiSessionStatus.Failed)
                TestHarness.False(runningTask.Result.Accepted);
            else
            {
                TestHarness.Equal(SmapiSessionStatus.Running, startup.Status);
                TestHarness.True(runningTask.Result.Accepted);
                TestHarness.Equal(SmapiSessionStatus.Running, failureTask.Result.PreviousStatus);
            }
        }
    }
}
