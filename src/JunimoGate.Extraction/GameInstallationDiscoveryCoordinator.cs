using JunimoGate.Core;

namespace JunimoGate.Extraction;

/// <summary>Coordinates repeatable platform snapshots with safe APK source scanning.</summary>
public sealed class GameInstallationDiscoveryCoordinator
{
    public const string SupportedAbi = "arm64-v8a";

    private readonly IPackageInstallationSnapshotProvider snapshotProvider;
    private readonly ApkSourceAnalyzer sourceAnalyzer;

    public GameInstallationDiscoveryCoordinator(
        IPackageInstallationSnapshotProvider snapshotProvider,
        ApkSourceAnalyzer? sourceAnalyzer = null)
    {
        ArgumentNullException.ThrowIfNull(snapshotProvider);
        this.snapshotProvider = snapshotProvider;
        this.sourceAnalyzer = sourceAnalyzer ?? new ApkSourceAnalyzer();
    }

    /// <summary>Analyzes every distinct requested package and returns all successful candidates and per-package diagnostics.</summary>
    public async ValueTask<GameDiscoveryReport> AnalyzeAsync(
        IEnumerable<string> packageNames,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(packageNames);
        var requestedPackages = packageNames.Distinct(StringComparer.Ordinal).ToArray();
        if (requestedPackages.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("Requested package names cannot be empty.", nameof(packageNames));
        }

        var reports = new List<PackageDiscoveryReport>(requestedPackages.Length);
        foreach (var packageName in requestedPackages)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                reports.Add(FailureReport(packageName, Diagnostic(
                    StartupStage.Discovery,
                    GameDiscoveryErrorCodes.Cancelled,
                    "Package discovery was cancelled.",
                    severity: DiagnosticSeverity.Warning)));
                continue;
            }

