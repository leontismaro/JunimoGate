using Android.Content;
using JString = Java.Lang.String;

namespace JunimoGate.App;

internal static class LauncherTextFormatter
{
    public static string Format(Context context, LauncherState state)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(state);
        return state.Message switch
        {
            LauncherMessageKey.CheckingInstalledGame => context.GetString(Resource.String.launcher_checking_game)!,
            LauncherMessageKey.CheckingWorkspace => context.GetString(Resource.String.launcher_checking_workspace)!,
            LauncherMessageKey.NeedsPreparation => context.GetString(Resource.String.launcher_needs_preparation)!,
            LauncherMessageKey.RecoveryAvailable => context.GetString(Resource.String.launcher_recovery_available)!,
            LauncherMessageKey.PreparingGame => context.GetString(Resource.String.launcher_preparing_game)!,
            LauncherMessageKey.PreparedGameChanged => context.GetString(Resource.String.launcher_prepared_game_changed)!,
            LauncherMessageKey.GameUpdated => context.GetString(Resource.String.launcher_game_updated)!,
            LauncherMessageKey.Recovering => context.GetString(Resource.String.launcher_recovering)!,
            LauncherMessageKey.GameNotInstalled => context.GetString(Resource.String.launcher_game_not_installed)!,
            LauncherMessageKey.Unsupported => Format(
                context,
                Resource.String.launcher_unsupported,
                state.Detail ?? "unknown"),
            LauncherMessageKey.Failed => Format(
                context,
                Resource.String.launcher_failed,
                state.Detail ?? "launcher_failed"),
            LauncherMessageKey.Ready => Format(
                context,
                Resource.String.launcher_ready,
                state.Detail ?? "—"),
            LauncherMessageKey.ModConfigurationInvalid => context.GetString(Resource.String.launcher_mod_configuration_invalid)!,
            LauncherMessageKey.Launching => context.GetString(Resource.String.launcher_launching)!,
            _ => throw new InvalidOperationException("The launcher message key is invalid."),
        };
    }

    public static int GetActionTextResource(LauncherState state) => state.Status switch
    {
        LauncherStatus.NeedsPreparation => Resource.String.prepare_and_launch,
        LauncherStatus.RecoveryAvailable or LauncherStatus.Failed => Resource.String.retry_and_launch,
        LauncherStatus.Preparing or LauncherStatus.Recovering or LauncherStatus.Launching => Resource.String.launching_game,
        _ => Resource.String.launch_game,
    };

    private static string Format(Context context, int resourceId, string value) =>
        context.Resources?.GetString(resourceId, [new JString(value)])
        ?? throw new InvalidOperationException("The launcher string resource is unavailable.");
}
