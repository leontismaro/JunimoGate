namespace JunimoGate.Extraction;

public interface IApkInventoryReader
{
    ValueTask<ApkEntryInventory> ReadAsync(Stream apkStream, CancellationToken cancellationToken = default);
}

public enum ExtractionTransactionState
{
    Created,
    Writing,
    Validated,
    Committed,
    RolledBack,
    Failed,
}

/// <summary>Boundary for a future atomic staging transaction; no commercial APK extraction is implemented in V1 scaffold.</summary>
public interface IExtractionTransaction : IAsyncDisposable
{
    string StagingDirectory { get; }

    ExtractionTransactionState State { get; }

    ValueTask MarkValidatedAsync(CancellationToken cancellationToken = default);

    ValueTask CommitAsync(CancellationToken cancellationToken = default);

    ValueTask RollbackAsync(CancellationToken cancellationToken = default);
}
