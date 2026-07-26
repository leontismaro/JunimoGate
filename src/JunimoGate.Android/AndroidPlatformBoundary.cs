using Android.Content;
using JunimoGate.Core;
using JunimoGate.Extraction;

namespace JunimoGate.Android;

/// <summary>Android entry points for discovering supported installed games and preparing private workspaces.</summary>
public static class AndroidPlatformBoundary
{
    /// <summary>Google Play package name for Stardew Valley.</summary>
    public const string PlayPackageName = "com.chucklefish.stardewvalley";

    /// <summary>Samsung Galaxy Store package name for Stardew Valley.</summary>
    public const string SamsungPackageName = "com.chucklefish.stardewvalleysamsung";

    /// <summary>Discovers every visible supported game package without selecting a preferred candidate.</summary>
    public static ValueTask<GameDiscoveryReport> DiscoverGamesAsync(
        Context context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var provider = new AndroidPackageInstallationSnapshotProvider(context);
        var coordinator = new GameInstallationDiscoveryCoordinator(provider);
        return coordinator.AnalyzeAsync(
            [PlayPackageName, SamsungPackageName],
            cancellationToken);
    }

    /// <summary>Prepares a live discovery candidate beneath the application's fixed private runtime root.</summary>
    public static async ValueTask<WorkspacePreparationResult> PrepareGameWorkspaceAsync(
        Context context,
        GameInstallationCandidate candidate,
        IProgress<WorkspaceProgressEvent>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(candidate);
        var packageName = candidate.Installation.PackageName;
        if (!packageName.Equals(PlayPackageName, StringComparison.Ordinal) &&
            !packageName.Equals(SamsungPackageName, StringComparison.Ordinal))
        {
            throw new ArgumentException("The candidate package is not supported by the Android boundary.", nameof(candidate));
        }

        var safeContext = context.ApplicationContext ?? context;
        await AndroidPrivateStorage.EnsureMigratedAsync(safeContext, cancellationToken).ConfigureAwait(false);
        var runtimeRoot = AndroidPrivateStorage.GetRuntimeRoot(safeContext);

        var options = new WorkspacePreparationOptions
        {
            ExtractorSchema = WorkspacePreparationOptions.DefaultExtractorSchema,
            ManifestSchema = WorkspacePreparationOptions.DefaultManifestSchema,
            RewriterRecipe = "unrewritten:v1",
            SmapiBuildId = "none",
            Progress = progress,
        };
        var request = new WorkspacePreparationRequest(
            runtimeRoot,
            candidate,
            options);
        var revalidator = new AndroidPackageWorkspaceCandidateRevalidator(safeContext, packageName);
        var preparer = new GameWorkspacePreparer(revalidator);
        return await preparer.PrepareAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
