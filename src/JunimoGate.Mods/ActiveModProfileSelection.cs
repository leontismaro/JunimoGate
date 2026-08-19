using System.Collections.Concurrent;
using System.Text.Json;

namespace JunimoGate.Mods;

public sealed record ActiveModProfileSelection(
    string Schema,
    long Revision,
    string ActiveProfileId,
    DateTimeOffset UpdatedAtUtc)
{
    public const string CurrentSchema = "junimogate-active-mod-profile/v1";

    public ProfileId Validate()
    {
        if (Schema != CurrentSchema || Revision < 1 ||
            !ProfileId.TryParse(ActiveProfileId, out var profileId) || UpdatedAtUtc == default)
        {
            throw new InvalidDataException("The active Mod Profile selection is malformed.");
        }
        return profileId;
    }
}

public sealed class ActiveModProfileSelectionRepository
{
    private const int MaximumSelectionBytes = 16 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.General)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> OperationLocks = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly string path;
    private readonly SemaphoreSlim operationLock;
    private readonly RepositoryChangeSignal changeSignal;
    public event Action? Changed
    {
        add => changeSignal.Changed += value;
        remove => changeSignal.Changed -= value;
    }

    public ActiveModProfileSelectionRepository(string profilesRoot)
    {
        if (string.IsNullOrWhiteSpace(profilesRoot) || !Path.IsPathFullyQualified(profilesRoot))
            throw new ArgumentException("The profiles root must be absolute.", nameof(profilesRoot));
        ProfilesRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(profilesRoot));
        path = Path.Combine(ProfilesRoot, "active-profile.json");
        operationLock = OperationLocks.GetOrAdd(ProfilesRoot, static _ => new SemaphoreSlim(1, 1));
        changeSignal = ModRepositoryChangeSignals.ActiveProfiles.GetOrAdd(ProfilesRoot, static _ => new RepositoryChangeSignal());
    }

    internal string ProfilesRoot { get; }

    public async ValueTask<ActiveModProfileSelection> OpenOrCreateAsync(
        ProfileId fallbackProfileId,
        CancellationToken cancellationToken = default)
    {
        await operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (File.Exists(path))
                return await ReadUnlockedAsync(cancellationToken).ConfigureAwait(false);
            var selection = new ActiveModProfileSelection(
                ActiveModProfileSelection.CurrentSchema,
                Revision: 1,
                fallbackProfileId.Value,
                DateTimeOffset.UtcNow);
            try
            {
                await WriteAtomicAsync(selection, overwrite: false, cancellationToken).ConfigureAwait(false);
                return selection;
            }
            catch (IOException) when (File.Exists(path))
            {
                return await ReadUnlockedAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            operationLock.Release();
        }
    }

    public async ValueTask<ActiveModProfileSelection> ReadAsync(
        CancellationToken cancellationToken = default)
    {
        await operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ReadUnlockedAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            operationLock.Release();
        }
    }

    internal async ValueTask<ActiveModProfileSelection> SetAsync(
        long expectedRevision,
        ProfileId profileId,
        CancellationToken cancellationToken = default)
    {
        await operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await ReadUnlockedAsync(cancellationToken).ConfigureAwait(false);
            if (current.Revision != expectedRevision)
                throw new InvalidOperationException("The active Mod Profile changed before the update could be saved.");
            if (current.ActiveProfileId == profileId.Value)
                return current;
            var updated = current with
            {
                Revision = checked(current.Revision + 1),
                ActiveProfileId = profileId.Value,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            };
            await WriteAtomicAsync(updated, overwrite: true, cancellationToken).ConfigureAwait(false);
            changeSignal.Publish();
            return updated;
        }
        finally
        {
            operationLock.Release();
        }
    }

    private async ValueTask<ActiveModProfileSelection> ReadUnlockedAsync(CancellationToken cancellationToken)
    {
        var file = new FileInfo(path);
        if (!file.Exists)
            throw new FileNotFoundException("The active Mod Profile selection does not exist.", path);
        if (file.Length is < 1 or > MaximumSelectionBytes)
            throw new InvalidDataException("The active Mod Profile selection has an invalid size.");
        try
        {
            var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            var selection = JsonSerializer.Deserialize<ActiveModProfileSelection>(bytes, JsonOptions)
                ?? throw new InvalidDataException("The active Mod Profile selection is empty.");
            selection.Validate();
            return selection;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The active Mod Profile selection JSON is malformed.", exception);
        }
    }

    private async ValueTask WriteAtomicAsync(
        ActiveModProfileSelection selection,
        bool overwrite,
        CancellationToken cancellationToken)
    {
        selection.Validate();
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(
                             temporary,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             8 * 1024,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await JsonSerializer.SerializeAsync(stream, selection, JsonOptions, cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite);
        }
        finally
        {
            try
            {
                File.Delete(temporary);
            }
            catch (IOException)
            {
                // Best-effort cleanup; the committed selection is authoritative.
            }
        }
    }
}
