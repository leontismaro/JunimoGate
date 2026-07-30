using Android.Content;

namespace JunimoGate.Android;

/// <summary>
/// Owns JunimoGate runtime, user-data, report, and live-save storage. Migrations only touch
/// JunimoGate-owned historical directories.
/// </summary>
public static class AndroidPrivateStorage
{
    private const string RootDirectoryName = "junimogate";
    private const string RuntimeDirectoryName = "runtime";
    private const string ReportsDirectoryName = "reports";
    private const string ProductLogsDirectoryName = "product-logs";
    private const string UserDataDirectoryName = "user-data";
    private const string LegacyMigrationMarkerName = ".storage-layout-v1";
    private const string UserDataMigrationMarkerName = ".storage-layout-v2";

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

            await EnsureMarkerAsync(
                    root,
                    LegacyMigrationMarkerName,
                    "junimogate-private-storage-v1\n",
                    cancellationToken)
                .ConfigureAwait(false);

            var userDataRoot = Path.Combine(root, UserDataDirectoryName);
            Directory.CreateDirectory(userDataRoot);
            RejectReparsePoint(userDataRoot);
            var legacySmapiRoot = Path.Combine(root, RuntimeDirectoryName, "smapi");
            if (Directory.Exists(legacySmapiRoot))
            {
                RejectReparsePoint(legacySmapiRoot);
                var migrations = new[]
                {
                    new DirectoryMigration(
                        Path.Combine(legacySmapiRoot, "profiles"),
                        Path.Combine(userDataRoot, "profiles")),
                    new DirectoryMigration(
                        Path.Combine(legacySmapiRoot, "config"),
                        Path.Combine(userDataRoot, "config")),
                    new DirectoryMigration(
                        Path.Combine(legacySmapiRoot, "logs"),
                        Path.Combine(userDataRoot, "logs")),
                    new DirectoryMigration(
                        Path.Combine(legacySmapiRoot, "save-backups"),
                        Path.Combine(userDataRoot, "save-backups")),
                    new DirectoryMigration(
                        Path.Combine(legacySmapiRoot, "saves"),
                        GetGameSaveRoot(safeContext),
                        AllowCrossVolumeCopy: true),
                };

                // Check every user-data destination before moving anything so a conflict can't leave
                // the v2 migration half-applied. Live saves may cross Android storage volumes.
                foreach (var migration in migrations)
                    ValidateMigration(migration.Source, migration.Destination);
                foreach (var migration in migrations)
                    MigrateDirectory(
                        migration.Source,
                        migration.Destination,
                        migration.AllowCrossVolumeCopy);
            }

            await EnsureMarkerAsync(
                    root,
                    UserDataMigrationMarkerName,
                    "junimogate-user-data-layout-v2\n",
                    cancellationToken)
                .ConfigureAwait(false);
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

    public static string GetProductLogsRoot(Context context) =>
        GetOwnedDirectory(context, ProductLogsDirectoryName);

    public static string GetUserDataRoot(Context context) =>
        GetOwnedDirectory(context, UserDataDirectoryName);

    public static string GetGameSaveRoot(Context context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var safeContext = context.ApplicationContext ?? context;
        // Stardew Valley's Android runner uses the app-specific external Files directory. Never
        // silently fall back to internal storage, since that would split the game's live saves from
        // SMAPI's save APIs and backups.
        var storage = safeContext.GetExternalFilesDir(null);
        if (storage is null)
            throw new IOException("The application external save storage root is unavailable.");

        string storagePath;
        try
        {
            storagePath = storage.CanonicalPath;
        }
        catch (Java.IO.IOException)
        {
            storagePath = storage.AbsolutePath;
        }

        if (string.IsNullOrWhiteSpace(storagePath))
            throw new IOException("The application external save storage root is unavailable.");
        var result = Path.GetFullPath(Path.Combine(storagePath, "Saves"));
        Directory.CreateDirectory(result);
        RejectReparsePoint(result);
        return result;
    }

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
        var destination = Path.Combine(newRoot, directoryName);
        MigrateDirectory(source, destination);
    }

    private static void MigrateDirectory(
        string source,
        string destination,
        bool allowCrossVolumeCopy = false)
    {
        if (!Directory.Exists(source))
            return;

        ValidateMigration(source, destination);
        if (!Directory.EnumerateFileSystemEntries(source).Any())
        {
            Directory.Delete(source);
            Directory.CreateDirectory(destination);
            return;
        }

        if (Directory.Exists(destination))
            Directory.Delete(destination);

        try
        {
            Directory.Move(source, destination);
        }
        catch (IOException) when (allowCrossVolumeCopy)
        {
            CopyDirectoryAcrossVolumes(source, destination);
        }
    }

    private static void ValidateMigration(string source, string destination)
    {
        if (!Directory.Exists(source))
            return;

        RejectReparsePoint(source);
        if (File.Exists(destination))
            throw new IOException("A file blocks a JunimoGate storage migration destination.");

        var destinationParent = Path.GetDirectoryName(destination)
            ?? throw new IOException("The migration destination has no parent directory.");
        Directory.CreateDirectory(destinationParent);
        RejectReparsePoint(destinationParent);
        if (Directory.Exists(destination))
        {
            RejectReparsePoint(destination);
            if (Directory.EnumerateFileSystemEntries(source).Any() &&
                Directory.EnumerateFileSystemEntries(destination).Any())
            {
                throw new IOException("Both legacy and current JunimoGate storage contain data; migration is ambiguous.");
            }
        }
    }

    private static void CopyDirectoryAcrossVolumes(string source, string destination)
    {
        var destinationParent = Path.GetDirectoryName(destination)
            ?? throw new IOException("The migration destination has no parent directory.");
        var temporary = Path.Combine(
            destinationParent,
            $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.migration");
        try
        {
            CopyDirectoryContents(source, temporary);
            Directory.Move(temporary, destination);
            Directory.Delete(source, recursive: true);
        }
        finally
        {
            if (Directory.Exists(temporary))
                Directory.Delete(temporary, recursive: true);
        }
    }

    private static void CopyDirectoryContents(string source, string destination)
    {
        RejectReparsePoint(source);
        Directory.CreateDirectory(destination);
        foreach (var entry in new DirectoryInfo(source).EnumerateFileSystemInfos())
        {
            RejectReparsePoint(entry.FullName);
            var target = Path.Combine(destination, entry.Name);
            switch (entry)
            {
                case DirectoryInfo directory:
                    CopyDirectoryContents(directory.FullName, target);
                    break;
                case FileInfo file:
                    file.CopyTo(target, overwrite: false);
                    break;
                default:
                    throw new IOException("JunimoGate storage contains an unsupported filesystem entry.");
            }
        }
    }

    private sealed record DirectoryMigration(
        string Source,
        string Destination,
        bool AllowCrossVolumeCopy = false);

    private static async Task EnsureMarkerAsync(
        string root,
        string markerName,
        string contents,
        CancellationToken cancellationToken)
    {
        var marker = Path.Combine(root, markerName);
        if (File.Exists(marker))
            return;

        var temporary = Path.Combine(root, $".{markerName}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(temporary, contents, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, marker, overwrite: false);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private static void RejectReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("JunimoGate private storage cannot traverse a reparse point.");
        }
    }
}
