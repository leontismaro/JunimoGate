using JunimoGate.Mods;
using JunimoGate.Tests;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

internal static class ModProfileV2Tests
{
    public static void CreatesSystemAndUserProfiles()
    {
        using var fixture = new Fixture();
        WriteLegacyProfile(fixture.Root, ProfileId.Parse("default"));

        var initial = fixture.Repository.ListAsync().AsTask().GetAwaiter().GetResult();
        TestHarness.Equal(1, initial.Count);
        TestHarness.Equal(ModProfileV2.NoModsId, initial[0].Id);
        TestHarness.Equal(0, initial[0].Members.Count);

        var first = fixture.Repository.CreateAsync(" Farm ").AsTask().GetAwaiter().GetResult();
        var second = fixture.Repository.CreateAsync("Farm").AsTask().GetAwaiter().GetResult();
        TestHarness.Equal("Farm", first.DisplayName);
        TestHarness.True(first.Id != second.Id);

        var listed = fixture.Repository.ListAsync().AsTask().GetAwaiter().GetResult();
        TestHarness.Equal(3, listed.Count);
        TestHarness.Equal(ModProfileV2.NoModsId, listed[0].Id);
        TestHarness.Throws<InvalidOperationException>(() => fixture.CreateCommands().DeleteProfileAsync(
            ProfileId.Parse(ModProfileV2.NoModsId)).AsTask().GetAwaiter().GetResult());
    }

    public static void UpdatesMembersAtomically()
    {
        using var fixture = new Fixture();
        var profile = fixture.Repository.CreateAsync("Main").AsTask().GetAwaiter().GetResult();
        var first = LibraryItem("Example.First", "1.0.0", 'a');
        var second = LibraryItem("Example.Second", "2.0.0", 'b');
        var members = new[]
        {
            ModProfileMember.FromLibraryItem(first, enabled: true),
            ModProfileMember.FromLibraryItem(second, enabled: false) with { LibraryItemId = null },
        };
        var updated = fixture.Repository.UpdateAsync(
                ProfileId.Parse(profile.Id),
                profile.Revision,
                "Main group",
                "Shared setup",
                ModAssemblyBindingPolicy.Strict,
                members)
            .AsTask().GetAwaiter().GetResult();

        TestHarness.Equal(2L, updated.Revision);
        TestHarness.Equal(2, updated.Members.Count);
        TestHarness.Equal(first.LibraryItemId, updated.Members[0].LibraryItemId);
        TestHarness.Equal<string?>(null, updated.Members[1].LibraryItemId);
        TestHarness.Equal("Example.Second", updated.Members[1].UniqueId);
        TestHarness.Throws<InvalidOperationException>(() => fixture.Repository.UpdateAsync(
                ProfileId.Parse(profile.Id),
                profile.Revision,
                "stale",
                null,
                null,
                Array.Empty<ModProfileMember>())
            .AsTask().GetAwaiter().GetResult());
    }

    public static void RejectsDuplicateMembersAndDeletesExactly()
    {
        using var fixture = new Fixture();
        var profile = fixture.Repository.CreateAsync("Temporary").AsTask().GetAwaiter().GetResult();
        var item = LibraryItem("Example.Duplicate", "1.0.0", 'c');
        var member = ModProfileMember.FromLibraryItem(item, enabled: true);
        TestHarness.Throws<InvalidDataException>(() => fixture.Repository.UpdateAsync(
                ProfileId.Parse(profile.Id),
                profile.Revision,
                profile.DisplayName,
                null,
                null,
                new[] { member, member with { UniqueId = member.UniqueId.ToUpperInvariant() } })
            .AsTask().GetAwaiter().GetResult());

        var commands = fixture.CreateCommands();
        TestHarness.True(commands.DeleteProfileAsync(ProfileId.Parse(profile.Id))
            .AsTask().GetAwaiter().GetResult());
        TestHarness.False(commands.DeleteProfileAsync(ProfileId.Parse(profile.Id))
            .AsTask().GetAwaiter().GetResult());
    }

