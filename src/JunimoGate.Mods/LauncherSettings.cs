using System.Text.Json;
using System.Text.Json.Serialization;

namespace JunimoGate.Mods;

public enum LauncherLogRetentionPolicy
{
    CurrentAndPrevious,
}

public sealed record LauncherSettings(
    string Schema,
    long Revision,
    ModAssemblyBindingPolicy DefaultAssemblyBindingPolicy,
    bool AddImportedModsToActiveProfile,
    bool ConfirmLibraryDeletion,
    LauncherLogRetentionPolicy LogRetentionPolicy,
    DateTimeOffset? LastUpdateCheckUtc,
    bool LegacyDefaultPolicyMigrationCompleted,
    DateTimeOffset UpdatedAtUtc)
{
    public const string CurrentSchema = "junimogate-launcher-settings/v1";

    public void Validate()
    {
        if (Schema != CurrentSchema || Revision < 1 ||
            !Enum.IsDefined(DefaultAssemblyBindingPolicy) ||
            !Enum.IsDefined(LogRetentionPolicy) ||
            LastUpdateCheckUtc is { } lastCheck && lastCheck == default || UpdatedAtUtc == default)
        {
            throw new InvalidDataException("The Launcher settings document is malformed.");
        }
    }
}

public sealed class LauncherSettingsRepository
{
    private const int MaximumSettingsBytes = 64 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.General)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };
    private readonly string path;
    private readonly SemaphoreSlim operationLock = new(1, 1);

    public LauncherSettingsRepository(string settingsRoot)
    {
        if (string.IsNullOrWhiteSpace(settingsRoot) || !Path.IsPathFullyQualified(settingsRoot))
            throw new ArgumentException("The settings root must be absolute.", nameof(settingsRoot));
        path = Path.Combine(Path.TrimEndingDirectorySeparator(Path.GetFullPath(settingsRoot)), "launcher.json");
    }

    public async ValueTask<LauncherSettings> OpenOrCreateAsync(
        ModAssemblyBindingPolicy legacyDefaultPolicy,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(legacyDefaultPolicy))
            throw new ArgumentOutOfRangeException(nameof(legacyDefaultPolicy));
        await operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (File.Exists(path))
                return await ReadUnlockedAsync(cancellationToken).ConfigureAwait(false);
            var settings = new LauncherSettings(
                LauncherSettings.CurrentSchema,
                Revision: 1,
                legacyDefaultPolicy,
                AddImportedModsToActiveProfile: false,
                ConfirmLibraryDeletion: true,
                LauncherLogRetentionPolicy.CurrentAndPrevious,
                LastUpdateCheckUtc: null,
                LegacyDefaultPolicyMigrationCompleted: false,
                DateTimeOffset.UtcNow);
            try
            {
                await WriteAtomicAsync(settings, overwrite: false, cancellationToken).ConfigureAwait(false);
                return settings;
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

    public async ValueTask<LauncherSettings> ReadAsync(CancellationToken cancellationToken = default)
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

    public async ValueTask<LauncherSettings> UpdateAsync(
        long expectedRevision,
        Func<LauncherSettings, LauncherSettings> update,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        await operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await ReadUnlockedAsync(cancellationToken).ConfigureAwait(false);
            if (current.Revision != expectedRevision)
                throw new InvalidOperationException("The Launcher settings changed before the update could be saved.");
            var requested = update(current) ?? throw new InvalidOperationException("The Launcher settings update returned null.");
            var updated = requested with
            {
                Schema = LauncherSettings.CurrentSchema,
                Revision = checked(current.Revision + 1),
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            };
            updated.Validate();
            await WriteAtomicAsync(updated, overwrite: true, cancellationToken).ConfigureAwait(false);
            return updated;
        }
        finally
        {
            operationLock.Release();
        }
    }

    private async ValueTask<LauncherSettings> ReadUnlockedAsync(CancellationToken cancellationToken)
    {
        var file = new FileInfo(path);
        if (!file.Exists)
            throw new FileNotFoundException("The Launcher settings do not exist.", path);
        if (file.Length is < 1 or > MaximumSettingsBytes)
            throw new InvalidDataException("The Launcher settings have an invalid size.");
        try
        {
            var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            var settings = JsonSerializer.Deserialize<LauncherSettings>(bytes, JsonOptions)
                ?? throw new InvalidDataException("The Launcher settings are empty.");
            settings.Validate();
            return settings;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The Launcher settings JSON is malformed.", exception);
        }
    }

    private async ValueTask WriteAtomicAsync(
        LauncherSettings settings,
        bool overwrite,
        CancellationToken cancellationToken)
    {
        settings.Validate();
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
                await JsonSerializer.SerializeAsync(stream, settings, JsonOptions, cancellationToken)
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
                // Best-effort cleanup; the committed settings file is authoritative.
            }
        }
    }
}

public static class LauncherSettingsMigration
{
    public static async ValueTask<LauncherSettings> MigrateLegacyDefaultPolicyAsync(
        LauncherSettingsRepository settingsRepository,
        ModProfileV2Repository profileRepository,
        ModAssemblyBindingPolicy legacyDefaultPolicy,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settingsRepository);
        ArgumentNullException.ThrowIfNull(profileRepository);
        var settings = await settingsRepository
            .OpenOrCreateAsync(legacyDefaultPolicy, cancellationToken)
            .ConfigureAwait(false);
        if (settings.LegacyDefaultPolicyMigrationCompleted)
            return settings;

        ModProfileV2 profile;
        try
        {
            profile = await profileRepository
                .ReadAsync(ProfileId.Parse("default"), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (InvalidDataException)
        {
            // The legacy v1 document is migrated by the existing Mod library migration first.
            return settings;
        }

        if (profile.AssemblyBindingPolicyOverride is { } profilePolicy &&
            settings.DefaultAssemblyBindingPolicy != profilePolicy)
        {
            settings = await settingsRepository.UpdateAsync(
                    settings.Revision,
                    value => value with { DefaultAssemblyBindingPolicy = profilePolicy },
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (profile.AssemblyBindingPolicyOverride is not null)
        {
            _ = await profileRepository.UpdateAsync(
                    ProfileId.Parse("default"),
                    profile.Revision,
                    profile.DisplayName,
                    profile.Description,
                    bindingPolicyOverride: null,
                    profile.Members,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return await settingsRepository.UpdateAsync(
                settings.Revision,
                value => value with { LegacyDefaultPolicyMigrationCompleted = true },
                cancellationToken)
            .ConfigureAwait(false);
    }
}
