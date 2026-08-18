using JunimoGate.Tests;
using StardewModdingAPI.Mobile;

internal static class AndroidWorldEntryGateTests
{
    public static void DoesNotBlockWithoutAWorldEntryRequest()
    {
        var gate = new AndroidWorldEntryGate();

        var observation = gate.ObserveDependency(isReady: false);

        TestHarness.False(observation.ShouldBlock);
        TestHarness.Equal(AndroidWorldEntryGate.WorldEntryState.Idle, gate.State);
    }

    public static void BlocksRequestedWorldEntryUntilReady()
    {
        var gate = new AndroidWorldEntryGate();
        gate.Request();

        var waiting = gate.ObserveDependency(isReady: false);
        var ready = gate.ObserveDependency(isReady: true);

        TestHarness.True(waiting.ShouldBlock);
        TestHarness.Equal(AndroidWorldEntryGate.WorldEntryTransition.StartedWaiting, waiting.Transition);
        TestHarness.False(ready.ShouldBlock);
        TestHarness.Equal(AndroidWorldEntryGate.WorldEntryTransition.DependencyReady, ready.Transition);
        TestHarness.Equal(AndroidWorldEntryGate.WorldEntryState.Idle, gate.State);
    }

    public static void ResetClearsARequestedWorldEntry()
    {
        var gate = new AndroidWorldEntryGate();
        gate.Request();

        gate.Reset();

        TestHarness.False(gate.ObserveDependency(isReady: false).ShouldBlock);
        TestHarness.Equal(AndroidWorldEntryGate.WorldEntryState.Idle, gate.State);
    }

    public static void TracksLoaderWaitingAndCompletion()
    {
        var gate = new AndroidWorldEntryGate();

        TestHarness.True(gate.BeginLoading());
        var waiting = gate.ObserveDependency(isReady: false);
        var loading = gate.ObserveDependency(isReady: true);

        TestHarness.True(waiting.ShouldBlock);
        TestHarness.Equal(AndroidWorldEntryGate.WorldEntryState.LoaderWaitingForAudio, waiting.State);
        TestHarness.False(loading.ShouldBlock);
        TestHarness.True(loading.ShouldAdvanceLoader);
        TestHarness.Equal(AndroidWorldEntryGate.WorldEntryState.Loading, loading.State);

        gate.CompleteLoading();

        TestHarness.Equal(AndroidWorldEntryGate.WorldEntryState.Idle, gate.State);
        TestHarness.False(gate.ObserveDependency(isReady: false).ShouldAdvanceLoader);
    }
}
