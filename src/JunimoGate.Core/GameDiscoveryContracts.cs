using System.Collections.ObjectModel;

namespace JunimoGate.Core;

/// <summary>Stable machine-readable errors produced while discovering and scanning installed packages.</summary>
public static class GameDiscoveryErrorCodes
{
    public const string PackageNotFoundOrNotVisible = "package_not_found_or_not_visible";
    public const string MetadataInvalid = "metadata_invalid";
    public const string SigningInfoMissing = "signing_info_missing";
    public const string ApkSourceMissing = "apk_source_missing";
    public const string ApkSourceUnreadable = "apk_source_unreadable";
    public const string ApkSourceHashFailed = "apk_source_hash_failed";
    public const string ApkSourceInvalidZip = "apk_source_invalid_zip";
    public const string ContentSourceMissing = "content_source_missing";
    public const string AssemblySourceMissing = "assembly_source_missing";
    public const string AbiUnsupported = "abi_unsupported";
    public const string AbiConflict = "abi_conflict";
    public const string SplitIdentityMismatch = "split_identity_mismatch";
    public const string PackageChangedDuringScan = "package_changed_during_scan";
    public const string GameCertificateUnrecognized = "game_certificate_unrecognized";
    public const string GameCertificatePolicyNotConfigured = "game_certificate_policy_not_configured";
    public const string Cancelled = "cancelled";
}

/// <summary>Platform-neutral metadata for one installed base or split APK path.</summary>
public sealed record PackageApkSourceSnapshot(
    string SourcePath,
    bool IsBase,
    string? SplitName);

/// <summary>Platform-neutral package metadata captured at one point in time.</summary>
public sealed record PackageInstallationSnapshot
{
    public PackageInstallationSnapshot(
        string? packageName,
        string? versionName,
        long? longVersionCode,
        SigningIdentity? signingIdentity,
        IEnumerable<PackageApkSourceSnapshot> apkSources)
    {
        ArgumentNullException.ThrowIfNull(apkSources);
        var sources = apkSources.ToArray();
        if (sources.Any(static source => source is null))
        {
            throw new ArgumentException("Package APK sources cannot contain null entries.", nameof(apkSources));
        }

        PackageName = packageName;
        VersionName = versionName;
        LongVersionCode = longVersionCode;
        SigningIdentity = signingIdentity;
        ApkSources = Array.AsReadOnly(sources);
    }

    /// <summary>Gets the package name reported by the platform.</summary>
    public string? PackageName { get; }

    /// <summary>Gets the version name reported by the platform.</summary>
    public string? VersionName { get; }

    /// <summary>Gets the long version code reported by the platform.</summary>
    public long? LongVersionCode { get; }

    /// <summary>Gets the signing identity, or null when signing information was unavailable.</summary>
    public SigningIdentity? SigningIdentity { get; }

    /// <summary>Gets the installed base and split APK source metadata.</summary>
    public ReadOnlyCollection<PackageApkSourceSnapshot> ApkSources { get; }
}

/// <summary>Provider seam for repeatable package snapshots before and after APK scanning.</summary>
public interface IPackageInstallationSnapshotProvider
{
    /// <summary>Captures the visible package metadata, or returns null when the package is absent or not visible.</summary>
    ValueTask<PackageInstallationSnapshot?> GetSnapshotAsync(
        string packageName,
        CancellationToken cancellationToken = default);
}

/// <summary>Stable platform-neutral role names reported for an APK source.</summary>
public static class ApkSourceRoleNames
{
    /// <summary>APK contains game content assets.</summary>
    public const string GameContent = "game-content";

    /// <summary>APK contains a legacy assemblies blob.</summary>
    public const string LegacyAssemblyBlob = "legacy-assembly-blob";

    /// <summary>APK contains a modern AssemblyStore blob.</summary>
    public const string ModernAssemblyBlob = "modern-assembly-blob";
}

/// <summary>Platform-neutral content and ABI inventory for one logical APK source.</summary>
public sealed record ApkSourceInventory
{
    /// <summary>Creates a canonical inventory for one logical APK source.</summary>
    public ApkSourceInventory(
        string sourceLabel,
        IEnumerable<string> roles,
        IEnumerable<string> nativeAbis,
        IEnumerable<string> assemblyStoreAbis)
    {
        if (string.IsNullOrWhiteSpace(sourceLabel))
        {
            throw new ArgumentException("A logical APK source label is required.", nameof(sourceLabel));
        }

        SourceLabel = sourceLabel;
        Roles = Array.AsReadOnly(NormalizeValues(roles, nameof(roles), normalizeCase: false));
        NativeAbis = Array.AsReadOnly(NormalizeValues(nativeAbis, nameof(nativeAbis), normalizeCase: true));
        AssemblyStoreAbis = Array.AsReadOnly(NormalizeValues(assemblyStoreAbis, nameof(assemblyStoreAbis), normalizeCase: true));
    }

    /// <summary>Gets the stable logical source label matching an APK source identity.</summary>
    public string SourceLabel { get; }

    /// <summary>Gets stable platform-neutral content role names.</summary>
    public ReadOnlyCollection<string> Roles { get; }

