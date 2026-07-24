using System.Collections.ObjectModel;

namespace JunimoGate.Extraction;

/// <summary>Content roles recognized from APK ZIP entry paths.</summary>
[Flags]
public enum ApkContentRole
{
    None = 0,
    GameContent = 1,
    LegacyAssemblyBlob = 2,
    ModernAssemblyBlob = 4,
}

/// <summary>Classifies APK roles and ABI evidence solely from normalized ZIP entry paths.</summary>
public sealed class ApkEntryInventory
{
    private ApkEntryInventory(
        ApkContentRole roles,
        IEnumerable<string> nativeAbis,
        IEnumerable<string> modernAssemblyStoreAbis)
    {
        Roles = roles;
        NativeAbis = Array.AsReadOnly(nativeAbis.Order(StringComparer.OrdinalIgnoreCase).ToArray());
        ModernAssemblyStoreAbis = Array.AsReadOnly(modernAssemblyStoreAbis.Order(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    /// <summary>Gets all content roles present in the APK.</summary>
    public ApkContentRole Roles { get; }

    /// <summary>Gets canonical distinct ABI directory names found below lib/.</summary>
    public ReadOnlyCollection<string> NativeAbis { get; }

    /// <summary>Gets canonical distinct ABIs encoded by modern AssemblyStore paths.</summary>
    public ReadOnlyCollection<string> ModernAssemblyStoreAbis { get; }

    /// <summary>Returns whether all requested roles are present.</summary>
    public bool Contains(ApkContentRole role) => (Roles & role) == role;

    /// <summary>Builds an inventory from ZIP entry names without opening payload entries.</summary>
    public static ApkEntryInventory Classify(IEnumerable<string?> entryNames)
    {
        ArgumentNullException.ThrowIfNull(entryNames);

        var roles = ApkContentRole.None;
        var nativeAbis = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var modernAbis = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entryName in entryNames)
        {
            if (entryName is null)
            {
                continue;
            }

            roles |= ApkEntryRoleClassifier.Classify(entryName);
            var normalizedPath = entryName.Replace('\\', '/');
            if (TryReadNativeAbi(normalizedPath, out var nativeAbi))
            {
                nativeAbis.Add(nativeAbi.ToLowerInvariant());
            }

            if (AssemblyStoreApkPath.TryParse(normalizedPath, out var modernAbi))
            {
                modernAbis.Add(modernAbi.ToLowerInvariant());
            }
        }

        return new ApkEntryInventory(roles, nativeAbis, modernAbis);
    }

    private static bool TryReadNativeAbi(string? entryName, out string abi)
    {
        abi = string.Empty;
        if (string.IsNullOrEmpty(entryName) || entryName.IndexOf('\0') >= 0)
        {
            return false;
        }

        var segments = entryName.Split('/', StringSplitOptions.None);
        if (segments.Length < 3 ||
            !segments[0].Equals("lib", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(segments[1]) ||
            string.IsNullOrWhiteSpace(segments[2]) ||
            segments[1] is "." or "..")
        {
            return false;
        }

        abi = segments[1];
        return true;
    }
}

/// <summary>Recognizes content roles from one normalized APK ZIP entry path.</summary>
public static class ApkEntryRoleClassifier
{
    private const string ContentPrefix = "assets/Content/";

    public static ApkContentRole Classify(string? entryName)
    {
        if (string.IsNullOrEmpty(entryName) || entryName.IndexOf('\0') >= 0)
        {
            return ApkContentRole.None;
        }

        var path = entryName.Replace('\\', '/');
        if (path.StartsWith(ContentPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return ApkContentRole.GameContent;
        }

        var segments = path.Split('/', StringSplitOptions.None);
        if (segments.Length == 2 &&
            segments[0].Equals("assemblies", StringComparison.OrdinalIgnoreCase) &&
            segments[1].Length > ".blob".Length &&
            segments[1].EndsWith(".blob", StringComparison.OrdinalIgnoreCase))
        {
            return ApkContentRole.LegacyAssemblyBlob;
        }

        if (AssemblyStoreApkPath.TryParse(path, out _))
        {
            return ApkContentRole.ModernAssemblyBlob;
        }

        return ApkContentRole.None;
    }
}