    public static void MutatesProfileMembersAtomically()
    {
        using var fixture = new Fixture();
        var profile = fixture.Repository.CreateAsync("Main").AsTask().GetAwaiter().GetResult();
        var profileId = ProfileId.Parse(profile.Id);
        var service = new ModProfileMemberMutationService(fixture.Repository);
        var first = LibraryItem("Example.First", "1.0.0", 'a');
        var second = LibraryItem("Example.Second", "1.0.0", 'b');
        var added = service.AddOrReplaceAsync(profileId, new[] { first, second }, enabled: true)
            .AsTask().GetAwaiter().GetResult();
        TestHarness.Equal(2, added.AddedMembers);
        TestHarness.Equal(0, added.ReplacedMembers);
        TestHarness.Equal(2L, added.Profile.Revision);

        var newer = LibraryItem("Example.First", "2.0.0", 'c');
        var replaced = service.AddOrReplaceAsync(profileId, new[] { newer }, enabled: true)
            .AsTask().GetAwaiter().GetResult();
        TestHarness.Equal(0, replaced.AddedMembers);
        TestHarness.Equal(1, replaced.ReplacedMembers);
        TestHarness.Equal(2, replaced.Profile.Members.Count);
        TestHarness.Equal("2.0.0", replaced.Profile.Members.Single(member =>
            member.UniqueId == "Example.First").ExpectedVersion);

        var disabled = service.SetEnabledAsync(
                profileId,
                new[] { "Example.First", "Example.Second" },
                enabled: false)
            .AsTask().GetAwaiter().GetResult();
        TestHarness.Equal(2, disabled.ChangedMembers);
        TestHarness.True(disabled.Profile.Members.All(member => !member.Enabled));
        TestHarness.Equal(replaced.Profile.Revision + 1, disabled.Profile.Revision);

        var removed = service.RemoveAsync(profileId, new[] { "Example.First" })
            .AsTask().GetAwaiter().GetResult();
        TestHarness.Equal(1, removed.ChangedMembers);
        TestHarness.Equal(1, removed.Profile.Members.Count);
        TestHarness.Equal("Example.Second", removed.Profile.Members[0].UniqueId);
    }

    public static void RejectsInvalidMemberMutations()
    {
        using var fixture = new Fixture();
        var profile = fixture.Repository.CreateAsync("Main").AsTask().GetAwaiter().GetResult();
        var service = new ModProfileMemberMutationService(fixture.Repository);
        var first = LibraryItem("Example.Shared", "1.0.0", 'd');
        var second = LibraryItem("Example.Shared", "2.0.0", 'e');
        TestHarness.Throws<ArgumentException>(() => service.AddOrReplaceAsync(
                ProfileId.Parse(profile.Id),
                new[] { first, second },
                enabled: true)
            .AsTask().GetAwaiter().GetResult());
        TestHarness.Throws<InvalidOperationException>(() => service.AddOrReplaceAsync(
                ProfileId.Parse(ModProfileV2.NoModsId),
                new[] { first },
                enabled: true)
            .AsTask().GetAwaiter().GetResult());
    }

    public static void MigratesLegacyDirectoriesAndRemovesFallback()
    {
        using var fixture = new Fixture();
        var profileId = ProfileId.Parse("default");
        WriteLegacyProfile(fixture.Root, profileId);
        var layout = LegacyLayout(fixture.Root, profileId);
        WriteMod(layout.EnabledDirectory, "Selected", "Example.Shared", "2.0.0", "selected");
        CopyDirectory(
            Path.Combine(layout.EnabledDirectory, "Selected"),
            Path.Combine(layout.DisabledDirectory, "SelectedCopy"));
        WriteMod(layout.DisabledDirectory, "Older", "Example.Shared", "1.0.0", "older");
        WriteMod(layout.DisabledDirectory, "Optional", "Example.Optional", "1.0.0", "optional");

        var library = new ModLibraryRepository(fixture.LibraryRoot);
        var migrator = new LegacyModProfileMigrator(fixture.Root, library, fixture.Repository);
        var result = migrator.MigrateAsync(profileId, "Default").AsTask().GetAwaiter().GetResult();

        TestHarness.False(result.AlreadyMigrated);
        TestHarness.Equal(3, result.ImportedItems);
        TestHarness.Equal(1, result.ReusedItems);
        TestHarness.Equal(2, result.Profile.Members.Count);
        var selected = result.Profile.Members.Single(member => member.UniqueId == "Example.Shared");
        TestHarness.True(selected.Enabled);
        TestHarness.Equal("2.0.0", selected.ExpectedVersion);
        var optional = result.Profile.Members.Single(member => member.UniqueId == "Example.Optional");
        TestHarness.False(optional.Enabled);
        TestHarness.False(Directory.Exists(layout.ModsDirectory));
        TestHarness.False(Directory.Exists(layout.DownloadsDirectory));
        TestHarness.False(Directory.Exists(layout.StagingDirectory));
        TestHarness.Equal(3, library.ReadAsync().AsTask().GetAwaiter().GetResult().Items.Count);

        TestHarness.Equal(
            ModAssemblyBindingPolicy.HighestCompatible,
            fixture.Repository.ReadAsync(profileId).AsTask().GetAwaiter().GetResult().AssemblyBindingPolicyOverride);

        var repeated = migrator.MigrateAsync(profileId, "Ignored").AsTask().GetAwaiter().GetResult();
        TestHarness.True(repeated.AlreadyMigrated);
        TestHarness.Equal(0, repeated.ImportedItems);
    }

