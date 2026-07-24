using Android.Content;
using JunimoGate.Core;
using JunimoGate.Extraction;

namespace JunimoGate.Android;

/// <summary>Re-discovers one exact Android package before workspace activation.</summary>
public sealed class AndroidPackageWorkspaceCandidateRevalidator : IWorkspaceCandidateRevalidator
{
    private readonly string exactPackageName;
    private readonly GameInstallationDiscoveryCoordinator coordinator;

    /// <summary>Creates a revalidator scoped to one exact package name.</summary>
    public AndroidPackageWorkspaceCandidateRevalidator(Context context, string exactPackageName)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(exactPackageName);
        this.exactPackageName = exactPackageName;
        coordinator = new GameInstallationDiscoveryCoordinator(
            new AndroidPackageInstallationSnapshotProvider(context));
    }

    /// <inheritdoc />
    public async ValueTask<GameInstallationCandidate?> RevalidateAsync(
        string packageName,
        CancellationToken cancellationToken = default)
    {
        if (!exactPackageName.Equals(packageName, StringComparison.Ordinal))
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var report = await coordinator
            .AnalyzeAsync([exactPackageName], cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return report.Packages.Count == 1 &&
            report.Packages[0].PackageName.Equals(exactPackageName, StringComparison.Ordinal)
                ? report.Packages[0].Candidate
                : null;
    }
}
