using System.Text.Json;
using System.Text.Json.Serialization;

namespace JunimoGate.Mods;

public enum ModAssemblyBindingPolicy
{
    Strict,
    FirstLoaded,
    HighestCompatible,
}

public sealed record ModProfile(
    string Schema,
    string Id,
    long Revision,
    ModAssemblyBindingPolicy AssemblyBindingPolicy,
    DateTimeOffset UpdatedAtUtc)
{
    public const string CurrentSchema = "junimogate-mod-profile/v1";

    public ProfileId Validate()
    {
        if (Schema != CurrentSchema || !ProfileId.TryParse(Id, out var profileId) || Revision < 1 ||
            !Enum.IsDefined(AssemblyBindingPolicy) || UpdatedAtUtc == default)
        {
            throw new InvalidDataException("The Mod Profile is malformed.");
        }

        return profileId;
    }
}

public readonly record struct ProfileId
{
    private ProfileId(string value)
    {
        Value = value;
    }

    public string Value { get; }

    public static ProfileId Parse(string value)
    {
        if (!TryParse(value, out var profileId))
        {
            throw new FormatException("A profile ID must match [a-z0-9][a-z0-9-]{0,63}.");
        }

        return profileId;
    }

    public static bool TryParse(string? value, out ProfileId profileId)
    {
        profileId = default;
        if (string.IsNullOrEmpty(value) || value.Length > 64 || !IsLowerAlphaNumeric(value[0]))
        {
            return false;
        }

        for (var index = 1; index < value.Length; index++)
        {
            if (!IsLowerAlphaNumeric(value[index]) && value[index] != '-')
            {
                return false;
            }
        }

        profileId = new ProfileId(value);
        return true;
    }

    public override string ToString() => Value ?? string.Empty;

    private static bool IsLowerAlphaNumeric(char value) =>
        value is >= 'a' and <= 'z' or >= '0' and <= '9';
}

public sealed class ProfileLayout
{
    public ProfileLayout(string profilesRoot, ProfileId profileId)
    {
        if (string.IsNullOrWhiteSpace(profilesRoot) || !Path.IsPathFullyQualified(profilesRoot))
        {
            throw new ArgumentException("The profiles root must be absolute.", nameof(profilesRoot));
        }

        ProfilesRoot = Path.GetFullPath(profilesRoot);
        ProfileDirectory = Path.Combine(ProfilesRoot, profileId.Value);
        ProfileJsonPath = Path.Combine(ProfileDirectory, "profile.json");
        ModsDirectory = Path.Combine(ProfileDirectory, "Mods");
        EnabledDirectory = Path.Combine(ModsDirectory, "enabled");
        DisabledDirectory = Path.Combine(ModsDirectory, "disabled");
        DownloadsDirectory = Path.Combine(ProfileDirectory, "downloads");
        StagingDirectory = Path.Combine(ProfileDirectory, "staging");
    }

    public string ProfilesRoot { get; }

    public string ProfileDirectory { get; }

    public string ProfileJsonPath { get; }

    public string ModsDirectory { get; }

    public string EnabledDirectory { get; }

    public string DisabledDirectory { get; }

    public string DownloadsDirectory { get; }

    public string StagingDirectory { get; }
}

