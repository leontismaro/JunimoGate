using System.Text.Json;
using Android.Content;
using JunimoGate.Android;
using JunimoGate.Core;
using JunimoGate.Mods;

namespace JunimoGate.GameHost;

public static partial class GameLaunchRegistry
{
    private const int MaximumModSelectionBytes = 2 * 1024 * 1024;

    private static string GetProfilesRoot(Context context) =>
        Path.Combine(AndroidPrivateStorage.GetUserDataRoot(context.ApplicationContext ?? context), "profiles");

    private static string GetModsRoot(Context context) =>
        Path.Combine(AndroidPrivateStorage.GetUserDataRoot(context.ApplicationContext ?? context), "mods");

    private static string GetSettingsRoot(Context context) =>
        Path.Combine(AndroidPrivateStorage.GetUserDataRoot(context.ApplicationContext ?? context), "settings");

    private static string GetModSelectionRoot(Context context) =>
        Path.Combine(
            AndroidPrivateStorage.GetRuntimeRoot(context.ApplicationContext ?? context),
            "mod-selections");

    private static string GetModSelectionPath(Context context, string selectionId) =>
        Path.Combine(GetModSelectionRoot(context), $"selection-{selectionId}.json");

    private static async ValueTask<ModLaunchSelectionSnapshot?> TryReadModSelectionAsync(
        Context context,
        string selectionId,
        CancellationToken cancellationToken)
    {
        if (!IsSnapshotId(selectionId))
            return null;
        try
        {
            var selection = await ReadJsonAsync<ModLaunchSelectionSnapshot>(
                    GetModSelectionPath(context, selectionId),
                    MaximumModSelectionBytes,
                    cancellationToken)
                .ConfigureAwait(false);
            _ = selection?.Validate();
            return selection;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          JsonException or InvalidDataException)
        {
            return null;
        }
    }

    private static async ValueTask<bool> IsCurrentModSelectionAsync(
        Context context,
        ModLaunchSelectionSnapshot? selection,
        CancellationToken cancellationToken)
    {
        if (selection is null)
            return false;
        ProfileId profileId;
        try
        {
            profileId = selection.Validate();
        }
        catch (InvalidDataException)
        {
            return false;
        }

        try
        {
            var profile = await new ModProfileV2Repository(GetProfilesRoot(context))
                .ReadAsync(profileId, cancellationToken)
                .ConfigureAwait(false);
            var library = await new ModLibraryRepository(GetModsRoot(context))
                .ReadAsync(cancellationToken)
                .ConfigureAwait(false);
            var globalSettings = await new LauncherSettingsRepository(GetSettingsRoot(context))
                .OpenOrCreateAsync(ModAssemblyBindingPolicy.HighestCompatible, cancellationToken)
                .ConfigureAwait(false);
            return selection.Matches(profile, library, globalSettings.DefaultAssemblyBindingPolicy);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return false;
        }
    }
}
