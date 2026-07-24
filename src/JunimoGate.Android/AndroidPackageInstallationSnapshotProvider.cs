using System.Security.Cryptography;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using JunimoGate.Core;

namespace JunimoGate.Android;

/// <summary>Captures exact installed-package snapshots through Android PackageManager.</summary>
public sealed class AndroidPackageInstallationSnapshotProvider : IPackageInstallationSnapshotProvider
{
    private readonly PackageManager packageManager;

    /// <summary>Creates a provider backed by the application PackageManager.</summary>
    public AndroidPackageInstallationSnapshotProvider(Context context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var safeContext = context.ApplicationContext ?? context;
        packageManager = safeContext.PackageManager
            ?? throw new InvalidOperationException("Android PackageManager is unavailable.");
    }

    /// <inheritdoc />
    public ValueTask<PackageInstallationSnapshot?> GetSnapshotAsync(
        string packageName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(packageName))
        {
            throw new ArgumentException("A package name is required.", nameof(packageName));
        }

        cancellationToken.ThrowIfCancellationRequested();
        PackageInfo packageInfo;
        try
        {
            var flags = OperatingSystem.IsAndroidVersionAtLeast(28)
                ? PackageInfoFlags.SigningCertificates
                : PackageInfoFlags.Signatures;
            packageInfo = packageManager.GetPackageInfo(packageName, flags)
                ?? throw new InvalidOperationException("PackageManager returned no package metadata.");
        }
        catch (PackageManager.NameNotFoundException)
        {
            return ValueTask.FromResult<PackageInstallationSnapshot?>(null);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var applicationInfo = packageInfo.ApplicationInfo
            ?? throw new InvalidOperationException("Package application metadata is missing.");
        var sources = ReadSources(packageInfo, applicationInfo);
        var signingIdentity = ReadSigningIdentity(packageInfo);
        var longVersionCode = ReadLongVersionCode(packageInfo);
        var snapshot = new PackageInstallationSnapshot(
            packageInfo.PackageName,
            packageInfo.VersionName,
            longVersionCode,
            signingIdentity,
            sources);
        return ValueTask.FromResult<PackageInstallationSnapshot?>(snapshot);
    }

    private static IReadOnlyList<PackageApkSourceSnapshot> ReadSources(
        PackageInfo packageInfo,
        ApplicationInfo applicationInfo)
    {
        if (string.IsNullOrWhiteSpace(applicationInfo.SourceDir))
        {
            throw new InvalidOperationException("The base APK source metadata is missing.");
        }

        var sources = new List<PackageApkSourceSnapshot>
        {
            new(applicationInfo.SourceDir, IsBase: true, SplitName: null),
        };
        var splitNames = packageInfo.SplitNames;
        var splitSourceDirs = applicationInfo.SplitSourceDirs;
        if (splitNames is null && splitSourceDirs is null)
        {
            return sources;
        }

        if (splitNames is null ||
            splitSourceDirs is null ||
            splitNames.Count != splitSourceDirs.Count)
        {
            throw new InvalidOperationException("Split APK names and source paths are not aligned.");
        }

        for (var index = 0; index < splitNames.Count; index++)
        {
            if (string.IsNullOrWhiteSpace(splitNames[index]) ||
                string.IsNullOrWhiteSpace(splitSourceDirs[index]))
            {
                throw new InvalidOperationException("Split APK metadata contains a missing name or source path.");
            }

            sources.Add(new PackageApkSourceSnapshot(
                splitSourceDirs[index],
                IsBase: false,
                splitNames[index]));
        }

        return sources;
    }

    private static SigningIdentity? ReadSigningIdentity(PackageInfo packageInfo)
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(28))
        {
            var signingInfo = packageInfo.SigningInfo;
            if (signingInfo is null)
            {
                return null;
            }

            if (signingInfo.HasMultipleSigners)
            {
                var currentSigners = signingInfo.GetApkContentsSigners();
                return CreateCurrentSignerIdentity(currentSigners);
            }

            var history = signingInfo.GetSigningCertificateHistory();
            if (history is null || history.Length == 0)
            {
                return null;
            }

            var historyDigests = history.Select(HashSignature).ToArray();
            return new SigningIdentity([historyDigests[^1]], historyDigests);
        }

#pragma warning disable CS0618 // API 26-27 requires GET_SIGNATURES and PackageInfo.Signatures.
        return CreateCurrentSignerIdentity(packageInfo.Signatures);
#pragma warning restore CS0618
    }

    private static SigningIdentity? CreateCurrentSignerIdentity(IEnumerable<Signature>? signatures)
    {
        if (signatures is null)
        {
            return null;
        }

        var digests = signatures.Select(HashSignature).ToArray();
        return digests.Length == 0 ? null : new SigningIdentity(digests);
    }

    private static Sha256Digest HashSignature(Signature signature)
    {
        if (signature is null)
        {
            throw new InvalidOperationException("Package signing metadata contains a null certificate.");
        }

        var certificate = signature.ToByteArray();
        if (certificate is null || certificate.Length == 0)
        {
            throw new InvalidOperationException("Package signing certificate bytes are missing.");
        }

        return Sha256Digest.Parse(Convert.ToHexStringLower(SHA256.HashData(certificate)));
    }

    private static long ReadLongVersionCode(PackageInfo packageInfo)
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(28))
        {
            return packageInfo.LongVersionCode;
        }

#pragma warning disable CS0618 // PackageInfo.VersionCode is the API 26-27 fallback.
        return packageInfo.VersionCode;
#pragma warning restore CS0618
    }
}
