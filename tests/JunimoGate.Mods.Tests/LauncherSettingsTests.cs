using JunimoGate.Mods;
using JunimoGate.Tests;

internal static class LauncherSettingsTests
{
    public static void CreatesDefaultsAndUpdatesAtomically()
    {
        using var fixture = new Fixture();
        var created = fixture.Repository.OpenOrCreateAsync(ModAssemblyBindingPolicy.Strict)
            .AsTask().GetAwaiter().GetResult();
        TestHarness.Equal(LauncherSettings.CurrentSchema, created.Schema);
        TestHarness.Equal(ModAssemblyBindingPolicy.Strict, created.DefaultAssemblyBindingPolicy);
        TestHarness.False(created.AddImportedModsToActiveProfile);
        TestHarness.True(created.ConfirmLibraryDeletion);
        TestHarness.False(created.LegacyDefaultPolicyMigrationCompleted);

        var updated = fixture.Repository.UpdateAsync(
                created.Revision,
                value => value with
                {
                    DefaultAssemblyBindingPolicy = ModAssemblyBindingPolicy.HighestCompatible,
                    AddImportedModsToActiveProfile = true,
                    LegacyDefaultPolicyMigrationCompleted = true,
                })
            .AsTask().GetAwaiter().GetResult();
        TestHarness.Equal(2L, updated.Revision);
        TestHarness.True(updated.AddImportedModsToActiveProfile);
        TestHarness.True(updated.LegacyDefaultPolicyMigrationCompleted);
        TestHarness.Equal(updated, fixture.Repository.ReadAsync().AsTask().GetAwaiter().GetResult());
        TestHarness.Throws<InvalidOperationException>(() => fixture.Repository.UpdateAsync(
                created.Revision,
                value => value with { ConfirmLibraryDeletion = false })
            .AsTask().GetAwaiter().GetResult());
    }

    public static void PersistsUpdateCheckTime()
    {
        using var fixture = new Fixture();
        var created = fixture.Repository.OpenOrCreateAsync(ModAssemblyBindingPolicy.HighestCompatible)
            .AsTask().GetAwaiter().GetResult();
        var checkedAt = DateTimeOffset.UtcNow;
        var updated = fixture.Repository.UpdateAsync(
                created.Revision,
                value => value with { LastUpdateCheckUtc = checkedAt })
            .AsTask().GetAwaiter().GetResult();
        TestHarness.Equal(checkedAt, updated.LastUpdateCheckUtc!.Value);
    }

    public static void RejectsMalformedJson()
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(fixture.Root);
        File.WriteAllText(Path.Combine(fixture.Root, "launcher.json"), "{\"schema\":null}");
        TestHarness.Throws<InvalidDataException>(() => fixture.Repository.ReadAsync().AsTask().GetAwaiter().GetResult());
    }

    public static void MigratesAndClearsTheDefaultProfileOverrideOnce()
    {
        using var fixture = new Fixture();
        var profilesRoot = Path.Combine(fixture.Root, "profiles");
        var profiles = new ModProfileV2Repository(profilesRoot);
        var defaultDirectory = Path.Combine(profilesRoot, "default");
        Directory.CreateDirectory(defaultDirectory);
        var now = DateTimeOffset.UtcNow;
        var migrated = new ModProfileV2(
            ModProfileV2.CurrentSchema,
            "default",
            "Default",
            Revision: 4,
            ModAssemblyBindingPolicy.FirstLoaded,
            Array.Empty<ModProfileMember>(),
            now,
            now,
            Description: null);
        var jsonOptions = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.General)
        {
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
        };
        File.WriteAllText(
            Path.Combine(defaultDirectory, "profile.json"),
            System.Text.Json.JsonSerializer.Serialize(migrated, jsonOptions));

        var settings = LauncherSettingsMigration.MigrateLegacyDefaultPolicyAsync(
                fixture.Repository,
                profiles,
                ModAssemblyBindingPolicy.Strict)
            .AsTask().GetAwaiter().GetResult();
        TestHarness.Equal(ModAssemblyBindingPolicy.FirstLoaded, settings.DefaultAssemblyBindingPolicy);
        TestHarness.True(settings.LegacyDefaultPolicyMigrationCompleted);
        TestHarness.Equal(
            null,
            profiles.ReadAsync(ProfileId.Parse("default")).AsTask().GetAwaiter().GetResult()
                .AssemblyBindingPolicyOverride);

        var repeated = LauncherSettingsMigration.MigrateLegacyDefaultPolicyAsync(
                fixture.Repository,
                profiles,
                ModAssemblyBindingPolicy.HighestCompatible)
            .AsTask().GetAwaiter().GetResult();
        TestHarness.Equal(settings, repeated);
    }

    private sealed class Fixture : IDisposable
    {
        public Fixture()
        {
            Root = Path.Combine(Path.GetTempPath(), $"junimogate-launcher-settings-{Guid.NewGuid():N}");
            Repository = new LauncherSettingsRepository(Root);
        }

        public string Root { get; }
        public LauncherSettingsRepository Repository { get; }

        public void Dispose()
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
    }
}
