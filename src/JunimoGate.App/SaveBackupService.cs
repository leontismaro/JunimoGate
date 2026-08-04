using Android.Content;
using JunimoGate.Android;
using JunimoGate.Core;

namespace JunimoGate.App;

internal sealed record SaveBackupOverview(
    IReadOnlyList<SaveBackupEntry> Backups,
    int UnavailableBackupCount,
    int LiveSaveCount,
    string? LatestSaveName,
    DateTimeOffset? LatestSaveTimeUtc);

internal sealed class SaveBackupService
{
    private readonly Context context;

    public SaveBackupService(Context context)
    {
        ArgumentNullException.ThrowIfNull(context);
        this.context = context.ApplicationContext ?? context;
    }

    public SaveBackupOverview Read()
    {
        var savesRoot = AndroidPrivateStorage.GetGameSaveRoot(context);
        var liveSaves = Directory.Exists(savesRoot)
            ? Directory.EnumerateDirectories(savesRoot, "*", SearchOption.TopDirectoryOnly)
                .Select(static path => new DirectoryInfo(path))
                .Where(static directory => (directory.Attributes & FileAttributes.ReparsePoint) == 0)
                .OrderByDescending(static directory => directory.LastWriteTimeUtc)
                .ToArray()
            : [];
        var catalog = CreateCatalog().Read();
        return new SaveBackupOverview(
            catalog.Entries,
            catalog.UnavailableEntryCount,
            liveSaves.Length,
            liveSaves.FirstOrDefault()?.Name,
            liveSaves.Length == 0
                ? null
                : new DateTimeOffset(liveSaves[0].LastWriteTimeUtc, TimeSpan.Zero));
    }

    public ValueTask ExportAsync(
        string fileName,
        Stream destination,
        CancellationToken cancellationToken) =>
        CreateCatalog().ExportAsync(fileName, destination, cancellationToken);

    private SaveBackupCatalog CreateCatalog() => new(Path.Combine(
        AndroidPrivateStorage.GetUserDataRoot(context),
        "save-backups"));
}
