using System.Security.Cryptography;
using System.Text;

namespace JunimoGate.Core;

/// <summary>
/// Creates a cheap package/update identity from PackageManager metadata and APK file statistics.
/// It never reads or hashes APK contents.
/// </summary>
public static class PackageUpdateMarker
{
    public const string Format = "junimogate-package-update-marker/v1";

    public static string Create(PackageInstallationSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (string.IsNullOrWhiteSpace(snapshot.PackageName) ||
            string.IsNullOrWhiteSpace(snapshot.VersionName) ||
            snapshot.LongVersionCode is null or < 0 ||
            snapshot.SigningIdentity is null ||
            snapshot.LastUpdateTimeUtcMilliseconds is null or < 0 ||
            snapshot.ApkSources.Count == 0)
        {
            throw new InvalidDataException("The package snapshot cannot produce an update marker.");
        }

        var builder = new StringBuilder();
        Append(builder, Format);
        Append(builder, snapshot.PackageName);
        Append(builder, snapshot.VersionName);
        Append(builder, snapshot.LongVersionCode.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Append(builder, snapshot.LastUpdateTimeUtcMilliseconds.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        foreach (var digest in snapshot.SigningIdentity.CurrentSignerDigests)
            Append(builder, $"current:{digest.Value}");
        foreach (var digest in snapshot.SigningIdentity.RotationHistory)
            Append(builder, $"history:{digest.Value}");

        foreach (var source in snapshot.ApkSources
                     .OrderBy(static item => item.IsBase ? 0 : 1)
                     .ThenBy(static item => item.SplitName, StringComparer.Ordinal)
                     .ThenBy(static item => item.SourcePath, StringComparer.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(source.SourcePath) ||
                !Path.IsPathFullyQualified(source.SourcePath) ||
                source.Size < 0 ||
                source.LastModifiedTimeUtcMilliseconds < 0 ||
                (source.IsBase ? source.SplitName is not null : string.IsNullOrWhiteSpace(source.SplitName)))
            {
                throw new InvalidDataException("The package source metadata cannot produce an update marker.");
            }

            Append(builder, source.IsBase ? "base" : $"split:{source.SplitName}");
            Append(builder, Path.GetFullPath(source.SourcePath));
            Append(builder, source.Size.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Append(builder, source.LastModifiedTimeUtcMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private static void Append(StringBuilder builder, string value) =>
        builder.Append(value.Length).Append(':').Append(value).Append('\n');
}
