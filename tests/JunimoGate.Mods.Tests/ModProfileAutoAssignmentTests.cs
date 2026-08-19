using JunimoGate.Mods;
using JunimoGate.Tests;

internal static class ModProfileAutoAssignmentTests
{
    public static void AddsUniqueImportsWithoutReplacingExistingVersions()
    {
        using var fixture = new Fixture();
        var first = fixture.Item("Author.Existing", "1.0.0", 'a');
        var replacement = fixture.Item("Author.Existing", "2.0.0", 'b');
        var added = fixture.Item("Author.New", "1.0.0", 'c');
        fixture.WriteDefault([ModProfileMember.FromLibraryItem(first, enabled: true)]);

        var result = ModProfileAutoAssignment.AddImportedToActiveProfileAsync(
                fixture.Active,
                fixture.Profiles,
                [replacement, added])
            .AsTask().GetAwaiter().GetResult();
        TestHarness.Equal(1, result.AddedMembers);
        TestHarness.Equal(1, result.ExistingMembers);
        var profile = fixture.Profiles.ReadAsync(ProfileId.Parse("default")).AsTask().GetAwaiter().GetResult();
        TestHarness.Equal(first.LibraryItemId, profile.Members.Single(member => member.UniqueId == first.Manifest.UniqueId).LibraryItemId);
        TestHarness.Equal(added.LibraryItemId, profile.Members.Single(member => member.UniqueId == added.Manifest.UniqueId).LibraryItemId);
    }

    public static void LeavesAmbiguousVersionsInTheLibraryOnly()
    {
        using var fixture = new Fixture();
        var first = fixture.Item("Author.Ambiguous", "1.0.0", 'd');
        var second = fixture.Item("Author.Ambiguous", "2.0.0", 'e');
        fixture.WriteDefault([]);
        var result = ModProfileAutoAssignment.AddImportedToActiveProfileAsync(
                fixture.Active,
                fixture.Profiles,
                [first, second])
            .AsTask().GetAwaiter().GetResult();
        TestHarness.Equal(0, result.AddedMembers);
        TestHarness.Equal(1, result.AmbiguousUniqueIds);
        TestHarness.Equal(0, fixture.Profiles.ReadAsync(ProfileId.Parse("default")).AsTask().GetAwaiter().GetResult().Members.Count);
    }

    public static void ReconnectsAnExactMissingVersion()
    {
        using var fixture = new Fixture();
        var restored = fixture.Item("Author.Missing", "1.2.3", 'a');
        var now = DateTimeOffset.UtcNow;
        fixture.WriteDefault([new ModProfileMember(
            restored.Manifest.UniqueId,
            LibraryItemId: null,
            Enabled: true,
            "Old name",
            restored.Manifest.Version,
            "Old author",
            now)]);
        var result = ModProfileAutoAssignment.AddImportedToActiveProfileAsync(
                fixture.Active,
                fixture.Profiles,
                [restored])
            .AsTask().GetAwaiter().GetResult();
        TestHarness.Equal(1, result.AddedMembers);
        var member = fixture.Profiles.ReadAsync(ProfileId.Parse("default")).AsTask().GetAwaiter().GetResult().Members.Single();
        TestHarness.Equal(restored.LibraryItemId, member.LibraryItemId);
        TestHarness.True(member.Enabled);
        TestHarness.Equal(now, member.AddedAtUtc);
    }

    public static void DoesNotModifyTheNoModsProfile()
    {
        using var fixture = new Fixture();
        fixture.WriteDefault([]);
        var active = fixture.Active.OpenOrCreateAsync(ProfileId.Parse("default")).AsTask().GetAwaiter().GetResult();
        _ = fixture.Profiles.ListAsync().AsTask().GetAwaiter().GetResult();
        _ = fixture.Active.SetAsync(active.Revision, ProfileId.Parse(ModProfileV2.NoModsId)).AsTask().GetAwaiter().GetResult();
        var result = ModProfileAutoAssignment.AddImportedToActiveProfileAsync(
                fixture.Active,
                fixture.Profiles,
                [fixture.Item("Author.New", "1.0.0", 'f')])
            .AsTask().GetAwaiter().GetResult();
        TestHarness.True(result.BlockedByReadOnlyProfile);
    }