    public static void CreatesDefaultV2WithoutLegacyDirectories()
    {
        using var fixture = new Fixture();
        var profile = fixture.Repository.OpenOrCreateDefaultAsync().AsTask().GetAwaiter().GetResult();
        var layout = LegacyLayout(fixture.Root, ProfileId.Parse("default"));

        TestHarness.Equal(ModProfileV2.CurrentSchema, profile.Schema);
        TestHarness.False(Directory.Exists(layout.ModsDirectory));
        TestHarness.False(Directory.Exists(layout.DownloadsDirectory));
        TestHarness.False(Directory.Exists(layout.StagingDirectory));
    }

    public static void CleansLegacyDirectoriesForAlreadyMigratedProfiles()
    {
        using var fixture = new Fixture();
        var profileId = ProfileId.Parse("default");
        var profile = fixture.Repository.OpenOrCreateDefaultAsync().AsTask().GetAwaiter().GetResult();
        var missing = ModProfileMember.FromLibraryItem(
            LibraryItem("Example.Missing", "1.0.0", 'f'),
            enabled: true) with { LibraryItemId = null };
        _ = fixture.Repository.UpdateAsync(
                profileId,
                profile.Revision,
                profile.DisplayName,
                profile.Description,
                profile.AssemblyBindingPolicyOverride,
                new[] { missing })
            .AsTask().GetAwaiter().GetResult();
        var layout = LegacyLayout(fixture.Root, profileId);
        Directory.CreateDirectory(layout.EnabledDirectory);
        Directory.CreateDirectory(layout.DownloadsDirectory);
        Directory.CreateDirectory(layout.StagingDirectory);

        var result = new LegacyModProfileMigrator(
                fixture.Root,
                new ModLibraryRepository(fixture.LibraryRoot),
                fixture.Repository)
            .MigrateAsync(profileId, "Ignored").AsTask().GetAwaiter().GetResult();

        TestHarness.True(result.AlreadyMigrated);
        TestHarness.Equal<string?>(null, result.Profile.Members.Single().LibraryItemId);
        TestHarness.False(Directory.Exists(layout.ModsDirectory));
        TestHarness.False(Directory.Exists(layout.DownloadsDirectory));
        TestHarness.False(Directory.Exists(layout.StagingDirectory));
    }

