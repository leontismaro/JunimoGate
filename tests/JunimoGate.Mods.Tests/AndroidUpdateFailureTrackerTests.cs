using JunimoGate.Tests;
using StardewModdingAPI.Mobile;

internal static class AndroidUpdateFailureTrackerTests
{
    public static void LogsOnlyTheFirstFailureAndOneSuppressionNotice()
    {
        var tracker = new AndroidUpdateFailureTracker();

        var first = tracker.RecordFailure();
        var second = tracker.RecordFailure();
        var third = tracker.RecordFailure();

        TestHarness.Equal(1, first.ConsecutiveFailures);
        TestHarness.True(first.ShouldLogDetails);
        TestHarness.False(first.ShouldLogSuppressionNotice);
        TestHarness.Equal(2, second.ConsecutiveFailures);
        TestHarness.False(second.ShouldLogDetails);
        TestHarness.True(second.ShouldLogSuppressionNotice);
        TestHarness.Equal(3, third.ConsecutiveFailures);
        TestHarness.False(third.ShouldLogDetails);
        TestHarness.False(third.ShouldLogSuppressionNotice);
        TestHarness.False(third.ShouldTerminate);
    }

    public static void SuccessfulUpdateResetsTheFailureSequence()
    {
        var tracker = new AndroidUpdateFailureTracker();
        tracker.RecordFailure();
        tracker.RecordFailure();

        TestHarness.Equal(2, tracker.Reset());
        TestHarness.Equal(0, tracker.Reset());
        TestHarness.True(tracker.RecordFailure().ShouldLogDetails);
    }

    public static void TerminatesAfterTheRecoveryBudget()
    {
        var tracker = new AndroidUpdateFailureTracker(maxRecoverableFailures: 2);

        TestHarness.False(tracker.RecordFailure().ShouldTerminate);
        TestHarness.False(tracker.RecordFailure().ShouldTerminate);
        var terminal = tracker.RecordFailure();

        TestHarness.True(terminal.ShouldTerminate);
        TestHarness.Equal(3, terminal.ConsecutiveFailures);
    }
}
