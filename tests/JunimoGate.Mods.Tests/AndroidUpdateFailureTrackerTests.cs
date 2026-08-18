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
}