    public static void AllowsDeletedBindingsForAlreadyMigratedProfiles()
    {
        using var fixture = new Fixture();
        var profileId = ProfileId.Parse("default");
        WriteLegacyProfile(fixture.Root, profileId);
        var layout = LegacyLayout(fixture.Root, profileId);
        WriteMod(layout.EnabledDirectory, "Deleted", "Example.Deleted", "1.0.0", "deleted");
        var library = new ModLibraryRepository(fixture.LibraryRoot);
        var migrator = new LegacyModProfileMigrator(fixture.Root, library, fixture.Repository);
        var migrated = migrator.MigrateAsync(profileId, "Default").AsTask().GetAwaiter().GetResult();
        var member = migrated.Profile.Members.Single();
        library.DeleteManyAsync(new[] { member.LibraryItemId! }).AsTask().GetAwaiter().GetResult();

        Directory.CreateDirectory(layout.EnabledDirectory);
        Directory.CreateDirectory(layout.DownloadsDirectory);
        Directory.CreateDirectory(layout.StagingDirectory);

        var repeated = migrator.MigrateAsync(profileId, "Ignored").AsTask().GetAwaiter().GetResult();

        TestHarness.True(repeated.AlreadyMigrated);
        var retained = repeated.Profile.Members.Single();
        TestHarness.Equal(member.LibraryItemId, retained.LibraryItemId);
        TestHarness.Equal(member.ExpectedName, retained.ExpectedName);
        TestHarness.Equal(member.ExpectedVersion, retained.ExpectedVersion);
        TestHarness.True(retained.Enabled);
        TestHarness.False(Directory.Exists(layout.ModsDirectory));
        TestHarness.False(Directory.Exists(layout.DownloadsDirectory));
        TestHarness.False(Directory.Exists(layout.StagingDirectory));
    }

    public static void SerializesConcurrentLegacyMigration()
    {
        using var fixture = new Fixture();
        var profileId = ProfileId.Parse("default");
        WriteLegacyProfile(fixture.Root, profileId);
        var layout = LegacyLayout(fixture.Root, profileId);
        WriteMod(layout.EnabledDirectory, "Selected", "Example.Concurrent", "1.0.0", "selected");
        var library = new ModLibraryRepository(fixture.LibraryRoot);
        var first = new LegacyModProfileMigrator(
            fixture.Root,
            library,
            new ModProfileV2Repository(fixture.Root));
        var second = new LegacyModProfileMigrator(
            fixture.Root,
            new ModLibraryRepository(fixture.LibraryRoot),
            new ModProfileV2Repository(fixture.Root));

        var results = Task.WhenAll(
                first.MigrateAllAsync().AsTask(),
                second.MigrateAllAsync().AsTask())
            .GetAwaiter().GetResult();

        TestHarness.Equal(2, results.Length);
        TestHarness.Equal(1, results.Count(batch => batch.Single().AlreadyMigrated));
        TestHarness.Equal(1, results.Count(batch => !batch.Single().AlreadyMigrated));
        TestHarness.Equal(1, library.ReadAsync().AsTask().GetAwaiter().GetResult().Items.Count);
        TestHarness.False(Directory.Exists(layout.ModsDirectory));
    }

    public static void RejectsAmbiguousLegacyEnabledMods()
    {
        using var fixture = new Fixture();
        var profileId = ProfileId.Parse("default");
        WriteLegacyProfile(fixture.Root, profileId);
        var layout = LegacyLayout(fixture.Root, profileId);
        WriteMod(layout.EnabledDirectory, "First", "Example.Duplicate", "1.0.0", "first");
        WriteMod(layout.EnabledDirectory, "Second", "Example.Duplicate", "2.0.0", "second");
        var library = new ModLibraryRepository(fixture.LibraryRoot);
        var migrator = new LegacyModProfileMigrator(fixture.Root, library, fixture.Repository);

        TestHarness.Throws<InvalidDataException>(() => migrator.MigrateAsync(profileId, "Default")
            .AsTask().GetAwaiter().GetResult());
        TestHarness.Equal(0, library.ReadAsync().AsTask().GetAwaiter().GetResult().Items.Count);
        TestHarness.Throws<InvalidDataException>(() => fixture.Repository.ReadAsync(profileId)
            .AsTask().GetAwaiter().GetResult());
    }

    public static void ReusesResolvedLibraryIdentityAcrossLegacyProfiles()
    {
        using var fixture = new Fixture();
        var library = new ModLibraryRepository(fixture.LibraryRoot);
        var migrator = new LegacyModProfileMigrator(fixture.Root, library, fixture.Repository);
        var firstId = ProfileId.Parse("default");
        var secondId = ProfileId.Parse("secondary");

        WriteLegacyProfile(fixture.Root, firstId);
        WriteMod(LegacyLayout(fixture.Root, firstId).EnabledDirectory,
            "Shared", "Example.Shared", "1.0.0", "same-content");
        var first = migrator.MigrateAsync(firstId, "First").AsTask().GetAwaiter().GetResult();

        WriteLegacyProfile(fixture.Root, secondId);
        WriteMod(LegacyLayout(fixture.Root, secondId).EnabledDirectory,
            "Shared", "Example.Shared", "1.0.0", "same-content");
        var second = migrator.MigrateAsync(secondId, "Second").AsTask().GetAwaiter().GetResult();

        TestHarness.Equal(0, second.ImportedItems);
        TestHarness.Equal(1, second.ReusedItems);
        TestHarness.Equal(
            first.Profile.Members.Single().LibraryItemId,
            second.Profile.Members.Single().LibraryItemId);
    }

