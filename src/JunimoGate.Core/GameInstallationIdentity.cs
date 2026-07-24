using System.Collections.ObjectModel;

namespace JunimoGate.Core;

/// <summary>Immutable Android package signing identity.</summary>
public sealed class SigningIdentity
{
    public SigningIdentity(
        IEnumerable<Sha256Digest> currentSignerDigests,
        IEnumerable<Sha256Digest>? rotationHistory = null)
    {
        ArgumentNullException.ThrowIfNull(currentSignerDigests);

        var currentSigners = currentSignerDigests
            .Select(RequireValidDigest)
            .Distinct()
            .OrderBy(static digest => digest.Value, StringComparer.Ordinal)
            .ToArray();
        if (currentSigners.Length == 0)
        {
            throw new ArgumentException("At least one current signer digest is required.", nameof(currentSignerDigests));
        }

        var history = rotationHistory?.Select(RequireValidDigest).ToArray() ?? [];
        if (history.Distinct().Count() != history.Length)
        {
            throw new ArgumentException("Signing rotation history cannot contain duplicate digests.", nameof(rotationHistory));
        }

        if (currentSigners.Length > 1 && history.Length != 0)
        {
            throw new ArgumentException("Signing rotation history must be empty for packages with multiple current signers.", nameof(rotationHistory));
        }

        if (history.Length != 0 && !currentSigners.Contains(history[^1]))
        {
            throw new ArgumentException("The final signing rotation digest must be a current signer.", nameof(rotationHistory));
        }

        CurrentSignerDigests = Array.AsReadOnly(currentSigners);
        RotationHistory = Array.AsReadOnly(history);
    }

    /// <summary>Gets the canonical sorted set of current signer certificate digests.</summary>
    public ReadOnlyCollection<Sha256Digest> CurrentSignerDigests { get; }

    /// <summary>Gets the oldest-to-newest signer rotation history, including the current signer as its final item.</summary>
    public ReadOnlyCollection<Sha256Digest> RotationHistory { get; }

    private static Sha256Digest RequireValidDigest(Sha256Digest digest)
    {
        if (!digest.IsValid)
        {
            throw new ArgumentException("Every signer digest must be a valid SHA-256 digest.");
        }

        return digest;
    }
}

/// <summary>One APK participating in an installed game package.</summary>
public sealed record ApkSourceIdentity
{
    public ApkSourceIdentity(
        string sourcePath,
        Sha256Digest digest,
        long size,
        string label,
        string? splitName = null)
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

        if (size < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(size), "An APK source size cannot be negative.");
        }

        if (!IsValidLabel(label))
        {
            throw new ArgumentException("An APK source label must be 'base' or 'split-N' with a positive integer N.", nameof(label));
        }

        if (label.Equals("base", StringComparison.Ordinal) && splitName is not null)
        {
            throw new ArgumentException("The base APK cannot have a split name.", nameof(splitName));
        }

        if (!label.Equals("base", StringComparison.Ordinal) && string.IsNullOrWhiteSpace(splitName))
        {
            throw new ArgumentException("A split APK must have a split name.", nameof(splitName));
        }

        SourcePath = Path.GetFullPath(sourcePath);
        Digest = digest;
        Size = size;
        Label = label;
        SplitName = splitName;
    }

    /// <summary>Gets the absolute installed APK path.</summary>
    public string SourcePath { get; }

    /// <summary>Gets the complete APK file SHA-256 digest.</summary>
    public Sha256Digest Digest { get; }

    /// <summary>Gets the complete APK file size in bytes.</summary>
    public long Size { get; }

    /// <summary>Gets the stable logical source label, either base or split-N.</summary>
    public string Label { get; }

    /// <summary>Gets the platform split name, or null for the base APK.</summary>
    public string? SplitName { get; }

    private static bool IsValidLabel(string? label)
    {
        if (label is null || label.Equals("base", StringComparison.Ordinal))
        {
            return label is not null;
        }

        const string prefix = "split-";
        return label.StartsWith(prefix, StringComparison.Ordinal) &&
            int.TryParse(label.AsSpan(prefix.Length), out var number) &&
            number > 0 &&
            label.Equals($"{prefix}{number}", StringComparison.Ordinal);
    }
}

/// <summary>Stable identity fields read and verified from a legally installed game package.</summary>
public sealed record GameInstallationIdentity
{
    public GameInstallationIdentity(
        string packageName,
        string versionName,
        long longVersionCode,
        SigningIdentity signingIdentity,
        string selectedAbi,
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

        ArgumentNullException.ThrowIfNull(signingIdentity);
        if (string.IsNullOrWhiteSpace(selectedAbi))
        {
            throw new ArgumentException("A selected ABI is required.", nameof(selectedAbi));
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

        if (sourceArray.Select(static source => source.Label).Distinct(StringComparer.Ordinal).Count() != sourceArray.Length)
        {
            throw new ArgumentException("APK source labels must be unique.", nameof(apkSources));
        }

        if (sourceArray.Count(static source => source.Label.Equals("base", StringComparison.Ordinal)) != 1)
        {
            throw new ArgumentException("Exactly one base APK source is required.", nameof(apkSources));
        }

        var orderedSources = sourceArray.OrderBy(static source => SourceOrdinal(source.Label)).ToArray();
        PackageName = packageName;
        VersionName = versionName;
        LongVersionCode = longVersionCode;
        SigningIdentity = signingIdentity;
        SelectedAbi = selectedAbi;
        ApkSources = Array.AsReadOnly(orderedSources);
    }

    /// <summary>Gets the qualified Android package name.</summary>
    public string PackageName { get; }

    /// <summary>Gets the package version name.</summary>
    public string VersionName { get; }

    /// <summary>Gets the package long version code.</summary>
    public long LongVersionCode { get; }

    /// <summary>Gets the verified signing identity.</summary>
    public SigningIdentity SigningIdentity { get; }

    /// <summary>Gets the selected supported ABI.</summary>
    public string SelectedAbi { get; }

    /// <summary>Gets APK sources in stable base-then-split order.</summary>
    public ReadOnlyCollection<ApkSourceIdentity> ApkSources { get; }

    private static int SourceOrdinal(string label) =>
        label.Equals("base", StringComparison.Ordinal)
            ? 0
            : int.Parse(label.AsSpan("split-".Length), System.Globalization.CultureInfo.InvariantCulture);
}
