using JunimoGate.Tests;
using StardewModdingAPI.Mobile;

internal static class AndroidWorldEntryGateTests
{
    public static void DoesNotBlockWithoutAWorldEntryRequest()
    {
        var gate = new AndroidWorldEntryGate();

        TestHarness.False(gate.ShouldBlock(isReady: false));
        TestHarness.False(gate.IsRequested);
    }

    public static void BlocksRequestedWorldEntryUntilReady()
    {
        var gate = new AndroidWorldEntryGate();
        gate.Request();

        TestHarness.True(gate.ShouldBlock(isReady: false));
        TestHarness.True(gate.IsRequested);
        TestHarness.False(gate.ShouldBlock(isReady: true));
        TestHarness.False(gate.IsRequested);
    }

    public static void ResetClearsARequestedWorldEntry()
    {
        var gate = new AndroidWorldEntryGate();
        gate.Request();

        gate.Reset();

        TestHarness.False(gate.ShouldBlock(isReady: false));
        TestHarness.False(gate.IsRequested);
    }
}