    public static void ProtectsActiveAndBuiltInProfiles()
    {
        using var fixture = new Fixture();
        _ = fixture.Repository.OpenOrCreateDefaultAsync().AsTask().GetAwaiter().GetResult();
        var repository = new ActiveModProfileSelectionRepository(fixture.Root);
        var created = repository.OpenOrCreateAsync(ProfileId.Parse("default"))
            .AsTask().GetAwaiter().GetResult();
        TestHarness.Equal("default", created.ActiveProfileId);
        var commands = new ModManagementCommandService(
            new ModLibraryRepository(fixture.LibraryRoot),
            fixture.Repository,
            repository,
            new PassThroughMutationGate());
        TestHarness.Throws<InvalidOperationException>(() => commands.DeleteProfileAsync(ProfileId.Parse("default"))
            .AsTask().GetAwaiter().GetResult());
        TestHarness.Throws<InvalidOperationException>(() => commands.DeleteProfileAsync(ProfileId.Parse(ModProfileV2.NoModsId))
            .AsTask().GetAwaiter().GetResult());
        var temporary = fixture.Repository.CreateAsync("Temporary").AsTask().GetAwaiter().GetResult();
        var updated = commands.SelectProfileAsync(ProfileId.Parse(temporary.Id))
            .AsTask().GetAwaiter().GetResult();
        TestHarness.Equal(2L, updated.Revision);
        TestHarness.Equal(temporary.Id, updated.ActiveProfileId);
        TestHarness.Throws<InvalidOperationException>(() => commands.DeleteProfileAsync(ProfileId.Parse(temporary.Id))
            .AsTask().GetAwaiter().GetResult());
        _ = commands.SelectProfileAsync(ProfileId.Parse(ModProfileV2.NoModsId))
            .AsTask().GetAwaiter().GetResult();
        TestHarness.True(commands.DeleteProfileAsync(ProfileId.Parse(temporary.Id))
            .AsTask().GetAwaiter().GetResult());
        TestHarness.Throws<FileNotFoundException>(() => commands.SelectProfileAsync(ProfileId.Parse("missing"))
            .AsTask().GetAwaiter().GetResult());
    }

    public static void RepositoryInstancesShareChangeSignals()
    {
        using var fixture = new Fixture();
        var profiles = new ModProfileV2Repository(fixture.Root);
        _ = fixture.Repository.ListAsync().AsTask().GetAwaiter().GetResult();
        var profileChanges = 0;
        profiles.Changed += () => profileChanges++;
        var created = fixture.Repository.CreateAsync("Shared signal").AsTask().GetAwaiter().GetResult();
        TestHarness.Equal(1, profileChanges);

        var firstSelection = new ActiveModProfileSelectionRepository(fixture.Root);
        var secondSelection = new ActiveModProfileSelectionRepository(fixture.Root);
        var selectionChanges = 0;
        secondSelection.Changed += () => selectionChanges++;
        _ = firstSelection.OpenOrCreateAsync(ProfileId.Parse("default")).AsTask().GetAwaiter().GetResult();
        var commands = new ModManagementCommandService(
            new ModLibraryRepository(fixture.LibraryRoot),
            fixture.Repository,
            firstSelection,
            new PassThroughMutationGate());
        _ = commands.SelectProfileAsync(ProfileId.Parse(created.Id)).AsTask().GetAwaiter().GetResult();
        TestHarness.Equal(1, selectionChanges);
    }

