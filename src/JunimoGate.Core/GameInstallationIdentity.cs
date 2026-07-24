using System.Collections.ObjectModel;

namespace JunimoGate.Core;

/// <summary>One APK participating in an installed game package.</summary>
public sealed record ApkSourceIdentity
{
    public ApkSourceIdentity(string sourcePath, Sha256Digest digest)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            throw new ArgumentException("An APK source path is required.", nameof(sourcePath));
        }

        if (!Path.IsPathFullyQualified(sourcePath))
        {
            throw new ArgumentException("An APK source path must be absolute.", nameof(sourcePath));
        }

        if (!digest.IsValid)
        {
            throw new ArgumentException("A valid APK source digest is required.", nameof(digest));
        }

        SourcePath = Path.GetFullPath(sourcePath);
        Digest = digest;
    }

    public string SourcePath { get; }

    public Sha256Digest Digest { get; }
}

/// <summary>Stable identity fields read from a legally installed game package.</summary>
public sealed record GameInstallationIdentity
{
    public GameInstallationIdentity(
        string packageName,
        string versionName,
        long longVersionCode,
        string abi,
        Sha256Digest signerDigest,
        IEnumerable<ApkSourceIdentity> apkSources)
    {
        if (string.IsNullOrWhiteSpace(packageName) || !packageName.Contains('.', StringComparison.Ordinal))
        {
            throw new ArgumentException("A qualified Android package name is required.", nameof(packageName));
        }

        if (string.IsNullOrWhiteSpace(versionName))
        {
            throw new ArgumentException("A version name is required.", nameof(versionName));
        }

        if (longVersionCode < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(longVersionCode), "The version code cannot be negative.");
        }

        if (string.IsNullOrWhiteSpace(abi))
        {
            throw new ArgumentException("An ABI is required.", nameof(abi));
        }

        if (!signerDigest.IsValid)
        {
            throw new ArgumentException("A valid signer digest is required.", nameof(signerDigest));
        }

        ArgumentNullException.ThrowIfNull(apkSources);
        var sourceArray = apkSources.ToArray();
        if (sourceArray.Length == 0)
        {
            throw new ArgumentException("At least one APK source is required.", nameof(apkSources));
        }

        if (sourceArray.Any(static source => source is null))
        {
            throw new ArgumentException("APK sources cannot contain null entries.", nameof(apkSources));
        }

        if (sourceArray.Select(static source => source.SourcePath).Distinct(StringComparer.Ordinal).Count() != sourceArray.Length)
        {
            throw new ArgumentException("APK source paths must be unique.", nameof(apkSources));
        }

        PackageName = packageName;
        VersionName = versionName;
        LongVersionCode = longVersionCode;
        Abi = abi;
        SignerDigest = signerDigest;
        ApkSources = Array.AsReadOnly(sourceArray);
    }

    public string PackageName { get; }

    public string VersionName { get; }

    public long LongVersionCode { get; }

    public string Abi { get; }

    public Sha256Digest SignerDigest { get; }

    public ReadOnlyCollection<ApkSourceIdentity> ApkSources { get; }
}
