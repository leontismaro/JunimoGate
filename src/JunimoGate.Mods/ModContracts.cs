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

/// <summary>ZIP installer transaction boundary. Implementations pre-scan before writing.</summary>
public interface IModArchiveInstallTransaction : IAsyncDisposable
{
    ModInstallTransactionState State { get; }

    ModArchiveScanResult? ScanResult { get; }

    ModArchiveImportResult? ImportResult { get; }

    ValueTask ScanAsync(Stream archive, CancellationToken cancellationToken = default);

    ValueTask CommitAsync(CancellationToken cancellationToken = default);

    ValueTask RollbackAsync(CancellationToken cancellationToken = default);
}
