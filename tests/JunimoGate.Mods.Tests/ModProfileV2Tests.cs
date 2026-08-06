using JunimoGate.Mods;
using JunimoGate.Tests;
using System.Text;

internal static class ModProfileV2Tests
{
    public static void CreatesSystemAndUserProfiles()
    {
        using var fixture = new Fixture();
        _ = new ModProfileRepository(fixture.Root)
            .OpenOrCreateAsync(ProfileId.Parse("default"))
            .AsTask().GetAwaiter().GetResult();

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
        TestHarness.Throws<InvalidOperationException>(() => fixture.Repository.DeleteAsync(
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

        TestHarness.True(fixture.Repository.DeleteAsync(ProfileId.Parse(profile.Id))
            .AsTask().GetAwaiter().GetResult());
        TestHarness.False(fixture.Repository.DeleteAsync(ProfileId.Parse(profile.Id))
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

    public static void MigratesLegacyDirectoriesWithoutRemovingFallback()
    {
        using var fixture = new Fixture();
        var profileId = ProfileId.Parse("default");
        var legacyRepository = new ModProfileRepository(fixture.Root);
        var legacy = legacyRepository.OpenOrCreateAsync(profileId).AsTask().GetAwaiter().GetResult();
        var layout = new ProfileLayout(fixture.Root, profileId);
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
        TestHarness.True(Directory.Exists(layout.EnabledDirectory));
        TestHarness.True(Directory.Exists(layout.DisabledDirectory));
        TestHarness.Equal(3, library.ReadAsync().AsTask().GetAwaiter().GetResult().Items.Count);

        var compatible = legacyRepository.ReadAsync(profileId).AsTask().GetAwaiter().GetResult();
        TestHarness.Equal(legacy.Revision, compatible.Revision);
        TestHarness.Equal(legacy.AssemblyBindingPolicy, compatible.AssemblyBindingPolicy);
        var updated = legacyRepository.UpdateBindingPolicyAsync(
                profileId,
                compatible.Revision,
                ModAssemblyBindingPolicy.Strict)
            .AsTask().GetAwaiter().GetResult();
        TestHarness.Equal(ModAssemblyBindingPolicy.Strict, updated.AssemblyBindingPolicy);
        TestHarness.Equal(2L, updated.Revision);
        TestHarness.Equal(
            ModAssemblyBindingPolicy.Strict,
            fixture.Repository.ReadAsync(profileId).AsTask().GetAwaiter().GetResult().AssemblyBindingPolicyOverride);

        var repeated = migrator.MigrateAsync(profileId, "Ignored").AsTask().GetAwaiter().GetResult();
        TestHarness.True(repeated.AlreadyMigrated);
        TestHarness.Equal(0, repeated.ImportedItems);
    }

    public static void RejectsAmbiguousLegacyEnabledMods()
    {
        using var fixture = new Fixture();
        var profileId = ProfileId.Parse("default");
        _ = new ModProfileRepository(fixture.Root).OpenOrCreateAsync(profileId)
            .AsTask().GetAwaiter().GetResult();
        var layout = new ProfileLayout(fixture.Root, profileId);
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

    public static void PersistsActiveProfileWithRevisionChecks()
    {
        using var fixture = new Fixture();
        var repository = new ActiveModProfileSelectionRepository(fixture.Root);
        var created = repository.OpenOrCreateAsync(ProfileId.Parse("default"))
            .AsTask().GetAwaiter().GetResult();
        TestHarness.Equal("default", created.ActiveProfileId);
        var updated = repository.SetAsync(created.Revision, ProfileId.Parse(ModProfileV2.NoModsId))
            .AsTask().GetAwaiter().GetResult();
        TestHarness.Equal(2L, updated.Revision);
        TestHarness.Equal(ModProfileV2.NoModsId, updated.ActiveProfileId);
        TestHarness.Throws<InvalidOperationException>(() => repository.SetAsync(
                created.Revision,
                ProfileId.Parse("default"))
            .AsTask().GetAwaiter().GetResult());
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

        public void Dispose()
        {
            Directory.Delete(Root, recursive: true);
            Directory.Delete(LibraryRoot, recursive: true);
        }
    }
}
