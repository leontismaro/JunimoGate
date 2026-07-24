namespace JunimoGate.Extraction;

[Flags]
public enum ApkContentRole
{
    None = 0,
    GameContent = 1,
    LegacyAssemblyBlob = 2,
    ModernAssemblyBlob = 4,
}

/// <summary>Classifies APK roles solely from normalized ZIP entry paths.</summary>
public sealed class ApkEntryInventory
{
    private ApkEntryInventory(ApkContentRole roles)
    {
        Roles = roles;
    }

    public ApkContentRole Roles { get; }

    public bool Contains(ApkContentRole role) => (Roles & role) == role;

    public static ApkEntryInventory Classify(IEnumerable<string> entryNames)
    {
        ArgumentNullException.ThrowIfNull(entryNames);

        var roles = ApkContentRole.None;
        foreach (var entryName in entryNames)
        {
            roles |= ApkEntryRoleClassifier.Classify(entryName);
        }

        return new ApkEntryInventory(roles);
    }
}

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
