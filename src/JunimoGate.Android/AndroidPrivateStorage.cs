using Android.Content;

namespace JunimoGate.Android;

/// <summary>
/// Owns JunimoGate private runtime/report storage outside the host package FilesDir scanned by the
/// game SDK. Only the two historical JunimoGate-owned directories may be migrated.
/// </summary>
public static class AndroidPrivateStorage
{
    private const string RootDirectoryName = "junimogate";
    private const string RuntimeDirectoryName = "runtime";
    private const string ReportsDirectoryName = "reports";
    private const string MigrationMarkerName = ".storage-layout-v1";

    private static readonly SemaphoreSlim MigrationLock = new(1, 1);

    public static async ValueTask EnsureMigratedAsync(
        Context context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var safeContext = context.ApplicationContext ?? context;
        var filesPath = safeContext.FilesDir?.AbsolutePath;
        var noBackupPath = safeContext.NoBackupFilesDir?.AbsolutePath;
        if (string.IsNullOrWhiteSpace(filesPath) || string.IsNullOrWhiteSpace(noBackupPath))
        {
            throw new IOException("The application private storage roots are unavailable.");
        }

        await MigrationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var root = Path.Combine(noBackupPath, RootDirectoryName);
            Directory.CreateDirectory(root);
            RejectReparsePoint(noBackupPath);
            RejectReparsePoint(root);

            MigrateOwnedDirectory(filesPath, root, RuntimeDirectoryName);
            MigrateOwnedDirectory(filesPath, root, ReportsDirectoryName);

            var marker = Path.Combine(root, MigrationMarkerName);
            if (!File.Exists(marker))
            {
                var temporary = Path.Combine(root, $".{MigrationMarkerName}.{Guid.NewGuid():N}.tmp");
                try
                {
                    await File.WriteAllTextAsync(temporary, "junimogate-private-storage-v1\n", cancellationToken)
                        .ConfigureAwait(false);
                    File.Move(temporary, marker, overwrite: false);
                }
                finally
                {
                    if (File.Exists(temporary))
                    {
                        File.Delete(temporary);
                    }
                }
            }
        }
        finally
        {
            MigrationLock.Release();
        }
    }

    public static string GetRuntimeRoot(Context context) =>
        GetOwnedDirectory(context, RuntimeDirectoryName);

    public static string GetReportsRoot(Context context) =>
        GetOwnedDirectory(context, ReportsDirectoryName);

    private static string GetOwnedDirectory(Context context, string directoryName)
    {
        ArgumentNullException.ThrowIfNull(context);
        var safeContext = context.ApplicationContext ?? context;
        var noBackupPath = safeContext.NoBackupFilesDir?.AbsolutePath;
        if (string.IsNullOrWhiteSpace(noBackupPath))
        {
            throw new IOException("The application no-backup storage root is unavailable.");
        }

        var root = Path.Combine(noBackupPath, RootDirectoryName);
        var result = Path.Combine(root, directoryName);
        Directory.CreateDirectory(result);
        RejectReparsePoint(noBackupPath);
        RejectReparsePoint(root);
        RejectReparsePoint(result);
        return result;
    }

    private static void MigrateOwnedDirectory(string legacyRoot, string newRoot, string directoryName)
    {
        var source = Path.Combine(legacyRoot, directoryName);
        if (!Directory.Exists(source))
        {
            return;
        }

        RejectReparsePoint(legacyRoot);
        RejectReparsePoint(source);
        var destination = Path.Combine(newRoot, directoryName);
        if (Directory.Exists(destination))
        {
            RejectReparsePoint(destination);
            if (Directory.EnumerateFileSystemEntries(source).Any() ||
                Directory.EnumerateFileSystemEntries(destination).Any())
            {
                throw new IOException("Both legacy and current JunimoGate storage contain data; migration is ambiguous.");
            }

            Directory.Delete(source);
            return;
        }

        Directory.Move(source, destination);
    }

    private static void RejectReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("JunimoGate private storage cannot traverse a reparse point.");
        }
    }
}