public sealed class ModProfileRepository
{
    private const int MaximumProfileBytes = 64 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.General)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };
    private readonly string profilesRoot;

    public ModProfileRepository(string profilesRoot)
    {
        if (string.IsNullOrWhiteSpace(profilesRoot) || !Path.IsPathFullyQualified(profilesRoot))
            throw new ArgumentException("The profiles root must be absolute.", nameof(profilesRoot));
        this.profilesRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(profilesRoot));
    }

    public async ValueTask<ModProfile> OpenOrCreateAsync(
        ProfileId profileId,
        CancellationToken cancellationToken = default)
    {
        var layout = new ProfileLayout(profilesRoot, profileId);
        EnsureDirectories(layout);
        if (File.Exists(layout.ProfileJsonPath))
            return await ReadAsync(profileId, cancellationToken).ConfigureAwait(false);

        var profile = new ModProfile(
            ModProfile.CurrentSchema,
            profileId.Value,
            Revision: 1,
            ModAssemblyBindingPolicy.HighestCompatible,
            DateTimeOffset.UtcNow);
        try
        {
            await WriteAtomicAsync(layout.ProfileJsonPath, profile, overwrite: false, cancellationToken)
                .ConfigureAwait(false);
            return profile;
        }
        catch (IOException) when (File.Exists(layout.ProfileJsonPath))
        {
            return await ReadAsync(profileId, cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask<ModProfile> ReadAsync(
        ProfileId profileId,
        CancellationToken cancellationToken = default)
    {
        var layout = new ProfileLayout(profilesRoot, profileId);
        var file = new FileInfo(layout.ProfileJsonPath);
        if (!file.Exists)
            throw new FileNotFoundException("The Mod Profile does not exist.", layout.ProfileJsonPath);
        if (file.Length is < 1 or > MaximumProfileBytes)
            throw new InvalidDataException("The Mod Profile has an invalid size.");

        var bytes = await File.ReadAllBytesAsync(layout.ProfileJsonPath, cancellationToken).ConfigureAwait(false);
        try
        {
            using var document = JsonDocument.Parse(bytes);
            if (!document.RootElement.TryGetProperty("schema", out var schema))
                throw new InvalidDataException("The Mod Profile schema is missing.");
            if (schema.GetString() == ModProfileV2.CurrentSchema)
            {
                var v2 = JsonSerializer.Deserialize<ModProfileV2>(bytes, ModProfileV2Repository.SerializerOptions)
                    ?? throw new InvalidDataException("The Mod Profile is empty.");
                var actualV2Id = v2.Validate();
                if (actualV2Id != profileId)
                    throw new InvalidDataException("The Mod Profile ID does not match its directory.");
                return ProjectLegacyView(v2);
            }
            var profile = JsonSerializer.Deserialize<ModProfile>(bytes, JsonOptions)
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

    public async ValueTask<ModProfile> UpdateBindingPolicyAsync(
        ProfileId profileId,
        long expectedRevision,
        ModAssemblyBindingPolicy policy,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(policy))
            throw new ArgumentOutOfRangeException(nameof(policy));
        var current = await ReadAsync(profileId, cancellationToken).ConfigureAwait(false);
        if (current.Revision != expectedRevision)
            throw new InvalidOperationException("The Mod Profile changed before the update could be saved.");
        if (current.AssemblyBindingPolicy == policy)
            return current;

        var layout = new ProfileLayout(profilesRoot, profileId);
        var bytes = await File.ReadAllBytesAsync(layout.ProfileJsonPath, cancellationToken).ConfigureAwait(false);
        using (var document = JsonDocument.Parse(bytes))
        {
            if (document.RootElement.TryGetProperty("schema", out var schema) &&
                schema.GetString() == ModProfileV2.CurrentSchema)
            {
                var v2Repository = new ModProfileV2Repository(profilesRoot);
                var v2 = await v2Repository.ReadAsync(profileId, cancellationToken).ConfigureAwait(false);
                var updatedV2 = await v2Repository.UpdateAsync(
                        profileId,
                        expectedRevision,
                        v2.DisplayName,
                        v2.Description,
                        policy,
                        v2.Members,
                        cancellationToken)
                    .ConfigureAwait(false);
                return ProjectLegacyView(updatedV2);
            }
        }

        var updated = current with
        {
            Revision = checked(current.Revision + 1),
            AssemblyBindingPolicy = policy,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };
        await WriteAtomicAsync(layout.ProfileJsonPath, updated, overwrite: true, cancellationToken).ConfigureAwait(false);
        return updated;
    }

    private static ModProfile ProjectLegacyView(ModProfileV2 profile) => new(
        ModProfile.CurrentSchema,
        profile.Id,
        profile.Revision,
        profile.AssemblyBindingPolicyOverride ?? ModAssemblyBindingPolicy.HighestCompatible,
        profile.UpdatedAtUtc);

    private static void EnsureDirectories(ProfileLayout layout)
    {
        Directory.CreateDirectory(layout.ProfileDirectory);
        Directory.CreateDirectory(layout.EnabledDirectory);
        Directory.CreateDirectory(layout.DisabledDirectory);
        Directory.CreateDirectory(layout.DownloadsDirectory);
        Directory.CreateDirectory(layout.StagingDirectory);
    }

    private static async ValueTask WriteAtomicAsync(
        string path,
        ModProfile profile,
        bool overwrite,
        CancellationToken cancellationToken)
    {
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
                // Best-effort cleanup; the committed profile is authoritative.
            }
        }
    }
}
