using System.Text.Json;
using System.Text.Json.Serialization;

namespace JunimoGate.Mods;

public sealed record ModProfileMember(
    string UniqueId,
    string? LibraryItemId,
    bool Enabled,
    string ExpectedName,
    string ExpectedVersion,
    string? ExpectedAuthor,
    DateTimeOffset AddedAtUtc)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(UniqueId) || UniqueId.Length > 256 ||
            LibraryItemId is not null && !ModLibraryItemId.IsValid(LibraryItemId) ||
            string.IsNullOrWhiteSpace(ExpectedName) || ExpectedName.Length > 256 ||
            string.IsNullOrWhiteSpace(ExpectedVersion) || ExpectedVersion.Length > 128 ||
            ExpectedAuthor?.Length > 256 || AddedAtUtc == default)
        {
            throw new InvalidDataException("The Mod Profile member is malformed.");
        }
    }

    public static ModProfileMember FromLibraryItem(ModLibraryItem item, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(item);
        item.Validate();
        return new ModProfileMember(
            item.Manifest.UniqueId,
            item.LibraryItemId,
            enabled,
            item.Manifest.Name,
            item.Manifest.Version,
            item.Manifest.Author,
            DateTimeOffset.UtcNow);
    }
}

public sealed record ModProfileV2(
    string Schema,
    string Id,
    string DisplayName,
    long Revision,
    ModAssemblyBindingPolicy? AssemblyBindingPolicyOverride,
    IReadOnlyList<ModProfileMember> Members,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    string? Description)
{
    public const string CurrentSchema = "junimogate-mod-profile/v2";
    public const string NoModsId = "no-mods";
    public const int MaximumMembers = 4_096;

    public ProfileId Validate()
    {
        if (Schema != CurrentSchema || !ProfileId.TryParse(Id, out var profileId) ||
            string.IsNullOrWhiteSpace(DisplayName) || DisplayName.Length > 80 || Revision < 1 ||
            AssemblyBindingPolicyOverride is { } policy && !Enum.IsDefined(policy) ||
            Members is null || Members.Count > MaximumMembers || CreatedAtUtc == default ||
            UpdatedAtUtc < CreatedAtUtc || Description?.Length > 1_024)
        {
            throw new InvalidDataException("The Mod Profile v2 document is malformed.");
        }

        var uniqueIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var member in Members)
        {
            member?.Validate();
            if (member is null || !uniqueIds.Add(member.UniqueId))
                throw new InvalidDataException("The Mod Profile contains a duplicate or null member.");
        }

        if (Id == NoModsId && (Members.Count != 0 || AssemblyBindingPolicyOverride is not null))
            throw new InvalidDataException("The no-Mod Profile must remain empty and use the global binding policy.");
        return profileId;
    }
}

