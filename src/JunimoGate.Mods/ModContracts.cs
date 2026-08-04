namespace JunimoGate.Mods;

public enum ModInstallTransactionState
{
    Created,
    Scanning,
    AwaitingConfirmation,
    ExtractingToStaging,
    Validated,
    Committed,
    RolledBack,
    Failed,
}

public enum DependencyResolutionStatus
{
    Compatible,
    MissingDependency,
    VersionConflict,
    CycleDetected,
    InvalidManifest,
}

public sealed record DependencyResolution(
    DependencyResolutionStatus Status,
    IReadOnlyList<string> OrderedModIds,
    IReadOnlyList<string> Messages);

/// <summary>Future ZIP installer transaction boundary. Implementations must pre-scan before writing.</summary>
public interface IModArchiveInstallTransaction : IAsyncDisposable
{
    ModInstallTransactionState State { get; }

    ModArchiveScanResult? ScanResult { get; }

    ModArchiveImportResult? ImportResult { get; }

    ValueTask ScanAsync(Stream archive, CancellationToken cancellationToken = default);

    ValueTask CommitAsync(CancellationToken cancellationToken = default);

    ValueTask RollbackAsync(CancellationToken cancellationToken = default);
}

/// <summary>Future dependency graph boundary; no resolver is claimed by this scaffold.</summary>
public interface IModDependencyGraph
{
    DependencyResolution Resolve(IEnumerable<string> selectedModIds);
}
