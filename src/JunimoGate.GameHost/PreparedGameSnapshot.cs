namespace JunimoGate.GameHost;

public sealed record PreparedManagedAssembly(string SimpleName, string RelativePath, long Size);

public sealed record PreparedContentFile(string RelativePath, long Size);

public sealed partial record PreparedGameSnapshot(
    string Schema,
    string CompatibilityRuleId,
    string PackageName,
    string VersionName,
    long VersionCode,
    string Abi,
    string PackageMarker,
    string SourceWorkspaceKey,
    string SourceWorkspacePath,
    string AppliedWorkspaceKey,
    string AppliedWorkspacePath,
    string OverlayAssemblyPath,
    long OverlayAssemblySize,
    string ConfigDirectory,
    string LogDirectory,
    string SaveDirectory,
    string BackupDirectory,
    IReadOnlyList<PreparedManagedAssembly> ManagedAssemblies,
    IReadOnlyList<PreparedContentFile> ContentFiles,
    DateTimeOffset PreparedAtUtc);
