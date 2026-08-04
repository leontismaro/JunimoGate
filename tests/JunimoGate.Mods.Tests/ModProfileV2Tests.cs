using JunimoGate.Mods;
using JunimoGate.Tests;

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

    private sealed class Fixture : IDisposable
    {
        public Fixture()
        {
            Root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), $"junimogate-profile-v2-{Guid.NewGuid():N}"));
            Directory.CreateDirectory(Root);
            Repository = new ModProfileV2Repository(Root);
        }

        public string Root { get; }
        public ModProfileV2Repository Repository { get; }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
