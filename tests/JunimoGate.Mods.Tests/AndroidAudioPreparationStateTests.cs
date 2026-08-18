using JunimoGate.Tests;
using StardewModdingAPI.Mobile.Audio;

internal static class AndroidAudioPreparationStateTests
{
    public static void DistinguishesReadyAndErrorCompletion()
    {
        var ready = new AndroidAudioPreparationState(1, 2);
        ready.CompleteCue(hadError: false);
        TestHarness.Equal(AndroidAudioPreparationStatus.Preparing, ready.Snapshot.Status);
        ready.CompleteCue(hadError: false);

        var failed = new AndroidAudioPreparationState(2, 2);
        failed.CompleteCue(hadError: true);
        failed.CompleteCue(hadError: false);

        TestHarness.Equal(AndroidAudioPreparationStatus.Ready, ready.Snapshot.Status);
        TestHarness.True(ready.Snapshot.IsReady);
        TestHarness.Equal(AndroidAudioPreparationStatus.CompletedWithErrors, failed.Snapshot.Status);
        TestHarness.Equal(1, failed.Snapshot.ErrorCount);
        TestHarness.True(failed.Snapshot.IsReady);
    }

    public static void SupersededGenerationCannotComplete()
    {
        var state = new AndroidAudioPreparationState(3, 2);

        state.Supersede();
        state.CompleteCue(hadError: false);
        state.CompleteRemainingWithErrors();

        TestHarness.Equal(AndroidAudioPreparationStatus.Superseded, state.Snapshot.Status);
        TestHarness.Equal(2, state.Snapshot.RemainingCueCount);
        TestHarness.True(state.Snapshot.IsReady);
        TestHarness.True(state.CancellationToken.IsCancellationRequested);
    }

    public static void TerminalFailureCompletesRemainingWork()
    {
        var state = new AndroidAudioPreparationState(4, 3);
        state.CompleteCue(hadError: false);

        state.CompleteRemainingWithErrors();

        TestHarness.Equal(AndroidAudioPreparationStatus.CompletedWithErrors, state.Snapshot.Status);
        TestHarness.Equal(0, state.Snapshot.RemainingCueCount);
        TestHarness.Equal(2, state.Snapshot.ErrorCount);
    }
}
