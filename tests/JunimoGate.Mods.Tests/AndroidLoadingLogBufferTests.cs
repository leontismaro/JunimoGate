using JunimoGate.Tests;
using StardewModdingAPI.Internal.ConsoleWriting;
using StardewModdingAPI.Mobile;

internal static class AndroidLoadingLogBufferTests
{
    public static void KeepsNewestLinesInDisplayOrder()
    {
        var buffer = new AndroidLoadingLogBuffer(3);
        buffer.Append(ConsoleLogLevel.Info, "one");
        buffer.Append(ConsoleLogLevel.Warn, "two");
        buffer.Append(ConsoleLogLevel.Error, "three");
        buffer.Append(ConsoleLogLevel.Alert, "four");

        var lines = buffer.SnapshotNewestFirst(10);
        TestHarness.Equal(3, lines.Length);
        TestHarness.Equal(new AndroidLoadingLogLine(ConsoleLogLevel.Alert, "four"), lines[0]);
        TestHarness.Equal(new AndroidLoadingLogLine(ConsoleLogLevel.Error, "three"), lines[1]);
        TestHarness.Equal(new AndroidLoadingLogLine(ConsoleLogLevel.Warn, "two"), lines[2]);
    }

    public static void SplitsPlatformNewlinesWithoutLosingEmptyLines()
    {
        var buffer = new AndroidLoadingLogBuffer(8);
        buffer.Append(ConsoleLogLevel.Debug, "one\r\ntwo\n\rthree\r");

        var lines = buffer.SnapshotNewestFirst(8);
        TestHarness.Equal(5, lines.Length);
        TestHarness.Equal("", lines[0].Text);
        TestHarness.Equal("three", lines[1].Text);
        TestHarness.Equal("", lines[2].Text);
        TestHarness.Equal("two", lines[3].Text);
        TestHarness.Equal("one", lines[4].Text);
        TestHarness.Equal(ConsoleLogLevel.Debug, lines[0].Level);
    }

    public static void LimitsSnapshotsAndClearsState()
    {
        var buffer = new AndroidLoadingLogBuffer(4);
        buffer.Append(ConsoleLogLevel.Info, "one\ntwo\nthree");

        var lines = buffer.SnapshotNewestFirst(2);
        TestHarness.Equal(2, lines.Length);
        TestHarness.Equal("three", lines[0].Text);
        TestHarness.Equal("two", lines[1].Text);
        TestHarness.Equal(3, buffer.Count);

        buffer.Clear();
        TestHarness.Equal(0, buffer.Count);
        TestHarness.Equal(0, buffer.SnapshotNewestFirst(4).Length);
    }
}