public sealed class ModProfileV2Repository
{
    private const int MaximumProfileBytes = 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.General)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };
    private readonly string profilesRoot;
    private readonly SemaphoreSlim operationLock = new(1, 1);
    public event Action? Changed;

    public ModProfileV2Repository(string profilesRoot)
    {
        if (string.IsNullOrWhiteSpace(profilesRoot) || !Path.IsPathFullyQualified(profilesRoot))
            throw new ArgumentException("The profiles root must be absolute.", nameof(profilesRoot));
        this.profilesRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(profilesRoot));
    }

    public async ValueTask<IReadOnlyList<ModProfileV2>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        await operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(profilesRoot);
            await EnsureNoModsUnlockedAsync(cancellationToken).ConfigureAwait(false);
            var profiles = new List<ModProfileV2>();
            foreach (var directory in Directory.EnumerateDirectories(profilesRoot))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name = Path.GetFileName(directory);
                if (!ProfileId.TryParse(name, out var profileId))
                    continue;
                var profile = await TryReadV2UnlockedAsync(profileId, cancellationToken).ConfigureAwait(false);
                if (profile is not null)
                    profiles.Add(profile);
            }

            return profiles
                .OrderBy(profile => profile.Id == ModProfileV2.NoModsId ? 0 : 1)
                .ThenBy(profile => profile.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(profile => profile.Id, StringComparer.Ordinal)
                .ToArray();
        }
        finally
        {
            operationLock.Release();
        }
    }

    public async ValueTask<ModProfileV2> ReadAsync(
        ProfileId profileId,
        CancellationToken cancellationToken = default)
    {
        await operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ReadV2UnlockedAsync(profileId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            operationLock.Release();
        }
    }

    public async ValueTask<ModProfileV2> CreateAsync(
        string displayName,
        string? description = null,
        ModAssemblyBindingPolicy? bindingPolicyOverride = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedName = NormalizeDisplayName(displayName);
        var normalizedDescription = NormalizeDescription(description);
        if (bindingPolicyOverride is { } policy && !Enum.IsDefined(policy))
            throw new ArgumentOutOfRangeException(nameof(bindingPolicyOverride));

        await operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(profilesRoot);
            await EnsureNoModsUnlockedAsync(cancellationToken).ConfigureAwait(false);
            while (true)
            {
                var profileId = ProfileId.Parse($"group-{Guid.NewGuid():N}"[..30]);
                var directory = GetProfileDirectory(profileId);
                if (Directory.Exists(directory))
                    continue;
                Directory.CreateDirectory(directory);
                var now = DateTimeOffset.UtcNow;
                var profile = new ModProfileV2(
                    ModProfileV2.CurrentSchema,
                    profileId.Value,
                    normalizedName,
                    Revision: 1,
                    bindingPolicyOverride,
                    Array.Empty<ModProfileMember>(),
                    now,
                    now,
                    normalizedDescription);
                try
                {
                    await WriteAtomicAsync(GetProfilePath(profileId), profile, overwrite: false, cancellationToken)
                        .ConfigureAwait(false);
                    Changed?.Invoke();
                    return profile;
                }
                catch
                {
                    TryDeleteDirectory(directory);
                    throw;
                }
            }
        }
        finally
        {
            operationLock.Release();
        }
    }

    public async ValueTask<ModProfileV2> OpenOrCreateDefaultAsync(
        string displayName = "Default",
        CancellationToken cancellationToken = default)
    {
        var profileId = ProfileId.Parse("default");
        var normalizedName = NormalizeDisplayName(displayName);
        await operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(profilesRoot);
            await EnsureNoModsUnlockedAsync(cancellationToken).ConfigureAwait(false);
            var path = GetProfilePath(profileId);
            if (File.Exists(path))
                return await ReadV2UnlockedAsync(profileId, cancellationToken).ConfigureAwait(false);
            Directory.CreateDirectory(GetProfileDirectory(profileId));
            var now = DateTimeOffset.UtcNow;
            var profile = new ModProfileV2(
                ModProfileV2.CurrentSchema,
                profileId.Value,
                normalizedName,
                1,
                null,
                Array.Empty<ModProfileMember>(),
                now,
                now,
                null);
            await WriteAtomicAsync(path, profile, overwrite: false, cancellationToken).ConfigureAwait(false);
            Changed?.Invoke();
            return profile;
        }
        finally
        {
            operationLock.Release();
        }
    }

    public async ValueTask<ModProfileV2> CreateImportedAsync(
        string displayName,
        string? description,
        ModAssemblyBindingPolicy? bindingPolicyOverride,
        IReadOnlyList<ModProfileMember> members,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(members);
        var normalizedName = NormalizeDisplayName(displayName);
        var normalizedDescription = NormalizeDescription(description);
        if (bindingPolicyOverride is { } policy && !Enum.IsDefined(policy))
            throw new ArgumentOutOfRangeException(nameof(bindingPolicyOverride));

        await operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(profilesRoot);
            await EnsureNoModsUnlockedAsync(cancellationToken).ConfigureAwait(false);
            while (true)
            {
                var profileId = ProfileId.Parse($"group-{Guid.NewGuid():N}"[..30]);
                var directory = GetProfileDirectory(profileId);
                if (Directory.Exists(directory))
                    continue;
                Directory.CreateDirectory(directory);
                var now = DateTimeOffset.UtcNow;
                var profile = new ModProfileV2(
                    ModProfileV2.CurrentSchema,
                    profileId.Value,
                    normalizedName,
                    Revision: 1,
                    bindingPolicyOverride,
                    members.ToArray(),
                    now,
                    now,
                    normalizedDescription);
                try
                {
                    profile.Validate();
                    await WriteAtomicAsync(GetProfilePath(profileId), profile, overwrite: false, cancellationToken)
                        .ConfigureAwait(false);
                    Changed?.Invoke();
                    return profile;
                }
                catch
                {
                    TryDeleteDirectory(directory);
                    throw;
                }
            }
        }
        finally
        {
            operationLock.Release();
        }
    }

    public async ValueTask<ModProfileV2> UpdateAsync(
        ProfileId profileId,
        long expectedRevision,
        string displayName,
        string? description,
        ModAssemblyBindingPolicy? bindingPolicyOverride,
        IReadOnlyList<ModProfileMember> members,
        CancellationToken cancellationToken = default)
    {
        if (profileId.Value == ModProfileV2.NoModsId)
            throw new InvalidOperationException("The no-Mod Profile cannot be edited.");
        ArgumentNullException.ThrowIfNull(members);
        var normalizedName = NormalizeDisplayName(displayName);
        var normalizedDescription = NormalizeDescription(description);
        if (bindingPolicyOverride is { } policy && !Enum.IsDefined(policy))
            throw new ArgumentOutOfRangeException(nameof(bindingPolicyOverride));

        await operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await ReadV2UnlockedAsync(profileId, cancellationToken).ConfigureAwait(false);
            if (current.Revision != expectedRevision)
                throw new InvalidOperationException("The Mod Profile changed before the update could be saved.");
            var updated = current with
            {
                DisplayName = normalizedName,
                Revision = checked(current.Revision + 1),
                AssemblyBindingPolicyOverride = bindingPolicyOverride,
                Members = members.ToArray(),
                UpdatedAtUtc = DateTimeOffset.UtcNow,
                Description = normalizedDescription,
            };
            updated.Validate();
            await WriteAtomicAsync(GetProfilePath(profileId), updated, overwrite: true, cancellationToken)
                .ConfigureAwait(false);
            Changed?.Invoke();
            return updated;
        }
        finally
        {
            operationLock.Release();
        }
    }

    public async ValueTask<bool> DeleteAsync(
        ProfileId profileId,
        CancellationToken cancellationToken = default)
    {
        if (profileId.Value == ModProfileV2.NoModsId)
            throw new InvalidOperationException("The no-Mod Profile cannot be deleted.");

        await operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var source = GetProfileDirectory(profileId);
            if (!Directory.Exists(source))
                return false;
            _ = await ReadV2UnlockedAsync(profileId, cancellationToken).ConfigureAwait(false);
            var stagingRoot = Path.Combine(profilesRoot, ".staging");
            Directory.CreateDirectory(stagingRoot);
            var removed = Path.Combine(stagingRoot, $"delete-{Guid.NewGuid():N}");
            Directory.Move(source, removed);
            TryDeleteDirectory(removed);
            Changed?.Invoke();
            return true;
        }
        finally
        {
            operationLock.Release();
        }
    }

    internal async ValueTask<ModProfileV2> WriteMigratedAsync(
        ModProfileV2 profile,
        CancellationToken cancellationToken)
    {
        var profileId = profile.Validate();
        if (profileId.Value == ModProfileV2.NoModsId)
            throw new InvalidOperationException("The reserved no-Mod Profile is not a migration target.");

        await operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(profilesRoot);
            await EnsureNoModsUnlockedAsync(cancellationToken).ConfigureAwait(false);
            var path = GetProfilePath(profileId);
            if (!File.Exists(path))
                throw new FileNotFoundException("The legacy Mod Profile does not exist.", path);
            var existing = await TryReadV2UnlockedAsync(profileId, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
                return existing;
            await WriteAtomicAsync(path, profile, overwrite: true, cancellationToken).ConfigureAwait(false);
            Changed?.Invoke();
            return profile;
        }
        finally
        {
            operationLock.Release();
        }
    }

    internal static JsonSerializerOptions SerializerOptions => JsonOptions;

    private async ValueTask<ModProfileV2> EnsureNoModsUnlockedAsync(CancellationToken cancellationToken)
    {
        var profileId = ProfileId.Parse(ModProfileV2.NoModsId);
        var path = GetProfilePath(profileId);
        if (File.Exists(path))
        {
            return await TryReadV2UnlockedAsync(profileId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("The reserved no-Mod Profile path contains a legacy document.");
        }

        Directory.CreateDirectory(GetProfileDirectory(profileId));
        var now = DateTimeOffset.UtcNow;
        var profile = new ModProfileV2(
            ModProfileV2.CurrentSchema,
            ModProfileV2.NoModsId,
            "No Mods",
            Revision: 1,
            AssemblyBindingPolicyOverride: null,
            Array.Empty<ModProfileMember>(),
            now,
            now,
            Description: null);
        try
        {
            await WriteAtomicAsync(path, profile, overwrite: false, cancellationToken).ConfigureAwait(false);
            Changed?.Invoke();
            return profile;
        }
        catch (IOException) when (File.Exists(path))
        {
            return await ReadV2UnlockedAsync(profileId, cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask<ModProfileV2> ReadV2UnlockedAsync(
        ProfileId profileId,
        CancellationToken cancellationToken) =>
        await TryReadV2UnlockedAsync(profileId, cancellationToken).ConfigureAwait(false)
        ?? throw new InvalidDataException("The requested Profile is not a Mod Profile v2 document.");

    private async ValueTask<ModProfileV2?> TryReadV2UnlockedAsync(
        ProfileId profileId,
        CancellationToken cancellationToken)
    {
        var path = GetProfilePath(profileId);
        var file = new FileInfo(path);
        if (!file.Exists)
            throw new FileNotFoundException("The Mod Profile does not exist.", path);
        if (file.Length is < 1 or > MaximumProfileBytes)
            throw new InvalidDataException("The Mod Profile has an invalid size.");
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        try
        {
            using var document = JsonDocument.Parse(bytes);
            if (!document.RootElement.TryGetProperty("schema", out var schema) ||
                schema.GetString() != ModProfileV2.CurrentSchema)
            {
                return null;
            }

            var profile = JsonSerializer.Deserialize<ModProfileV2>(bytes, JsonOptions)
                ?? throw new InvalidDataException("The Mod Profile is empty.");
            var actualId = profile.Validate();
            if (actualId != profileId)
                throw new InvalidDataException("The Mod Profile ID does not match its directory.");
            return profile;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The Mod Profile JSON is malformed.", exception);
        }
    }

    private string GetProfileDirectory(ProfileId profileId) => Path.Combine(profilesRoot, profileId.Value);
    private string GetProfilePath(ProfileId profileId) => Path.Combine(GetProfileDirectory(profileId), "profile.json");

    private static string NormalizeDisplayName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("The Mod Profile display name is required.", nameof(value));
        var normalized = value.Trim();
        if (normalized.Length > 80)
            throw new ArgumentException("The Mod Profile display name is too long.", nameof(value));
        return normalized;
    }

    private static string? NormalizeDescription(string? value)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        if (normalized?.Length > 1_024)
            throw new ArgumentException("The Mod Profile description is too long.", nameof(value));
        return normalized;
    }

    private static async ValueTask WriteAtomicAsync(
        string path,
        ModProfileV2 profile,
        bool overwrite,
        CancellationToken cancellationToken)
    {
        profile.Validate();
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(
                             temporary,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             16 * 1024,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await JsonSerializer.SerializeAsync(stream, profile, JsonOptions, cancellationToken)
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
                // Best-effort cleanup; the committed Profile is authoritative.
            }
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup; unreferenced staging is not authoritative.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort cleanup; unreferenced staging is not authoritative.
        }
    }
}