            reports.Add(await AnalyzePackageAsync(packageName, cancellationToken).ConfigureAwait(false));
        }

        return new GameDiscoveryReport(reports);
    }

    private async ValueTask<PackageDiscoveryReport> AnalyzePackageAsync(
        string requestedPackageName,
        CancellationToken cancellationToken)
    {
        var firstSnapshotResult = await CaptureSnapshotAsync(requestedPackageName, cancellationToken).ConfigureAwait(false);
        if (firstSnapshotResult.Diagnostic is not null)
        {
            return FailureReport(requestedPackageName, firstSnapshotResult.Diagnostic);
        }

        var firstSnapshot = firstSnapshotResult.Snapshot;
        if (firstSnapshot is null)
        {
            return FailureReport(requestedPackageName, Diagnostic(
                StartupStage.Discovery,
                GameDiscoveryErrorCodes.PackageNotFoundOrNotVisible,
                "The package was not found or is not visible."));
        }

        var validationDiagnostics = ValidateSnapshot(requestedPackageName, firstSnapshot);
        if (validationDiagnostics.Count != 0)
        {
            return new PackageDiscoveryReport(requestedPackageName, null, validationDiagnostics);
        }

        var labeledSources = CreateStableSourceLabels(firstSnapshot);
        var scans = new List<ApkSourceScanResult>(labeledSources.Count);
        var diagnostics = new List<DiagnosticRecord>();
        foreach (var source in labeledSources)
        {
            var scan = await sourceAnalyzer.AnalyzeAsync(source.Source, source.Label, cancellationToken).ConfigureAwait(false);
            scans.Add(scan);
            if (scan.Diagnostic is not null)
            {
                diagnostics.Add(scan.Diagnostic);
                if (scan.Diagnostic.Code.Equals(GameDiscoveryErrorCodes.Cancelled, StringComparison.Ordinal))
                {
                    return new PackageDiscoveryReport(requestedPackageName, null, diagnostics);
                }
            }
        }

        AddContentAndAbiDiagnostics(scans, diagnostics);

        var secondSnapshotResult = await CaptureSnapshotAsync(requestedPackageName, cancellationToken).ConfigureAwait(false);
        if (secondSnapshotResult.Diagnostic is not null)
        {
            diagnostics.Add(secondSnapshotResult.Diagnostic);
            return new PackageDiscoveryReport(requestedPackageName, null, diagnostics);
        }

        if (secondSnapshotResult.Snapshot is null || !SnapshotsMatch(firstSnapshot, secondSnapshotResult.Snapshot))
        {
            diagnostics.Add(Diagnostic(
                StartupStage.Discovery,
                GameDiscoveryErrorCodes.PackageChangedDuringScan,
                "Package metadata changed while APK sources were being scanned."));
        }

        if (diagnostics.Any(static diagnostic => diagnostic.Severity >= DiagnosticSeverity.Error))
        {
            return new PackageDiscoveryReport(requestedPackageName, null, diagnostics);
        }

        var identity = new GameInstallationIdentity(
            firstSnapshot.PackageName!,
            firstSnapshot.VersionName!,
            firstSnapshot.LongVersionCode!.Value,
            firstSnapshot.SigningIdentity!,
            SupportedAbi,
            scans.Select(static scan => scan.Source!));
        var sourceInventories = scans.Select(MapInventory).ToArray();
        var candidate = new GameInstallationCandidate(identity, sourceInventories);
        AddCertificateDiagnostic(candidate.CertificateVerification, diagnostics);
        return new PackageDiscoveryReport(
            requestedPackageName,
            candidate,
            diagnostics);
    }

    private async ValueTask<SnapshotCaptureResult> CaptureSnapshotAsync(
        string packageName,
        CancellationToken cancellationToken)
    {
        try
        {
            var snapshot = await snapshotProvider.GetSnapshotAsync(packageName, cancellationToken).ConfigureAwait(false);
            return new SnapshotCaptureResult(snapshot, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new SnapshotCaptureResult(null, Diagnostic(
                StartupStage.Discovery,
                GameDiscoveryErrorCodes.Cancelled,
                "Package discovery was cancelled.",
                severity: DiagnosticSeverity.Warning));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return new SnapshotCaptureResult(null, Diagnostic(
                StartupStage.Discovery,
                GameDiscoveryErrorCodes.MetadataInvalid,
                "Package metadata could not be captured."));
        }
    }

    private static List<DiagnosticRecord> ValidateSnapshot(
        string requestedPackageName,
        PackageInstallationSnapshot snapshot)
    {
        var diagnostics = new List<DiagnosticRecord>();
        if (!requestedPackageName.Equals(snapshot.PackageName, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(snapshot.PackageName) ||
            !snapshot.PackageName.Contains('.', StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(snapshot.VersionName) ||
            snapshot.LongVersionCode is null or < 0)
        {
            diagnostics.Add(Diagnostic(
                StartupStage.Discovery,
                GameDiscoveryErrorCodes.MetadataInvalid,
                "The package name or version metadata is invalid."));
        }

        if (snapshot.SigningIdentity is null)
        {
            diagnostics.Add(Diagnostic(
                StartupStage.Discovery,
                GameDiscoveryErrorCodes.SigningInfoMissing,
                "Package signing information is missing."));
        }

        var sources = snapshot.ApkSources;
        var baseSources = sources.Where(static source => source.IsBase).ToArray();
        var splitSources = sources.Where(static source => !source.IsBase).ToArray();
        var sourcePathsValid = sources.All(static source =>
            !string.IsNullOrWhiteSpace(source.SourcePath) && Path.IsPathFullyQualified(source.SourcePath));
        var sourcePathsUnique = sources
            .Select(static source => source.SourcePath)
            .Distinct(StringComparer.Ordinal)
            .Count() == sources.Count;
        var splitNamesValid = splitSources.All(static source => !string.IsNullOrWhiteSpace(source.SplitName));
        var splitNamesUnique = splitSources
            .Select(static source => source.SplitName)
            .Distinct(StringComparer.Ordinal)
            .Count() == splitSources.Length;
        var baseIdentityValid = baseSources.Length == 1 && baseSources[0].SplitName is null;
        if (sources.Count == 0 ||
            !sourcePathsValid ||
            !sourcePathsUnique ||
            !splitNamesValid ||
            !splitNamesUnique ||
            !baseIdentityValid)
        {
            diagnostics.Add(Diagnostic(
                StartupStage.Discovery,
                GameDiscoveryErrorCodes.SplitIdentityMismatch,
                "Base and split APK source identities are inconsistent."));
        }

        return diagnostics;
    }

    private static IReadOnlyList<LabeledSource> CreateStableSourceLabels(PackageInstallationSnapshot snapshot)
    {
        var sources = new List<LabeledSource>(snapshot.ApkSources.Count);
        sources.Add(new LabeledSource(
            snapshot.ApkSources.Single(static source => source.IsBase),
            "base"));

        var splitNumber = 1;
        foreach (var split in snapshot.ApkSources
                     .Where(static source => !source.IsBase)
                     .OrderBy(static source => source.SplitName, StringComparer.Ordinal))
        {
            sources.Add(new LabeledSource(split, $"split-{splitNumber}"));
            splitNumber++;
        }

        return sources;
    }

    private static void AddContentAndAbiDiagnostics(
        IEnumerable<ApkSourceScanResult> scans,
        ICollection<DiagnosticRecord> diagnostics)
    {
        var successfulInventories = scans
            .Where(static scan => scan.IsSuccess)
            .Select(static scan => scan.Inventory!)
            .ToArray();
        var hasContent = successfulInventories.Any(static inventory => inventory.Contains(ApkContentRole.GameContent));
        if (!hasContent)
        {
            diagnostics.Add(Diagnostic(
                StartupStage.Inventory,
                GameDiscoveryErrorCodes.ContentSourceMissing,
                "No APK source contains the required game content role."));
        }

        var hasLegacyAssembly = successfulInventories.Any(static inventory => inventory.Contains(ApkContentRole.LegacyAssemblyBlob));
        var hasAnyModernAssembly = successfulInventories.Any(static inventory => inventory.Contains(ApkContentRole.ModernAssemblyBlob));
        if (!hasLegacyAssembly && !hasAnyModernAssembly)
        {
            diagnostics.Add(Diagnostic(
                StartupStage.Inventory,
                GameDiscoveryErrorCodes.AssemblySourceMissing,
                "No APK source contains a supported assembly role."));
            return;
        }

        var nativeAbis = successfulInventories
            .SelectMany(static inventory => inventory.NativeAbis)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var modernAbis = successfulInventories
            .SelectMany(static inventory => inventory.ModernAssemblyStoreAbis)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var hasArm64Native = nativeAbis.Contains(SupportedAbi);
        var hasArm64ModernAssembly = modernAbis.Contains(SupportedAbi);
        var hasSupportedLegacyAssembly = hasLegacyAssembly && hasArm64Native;
        if (hasArm64ModernAssembly || hasSupportedLegacyAssembly)
        {
            return;
        }

        var code = hasAnyModernAssembly && hasArm64Native
            ? GameDiscoveryErrorCodes.AbiConflict
            : GameDiscoveryErrorCodes.AbiUnsupported;
        var message = code.Equals(GameDiscoveryErrorCodes.AbiConflict, StringComparison.Ordinal)
            ? "Native and AssemblyStore ABI evidence conflicts for the supported ABI."
            : "APK contents do not provide a supported arm64-v8a assembly source.";
        diagnostics.Add(Diagnostic(StartupStage.Inventory, code, message));
    }

    private static void AddCertificateDiagnostic(
        GameCertificateVerification verification,
        ICollection<DiagnosticRecord> diagnostics)
    {
        switch (verification.Status)
        {
            case GameCertificateStatus.Unrecognized:
                diagnostics.Add(Diagnostic(
                    StartupStage.Discovery,
                    GameDiscoveryErrorCodes.GameCertificateUnrecognized,
                    "The package certificate does not match JunimoGate's tested game identity; later stages must not execute its code.",
                    DiagnosticSeverity.Warning));
                break;
            case GameCertificateStatus.NotConfigured:
                diagnostics.Add(Diagnostic(
                    StartupStage.Discovery,
                    GameDiscoveryErrorCodes.GameCertificatePolicyNotConfigured,
                    "No tested game certificate is configured for this package; later stages must not execute its code.",
                    DiagnosticSeverity.Warning));
                break;
        }
    }

    private static ApkSourceInventory MapInventory(ApkSourceScanResult scan)
    {
        var inventory = scan.Inventory!;
        var roles = new List<string>(3);
        if (inventory.Contains(ApkContentRole.GameContent))
        {
            roles.Add(ApkSourceRoleNames.GameContent);
        }

        if (inventory.Contains(ApkContentRole.LegacyAssemblyBlob))
        {
            roles.Add(ApkSourceRoleNames.LegacyAssemblyBlob);
        }

        if (inventory.Contains(ApkContentRole.ModernAssemblyBlob))
        {
            roles.Add(ApkSourceRoleNames.ModernAssemblyBlob);
        }

        return new ApkSourceInventory(
            scan.Label,
            roles,
            inventory.NativeAbis,
            inventory.ModernAssemblyStoreAbis);
    }

    private static bool SnapshotsMatch(
        PackageInstallationSnapshot first,
        PackageInstallationSnapshot second)
    {
        if (!string.Equals(first.PackageName, second.PackageName, StringComparison.Ordinal) ||
            !string.Equals(first.VersionName, second.VersionName, StringComparison.Ordinal) ||
            first.LongVersionCode != second.LongVersionCode ||
            first.LastUpdateTimeUtcMilliseconds != second.LastUpdateTimeUtcMilliseconds ||
            !SigningIdentitiesMatch(first.SigningIdentity, second.SigningIdentity))
        {
            return false;
        }

        var firstSources = CanonicalSnapshotSources(first.ApkSources);
        var secondSources = CanonicalSnapshotSources(second.ApkSources);
        return firstSources.SequenceEqual(secondSources);
    }

    private static bool SigningIdentitiesMatch(SigningIdentity? first, SigningIdentity? second)
    {
        if (first is null || second is null)
        {
            return first is null && second is null;
        }

        return first.CurrentSignerDigests.SequenceEqual(second.CurrentSignerDigests) &&
            first.RotationHistory.SequenceEqual(second.RotationHistory);
    }

    private static IEnumerable<string> CanonicalSnapshotSources(
        IEnumerable<PackageApkSourceSnapshot> sources) =>
        sources
            .Select(static source => $"{source.IsBase}\0{source.SplitName}\0{source.SourcePath}\0{source.Size}\0{source.LastModifiedTimeUtcMilliseconds}")
            .Order(StringComparer.Ordinal);

    private static PackageDiscoveryReport FailureReport(string packageName, DiagnosticRecord diagnostic) =>
        new(packageName, null, [diagnostic]);

    private static DiagnosticRecord Diagnostic(
        StartupStage stage,
        string code,
        string message,
        DiagnosticSeverity severity = DiagnosticSeverity.Error) =>
        new(DateTimeOffset.UtcNow, stage, severity, code, message);

    private sealed record LabeledSource(PackageApkSourceSnapshot Source, string Label);

    private sealed record SnapshotCaptureResult(
        PackageInstallationSnapshot? Snapshot,
        DiagnosticRecord? Diagnostic);
}
