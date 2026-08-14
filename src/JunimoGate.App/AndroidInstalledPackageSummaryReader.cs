using System.Security.Cryptography;
using Android.Content;
using Android.Content.PM;
using Android.Graphics.Drawables;
using JunimoGate.Core;

namespace JunimoGate.App;

internal sealed class AndroidInstalledPackageSummaryReader : IInstalledPackageSummaryReader
{
    private readonly PackageManager packageManager;
    private readonly Dictionary<string, Drawable?> icons = new(StringComparer.Ordinal);

    public AndroidInstalledPackageSummaryReader(Context context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var safeContext = context.ApplicationContext ?? context;
        packageManager = safeContext.PackageManager
            ?? throw new InvalidOperationException("Android PackageManager is unavailable.");
    }

    public InstalledPackageSummary? Read(string packageName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageName);
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
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var application = packageInfo.ApplicationInfo;
        var displayName = ReadDisplayName(application);
        icons[packageName] = ReadIcon(application);
        return new InstalledPackageSummary(
            packageInfo.PackageName ?? packageName,
            displayName,
            packageInfo.VersionName,
            ReadLongVersionCode(packageInfo),
            ReadSigningIdentity(packageInfo));
    }

    public Drawable? GetIcon(string packageName) =>
        icons.GetValueOrDefault(packageName);

    private string ReadDisplayName(ApplicationInfo? application)
    {
        try
        {
            var displayName = application?.LoadLabel(packageManager)?.ToString();
            return string.IsNullOrWhiteSpace(displayName) ? "Stardew Valley" : displayName;
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            return "Stardew Valley";
        }
    }

    private Drawable? ReadIcon(ApplicationInfo? application)
    {
        try
        {
            return application?.LoadIcon(packageManager);
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            return null;
        }
    }

    private static SigningIdentity? ReadSigningIdentity(PackageInfo packageInfo)
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(28))
        {
            var signingInfo = packageInfo.SigningInfo;
            if (signingInfo is null)
                return null;
            if (signingInfo.HasMultipleSigners)
                return CreateCurrentSignerIdentity(signingInfo.GetApkContentsSigners());
            var history = signingInfo.GetSigningCertificateHistory();
            if (history is null || history.Length == 0)
                return null;
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
            return null;
        var digests = signatures.Select(HashSignature).ToArray();
        return digests.Length == 0 ? null : new SigningIdentity(digests);
    }

    private static Sha256Digest HashSignature(Signature signature)
    {
        if (signature is null)
            throw new InvalidOperationException("Package signing metadata contains a null certificate.");
        var certificate = signature.ToByteArray();
        if (certificate is null || certificate.Length == 0)
            throw new InvalidOperationException("Package signing certificate bytes are missing.");
        return Sha256Digest.Parse(Convert.ToHexStringLower(SHA256.HashData(certificate)));
    }

    private static long ReadLongVersionCode(PackageInfo packageInfo)
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(28))
            return packageInfo.LongVersionCode;
#pragma warning disable CS0618 // PackageInfo.VersionCode is the API 26-27 fallback.
        return packageInfo.VersionCode;
#pragma warning restore CS0618
    }

    private static bool IsRecoverable(Exception exception) => exception is not (
        OutOfMemoryException or
        StackOverflowException or
        AccessViolationException);
}