    public static void ReconnectsDanglingMembersWithoutReplacingValidBindings()
    {
        using var fixture = new Fixture();
        var retained = fixture.Item("Author.Retained", "1.0.0", 'a');
        var restored = fixture.Item("Author.Restored", "2.0.0", 'b');
        var oldId = new string('c', 64);
        var now = DateTimeOffset.UtcNow;
        fixture.WriteDefault([
            ModProfileMember.FromLibraryItem(retained, enabled: true),
            new ModProfileMember(
                restored.Manifest.UniqueId,
                oldId,
                Enabled: false,
                "Old restored name",
                restored.Manifest.Version,
                "Old author",
                now),
        ]);

        var count = ModProfileMissingMemberReconnector.ReconnectAsync(
                fixture.Profiles,
                [retained, restored])
            .AsTask().GetAwaiter().GetResult();

        TestHarness.Equal(1, count);
        var members = fixture.Profiles.ReadAsync(ProfileId.Parse("default")).AsTask().GetAwaiter().GetResult().Members;
        TestHarness.Equal(retained.LibraryItemId, members.Single(member => member.UniqueId == retained.Manifest.UniqueId).LibraryItemId);
        var reconnected = members.Single(member => member.UniqueId == restored.Manifest.UniqueId);
        TestHarness.Equal(restored.LibraryItemId, reconnected.LibraryItemId);
        TestHarness.False(reconnected.Enabled);
        TestHarness.Equal(now, reconnected.AddedAtUtc);
    }

    public static void LeavesAmbiguousDanglingMembersMissing()
    {
        using var fixture = new Fixture();
        var first = fixture.Item("Author.Ambiguous", "1.0.0", 'a');
        var second = fixture.Item("Author.Ambiguous", "1.0.0", 'b');
        fixture.WriteDefault([new ModProfileMember(
            first.Manifest.UniqueId,
            new string('c', 64),
            Enabled: true,
            "Expected",
            "1.0.0",
            "Author",
            DateTimeOffset.UtcNow)]);

        var count = ModProfileMissingMemberReconnector.ReconnectAsync(fixture.Profiles, [first, second])
            .AsTask().GetAwaiter().GetResult();

        TestHarness.Equal(0, count);
        TestHarness.Equal<string?>(null, fixture.Profiles.ReadAsync(ProfileId.Parse("default"))
            .AsTask().GetAwaiter().GetResult().Members.Single().LibraryItemId);
    }

    public static void DoesNotReconnectAgainstOnlyTheNewImportWhenTheLibraryIsAmbiguous()
    {
        using var fixture = new Fixture();
        var existing = fixture.Item("Author.Ambiguous", "1.0.0", 'a');
        var imported = fixture.Item("Author.Ambiguous", "1.0.0", 'b');
        fixture.WriteDefault([new ModProfileMember(
            imported.Manifest.UniqueId,
            LibraryItemId: null,
            Enabled: true,
            "Expected",
            "1.0.0",
            "Author",
            DateTimeOffset.UtcNow)]);

        var result = ModProfileAutoAssignment.AddImportedToActiveProfileAsync(
                fixture.Active,
                fixture.Profiles,
                [existing, imported],
                [imported])
            .AsTask().GetAwaiter().GetResult();

        TestHarness.Equal(0, result.AddedMembers);
        TestHarness.Equal(1, result.AmbiguousUniqueIds);
        TestHarness.Equal<string?>(null, fixture.Profiles.ReadAsync(ProfileId.Parse("default"))
            .AsTask().GetAwaiter().GetResult().Members.Single().LibraryItemId);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), $"junimogate-auto-profile-{Guid.NewGuid():N}");
        public Fixture()
        {
            Profiles = new ModProfileV2Repository(Path.Combine(root, "profiles"));
            Active = new ActiveModProfileSelectionRepository(Path.Combine(root, "profiles"));
        }

        public ModProfileV2Repository Profiles { get; }
        public ActiveModProfileSelectionRepository Active { get; }

        public ModLibraryItem Item(string uniqueId, string version, char content)
        {
            var id = new string(content, 64);
            return new ModLibraryItem(
                ModLibraryItem.CurrentSchema,
                id,
                id,
                new ModManifestSummary(
                    uniqueId,
                    "Tests",
                    version,
                    uniqueId,
                    null,
                    "entry.dll",
                    null,
                    Array.Empty<ModDependencySummary>()),
                $"library/{id}/files",
                DateTimeOffset.UtcNow,
                "test.zip",
                1,
                1);
        }

        public void WriteDefault(IReadOnlyList<ModProfileMember> members)
        {
            var directory = Path.Combine(root, "profiles", "default");
            Directory.CreateDirectory(directory);
            var now = DateTimeOffset.UtcNow;
            var profile = new ModProfileV2(
                ModProfileV2.CurrentSchema,
                "default",
                "Default",
                Revision: 1,
                AssemblyBindingPolicyOverride: null,
                members,
                now,
                now,
                Description: null);
            var options = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.General)
            {
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
                Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
            };
            File.WriteAllText(Path.Combine(directory, "profile.json"), System.Text.Json.JsonSerializer.Serialize(profile, options));
        }

        public void Dispose()
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}
