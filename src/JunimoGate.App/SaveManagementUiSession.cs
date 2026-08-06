using Android.Content;
using JunimoGate.Android;
using JunimoGate.Core;
using JunimoGate.GameHost;

namespace JunimoGate.App;

internal sealed record SaveManagementOverview(
    IReadOnlyList<LiveSaveGameEntry> Saves,
    SaveBackupCatalogSnapshot Backups);

internal sealed class SaveManagementUiSession : IDisposable
{
    private readonly Context context;
    private readonly string savesRoot;
    private readonly string backupRoot;
    private readonly string transferRoot;
    private readonly object cacheLock = new();
    private Task<SaveManagementOverview>? cached;
    private bool disposed;

    public SaveManagementUiSession(Context context)
    {
        ArgumentNullException.ThrowIfNull(context);
        this.context = context.ApplicationContext ?? context;
        savesRoot = AndroidPrivateStorage.GetGameSaveRoot(this.context);
        backupRoot = Path.Combine(AndroidPrivateStorage.GetUserDataRoot(this.context), "save-backups");
        var external = this.context.GetExternalFilesDir(null)?.AbsolutePath
            ?? throw new IOException("The external files directory is unavailable.");
        transferRoot = Path.Combine(external, ".save-transfer");
        Directory.CreateDirectory(transferRoot);
        CleanupStaleTransfers();
    }

    public event Action? Changed;

    public bool IsGameRunning => GameSessionRegistry.IsGameProcessActive(context);

    public async ValueTask<SaveManagementOverview> GetOverviewAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        Task<SaveManagementOverview> load;
        lock (cacheLock)
            load = cached ??= Task.Run(() => new SaveManagementOverview(
                LiveSaveGameCatalog.Read(savesRoot),
                new SaveBackupCatalog(backupRoot).Read()));
        try
        {
            return await load.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch when (load.IsCompleted && !load.IsCompletedSuccessfully)
        {
            lock (cacheLock)
            {
                if (ReferenceEquals(cached, load))
                    cached = null;
            }
            throw;
        }
    }

    public void Invalidate()
    {
        lock (cacheLock)
            cached = null;
        Changed?.Invoke();
    }

    public async ValueTask<string> StageDocumentAsync(
        global::Android.Net.Uri uri,
        IProgress<SaveTransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(uri);
        var path = Path.Combine(transferRoot, $"document-{Guid.NewGuid():N}.zip");
        try
        {
            await using var input = context.ContentResolver?.OpenInputStream(uri)
                ?? throw new IOException("The selected save archive could not be opened.");
            await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, true);
            var buffer = new byte[128 * 1024];
            long processed = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                processed += read;
                if (processed > 2L * 1024 * 1024 * 1024)
                    throw new InvalidDataException("The selected save archive is too large.");
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                progress?.Report(new SaveTransferProgress(processed, 0));
            }
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            return path;
        }
        catch
        {
            TryDelete(path);
            throw;
        }
    }

    public async ValueTask<string> StageBackupAsync(
        SaveBackupEntry backup,
        IProgress<SaveTransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var path = Path.Combine(transferRoot, $"backup-{Guid.NewGuid():N}.zip");
        try
        {
            await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, true);
            await new SaveBackupCatalog(backupRoot)
                .ExportAsync(backup.FileName, output, cancellationToken)
                .ConfigureAwait(false);
            progress?.Report(new SaveTransferProgress(backup.Size, backup.Size));
            return path;
        }
        catch
        {
            TryDelete(path);
            throw;
        }
    }

    public SaveArchiveInspection InspectArchive(string path)
    {
        ThrowIfDisposed();
        return SaveArchiveInspector.InspectZip(ValidateTransferPath(path));
    }

    public async ValueTask<SaveImportResult> ImportAsync(
        string archivePath,
        IReadOnlyList<SaveImportSelection> selections,
        IProgress<SaveTransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (IsGameRunning)
            throw new InvalidOperationException("The game is running.");
        var result = await new SaveImportTransaction(savesRoot, transferRoot, backupRoot)
            .ImportAsync(ValidateTransferPath(archivePath), selections, progress, cancellationToken)
            .ConfigureAwait(false);
        Invalidate();
        return result;
    }

    public Task ExportSaveAsync(
        LiveSaveGameEntry save,
        Stream destination,
        IProgress<SaveTransferProgress>? progress,
        CancellationToken cancellationToken) =>
        SaveArchiveWriter.ExportSaveAsync(savesRoot, save.DirectoryName, destination, progress, cancellationToken);

    public ValueTask ExportBackupAsync(
        SaveBackupEntry backup,
        Stream destination,
        CancellationToken cancellationToken) =>
        new SaveBackupCatalog(backupRoot).ExportAsync(backup.FileName, destination, cancellationToken);

    public void DeleteStagedArchive(string path)
    {
        try
        {
            TryDelete(ValidateTransferPath(path));
        }
        catch (InvalidDataException)
        {
        }
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
    }

    private string ValidateTransferPath(string path)
    {
        var full = Path.GetFullPath(path);
        if (!full.StartsWith(transferRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidDataException("The staged save archive path is invalid.");
        return full;
    }

    private void CleanupStaleTransfers()
    {
        foreach (var path in Directory.EnumerateFileSystemEntries(transferRoot, "*", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var updated = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : Directory.GetLastWriteTimeUtc(path);
                if (updated < DateTime.UtcNow.AddDays(-1))
                {
                    if (Directory.Exists(path))
                        Directory.Delete(path, recursive: true);
                    else
                        File.Delete(path);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(disposed, this);
}