    /// <summary>Gets canonical native library ABI names.</summary>
    public ReadOnlyCollection<string> NativeAbis { get; }

    /// <summary>Gets canonical modern AssemblyStore ABI names.</summary>
    public ReadOnlyCollection<string> AssemblyStoreAbis { get; }

    private static string[] NormalizeValues(
        IEnumerable<string> values,
        string parameterName,
        bool normalizeCase)
    {
        ArgumentNullException.ThrowIfNull(values, parameterName);
        return values
            .Select(value =>
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    throw new ArgumentException("Inventory values cannot be null or empty.", parameterName);
                }

                return normalizeCase ? value.ToLowerInvariant() : value;
            })
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }
}

/// <summary>A verified game installation offered as a discovery candidate.</summary>
public sealed record GameInstallationCandidate
{
    /// <summary>Creates a candidate with inventories aligned to every installation APK source.</summary>
    public GameInstallationCandidate(
        GameInstallationIdentity installation,
        IEnumerable<ApkSourceInventory> sourceInventories)
    {
        ArgumentNullException.ThrowIfNull(installation);
        ArgumentNullException.ThrowIfNull(sourceInventories);
        var inventories = sourceInventories.ToArray();
        if (inventories.Any(static inventory => inventory is null))
        {
            throw new ArgumentException("Source inventories cannot contain null entries.", nameof(sourceInventories));
        }

        var inventoryByLabel = inventories.ToDictionary(static inventory => inventory.SourceLabel, StringComparer.Ordinal);
        var sourceLabels = installation.ApkSources.Select(static source => source.Label).ToArray();
        if (inventoryByLabel.Count != inventories.Length ||
            inventoryByLabel.Count != sourceLabels.Length ||
            sourceLabels.Any(label => !inventoryByLabel.ContainsKey(label)))
        {
            throw new ArgumentException("Source inventories must align one-to-one with APK source labels.", nameof(sourceInventories));
        }

        Installation = installation;
        CertificateVerification = KnownGameCertificate.Verify(
            installation.PackageName,
            installation.SigningIdentity);
        SourceInventories = Array.AsReadOnly(sourceLabels.Select(label => inventoryByLabel[label]).ToArray());
    }

    /// <summary>Gets the verified installation identity.</summary>
    public GameInstallationIdentity Installation { get; }

    /// <summary>Gets whether the package certificate matches JunimoGate's tested game identity.</summary>
    public GameCertificateVerification CertificateVerification { get; }

    /// <summary>Gets per-source inventories aligned with the installation APK source order.</summary>
    public ReadOnlyCollection<ApkSourceInventory> SourceInventories { get; }
}

/// <summary>Discovery outcome and diagnostics for one requested package.</summary>
public sealed record PackageDiscoveryReport
{
    public PackageDiscoveryReport(
        string packageName,
        GameInstallationCandidate? candidate,
        IEnumerable<DiagnosticRecord> diagnostics)
    {
        if (string.IsNullOrWhiteSpace(packageName))
        {
            throw new ArgumentException("A requested package name is required.", nameof(packageName));
        }

        ArgumentNullException.ThrowIfNull(diagnostics);
        var diagnosticArray = diagnostics.ToArray();
        if (diagnosticArray.Any(static diagnostic => diagnostic is null))
        {
            throw new ArgumentException("Diagnostics cannot contain null entries.", nameof(diagnostics));
        }

        PackageName = packageName;
        Candidate = candidate;
        Diagnostics = Array.AsReadOnly(diagnosticArray);
    }

    /// <summary>Gets the requested package name.</summary>
    public string PackageName { get; }

    /// <summary>Gets the verified candidate, or null when discovery failed.</summary>
    public GameInstallationCandidate? Candidate { get; }

    /// <summary>Gets structured diagnostics for this package.</summary>
    public ReadOnlyCollection<DiagnosticRecord> Diagnostics { get; }

    /// <summary>Gets whether this report contains a verified candidate.</summary>
    public bool IsSuccess => Candidate is not null;
}

/// <summary>Aggregate report retaining every requested package report and every successful candidate.</summary>
public sealed record GameDiscoveryReport
{
    public GameDiscoveryReport(IEnumerable<PackageDiscoveryReport> packages)
    {
        ArgumentNullException.ThrowIfNull(packages);
        var packageArray = packages.ToArray();
        if (packageArray.Any(static package => package is null))
        {
            throw new ArgumentException("Package reports cannot contain null entries.", nameof(packages));
        }

        if (packageArray.Select(static package => package.PackageName).Distinct(StringComparer.Ordinal).Count() != packageArray.Length)
        {
            throw new ArgumentException("Package reports must have unique package names.", nameof(packages));
        }

        Packages = Array.AsReadOnly(packageArray);
        Candidates = Array.AsReadOnly(packageArray
            .Where(static package => package.Candidate is not null)
            .Select(static package => package.Candidate!)
            .ToArray());
    }

    /// <summary>Gets every requested package report in deterministic request order.</summary>
    public ReadOnlyCollection<PackageDiscoveryReport> Packages { get; }

    /// <summary>Gets every successfully verified installation candidate.</summary>
    public ReadOnlyCollection<GameInstallationCandidate> Candidates { get; }
}
