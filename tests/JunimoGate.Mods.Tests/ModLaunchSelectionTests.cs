using JunimoGate.Mods;
using JunimoGate.Tests;

internal static class ModLaunchSelectionTests
{
    public static void FreezesOnlyEnabledLibraryItems()
    {
        var first = Item("Example.First", 'a');
        var second = Item("Example.Second", 'b');
        var missing = new ModProfileMember(
            "Example.Missing",
            null,
            Enabled: false,
            "Missing",
            "1.0.0",
            "Test",
            DateTimeOffset.UtcNow);
        var now = DateTimeOffset.UtcNow;
        var profile = new ModProfileV2(
            ModProfileV2.CurrentSchema,
            "main",
            "Main",
            Revision: 4,
            ModAssemblyBindingPolicy.Strict,
            new[]
            {
                ModProfileMember.FromLibraryItem(first, enabled: true),
                ModProfileMember.FromLibraryItem(second, enabled: false),
                missing,
            },
            now,
            now,
            null);
        var library = new ModLibraryIndex(
            ModLibraryIndex.CurrentSchema,
            Revision: 7,
            now,
            new[] { first, second });

        var selection = ModLaunchSelectionBuilder.Build(
            profile,
            library,
            ModAssemblyBindingPolicy.HighestCompatible);

        TestHarness.Equal(1, selection.Items.Count);
        TestHarness.Equal(first.LibraryItemId, selection.Items[0].LibraryItemId);
        TestHarness.Equal(ModAssemblyBindingPolicy.Strict, selection.AssemblyBindingPolicy);
        TestHarness.True(selection.Matches(profile, library));
        TestHarness.False(selection.Matches(profile with { Revision = 5 }, library));
        TestHarness.True(selection.Matches(profile, library with { Revision = 8 }));
        TestHarness.False(selection.Matches(
            profile,
            library with { Items = new[] { first with { ContentGeneration = 2 }, second } }));
    }

    public static void RejectsMissingEnabledMembers()
    {
        var now = DateTimeOffset.UtcNow;
        var profile = new ModProfileV2(
            ModProfileV2.CurrentSchema,
            "main",
            "Main",
            Revision: 1,
            null,
            new[]
            {
                new ModProfileMember(
                    "Example.Missing",
                    null,
                    Enabled: true,
                    "Missing",
                    "1.0.0",
                    "Test",
                    now),
            },
            now,
            now,
            null);
        var library = new ModLibraryIndex(
            ModLibraryIndex.CurrentSchema,
            Revision: 1,
            now,
            Array.Empty<ModLibraryItem>());

        TestHarness.Throws<InvalidDataException>(() => ModLaunchSelectionBuilder.Build(
            profile,
            library,
            ModAssemblyBindingPolicy.HighestCompatible));
    }

    public static void RejectsChangedSelectedGeneration()
    {
        var item = Item("Example.Generation", 'f');
        var now = DateTimeOffset.UtcNow;
        var profile = new ModProfileV2(
            ModProfileV2.CurrentSchema,
            "main",
            "Main",
            Revision: 1,
            null,
            new[] { ModProfileMember.FromLibraryItem(item, enabled: true) },
            now,
            now,
            null);
        var library = new ModLibraryIndex(
            ModLibraryIndex.CurrentSchema,
            Revision: 1,
            now,
            new[] { item });
        var selection = ModLaunchSelectionBuilder.Build(
            profile,
            library,
            ModAssemblyBindingPolicy.HighestCompatible);

        TestHarness.True(selection.Matches(profile, library));
        TestHarness.False(selection.Matches(
            profile,
            library with { Items = new[] { item with { ContentGeneration = 2 } } }));
    }

    public static void MatchesTheFrozenGlobalBindingPolicy()
    {
        var item = Item("Example.First", 'e');
        var now = DateTimeOffset.UtcNow;
        var profile = new ModProfileV2(
            ModProfileV2.CurrentSchema,
            "main",
            "Main",
            Revision: 2,
            AssemblyBindingPolicyOverride: null,
            new[] { ModProfileMember.FromLibraryItem(item, enabled: true) },
            now,
            now,
            null);
        var library = new ModLibraryIndex(
            ModLibraryIndex.CurrentSchema,
            Revision: 3,
            now,
            new[] { item });

        var selection = ModLaunchSelectionBuilder.Build(
            profile,
            library,
            ModAssemblyBindingPolicy.FirstLoaded);

        TestHarness.True(selection.Matches(profile, library, ModAssemblyBindingPolicy.FirstLoaded));
        TestHarness.False(selection.Matches(profile, library, ModAssemblyBindingPolicy.HighestCompatible));
    }

    public static void ResolvesOnlyContainedExistingRoots()
    {
        var root = Path.Combine(Path.GetTempPath(), $"junimogate-selection-{Guid.NewGuid():N}");
        try
        {
            var item = Item("Example.First", 'c');
            var path = Path.Combine(root, "library", item.LibraryItemId, "files");
            Directory.CreateDirectory(path);
            var now = DateTimeOffset.UtcNow;
            var selection = new ModLaunchSelectionSnapshot(
                ModLaunchSelectionSnapshot.CurrentSchema,
                new string('d', 32),
                "main",
                ProfileRevision: 1,
                LibraryRevision: 1,
                ModAssemblyBindingPolicy.HighestCompatible,
                new[] { new ModLaunchSelectionItem(item.LibraryItemId, item.Manifest.UniqueId, item.RelativeStoragePath) },
                now);

            var roots = ModLaunchSelectionPathResolver.ResolveExistingRoots(Path.GetFullPath(root), selection);
            TestHarness.Equal(1, roots.Count);
            TestHarness.Equal(Path.GetFullPath(path), roots[0]);
            Directory.Delete(path, recursive: true);
            TestHarness.Throws<InvalidDataException>(() =>
                ModLaunchSelectionPathResolver.ResolveExistingRoots(Path.GetFullPath(root), selection));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static ModLibraryItem Item(string uniqueId, char digestCharacter)
    {
        var id = new string(digestCharacter, 64);
        return new ModLibraryItem(
            ModLibraryItem.CurrentSchema,
            id,
            id,
            new ModManifestSummary(
                uniqueId,
                "Test",
                "1.0.0",
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
}