    private static ModLibraryItem LibraryItem(string uniqueId, string version, char digestCharacter)
    {
        var id = new string(digestCharacter, 64);
        return new ModLibraryItem(
            ModLibraryItem.CurrentSchema,
            id,
            id,
            new ModManifestSummary(
                uniqueId,
                "Test",
                version,
                uniqueId,
                null,
                "Mod.dll",
                null,
                Array.Empty<ModDependencySummary>()),
            $"library/{id}/files",
            DateTimeOffset.UtcNow,
            "test.zip",
            1,
            1);
    }

    private static LegacyLayoutPaths LegacyLayout(string profilesRoot, ProfileId profileId)
    {
        var profileDirectory = Path.Combine(profilesRoot, profileId.Value);
        var modsDirectory = Path.Combine(profileDirectory, "Mods");
        return new LegacyLayoutPaths(
            Path.Combine(profileDirectory, "profile.json"),
            modsDirectory,
            Path.Combine(modsDirectory, "enabled"),
            Path.Combine(modsDirectory, "disabled"),
            Path.Combine(profileDirectory, "downloads"),
            Path.Combine(profileDirectory, "staging"));
    }

    private static void WriteLegacyProfile(
        string profilesRoot,
        ProfileId profileId,
        long revision = 1,
        ModAssemblyBindingPolicy policy = ModAssemblyBindingPolicy.HighestCompatible)
    {
        var layout = LegacyLayout(profilesRoot, profileId);
        Directory.CreateDirectory(layout.ProfileDirectory);
        Directory.CreateDirectory(layout.EnabledDirectory);
        Directory.CreateDirectory(layout.DisabledDirectory);
        Directory.CreateDirectory(layout.DownloadsDirectory);
        Directory.CreateDirectory(layout.StagingDirectory);
        var profile = new
        {
            Schema = "junimogate-mod-profile/v1",
            Id = profileId.Value,
            Revision = revision,
            AssemblyBindingPolicy = policy,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };
        File.WriteAllText(
            layout.ProfileJsonPath,
            JsonSerializer.Serialize(profile, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                Converters = { new JsonStringEnumConverter() },
            }));
    }

    private static void WriteMod(
        string parent,
        string directoryName,
        string uniqueId,
        string version,
        string content)
    {
        var directory = Path.Combine(parent, directoryName);
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, "manifest.json"),
            $$"""
            {
              // Exercise the same SMAPI JSON accepted by ZIP import.
              "Name": "{{directoryName}}",
              "Author": "Test",
              "Version": "{{version}}",
              "UniqueID": "{{uniqueId}}",
              "EntryDll": "Mod.dll",
            }
            """,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        File.WriteAllText(Path.Combine(directory, "Mod.dll"), content);
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
    }

    private sealed record LegacyLayoutPaths(
        string ProfileJsonPath,
        string ModsDirectory,
        string EnabledDirectory,
        string DisabledDirectory,
        string DownloadsDirectory,
        string StagingDirectory)
    {
        public string ProfileDirectory => Path.GetDirectoryName(ProfileJsonPath)!;
    }

    private sealed class Fixture : IDisposable
    {
        public Fixture()
        {
            Root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), $"junimogate-profile-v2-{Guid.NewGuid():N}"));
            Directory.CreateDirectory(Root);
            LibraryRoot = Path.Combine(Path.GetDirectoryName(Root)!, $"junimogate-library-{Guid.NewGuid():N}");
            Directory.CreateDirectory(LibraryRoot);
            Repository = new ModProfileV2Repository(Root);
        }

        public string Root { get; }
        public string LibraryRoot { get; }
        public ModProfileV2Repository Repository { get; }

        public ModManagementCommandService CreateCommands()
        {
            return new ModManagementCommandService(
                new ModLibraryRepository(LibraryRoot),
                Repository,
                new ActiveModProfileSelectionRepository(Root),
                new PassThroughMutationGate());
        }

        public void Dispose()
        {
            Directory.Delete(Root, recursive: true);
            Directory.Delete(LibraryRoot, recursive: true);
        }
    }

    private sealed class PassThroughMutationGate : IModContentMutationGate
    {
        public ValueTask<IAsyncDisposable> AcquireAsync(
            IReadOnlyCollection<string> affectedLibraryItemIds,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<IAsyncDisposable>(new EmptyLease());
        }

        private sealed class EmptyLease : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
