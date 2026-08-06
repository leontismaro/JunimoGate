using JunimoGate.Mods;
using JunimoGate.Tests;

internal static class ModManagementProjectionTests
{
    public static void ProjectsBundlesAndUnlockedMembers()
    {
        var (library, bundle, first, second, standalone) = CreateLibrary();
        var projection = ModManagementProjection.Create(library);
        TestHarness.Equal(2, projection.Items.Count);
        TestHarness.Equal(3, projection.ActualComponentCount);
        var parent = projection.Items.Single(item => item.IsBundle);
        TestHarness.Equal(bundle.BundleId, parent.Bundle!.BundleId);
        TestHarness.Equal(2, parent.Members.Count);
        TestHarness.True(projection.Items.Any(item =>
            item.Members.Count == 1 && item.Members[0].LibraryItemId == standalone.LibraryItemId));

        var unlocked = library with
        {
            BundleCatalog = library.BundleCatalog with
            {
                UnlockOverrides = new[] { new ModBundleUnlockOverride(bundle.FamilyKey, first.Manifest.UniqueId) },
            },
        };
        var split = ModManagementProjection.Create(unlocked);
        TestHarness.Equal(3, split.Items.Count);
        TestHarness.True(split.Items.All(item => !item.IsBundle));
        TestHarness.True(split.Items.Any(item => item.Members.Single().LibraryItemId == second.LibraryItemId));
    }

    public static void DiagnosesDependenciesWithoutBlocking()
    {
        var (library, bundle, _, _, dependencyV1) = CreateLibrary();
        var dependencyV2 = Item('d', "Shared.Framework", "2.0.0");
        library = library with { Items = library.Items.Append(dependencyV2).ToArray() };
        var parent = ModManagementProjection.Create(library).Items.Single(item => item.Bundle?.BundleId == bundle.BundleId);
        var profile = Profile(
            ModProfileMember.FromLibraryItem(dependencyV1, enabled: false));
        var disabled = ModDependencyAnalyzer.Analyze(parent, library, profile).Single();
        TestHarness.Equal(ModDependencyState.DisabledInProfile, disabled.State);
        TestHarness.True(disabled.IsRequired);
        TestHarness.Equal("2.0.0", disabled.MinimumVersion);
        TestHarness.Equal(2, disabled.Candidates.Count);

        profile = Profile(ModProfileMember.FromLibraryItem(dependencyV1, enabled: true));
        var mismatch = ModDependencyAnalyzer.Analyze(parent, library, profile).Single();
        TestHarness.Equal(ModDependencyState.VersionMismatch, mismatch.State);

        profile = Profile(ModProfileMember.FromLibraryItem(dependencyV2, enabled: true));
        var satisfied = ModDependencyAnalyzer.Analyze(parent, library, profile).Single();
        TestHarness.Equal(ModDependencyState.Satisfied, satisfied.State);

        var available = ModDependencyAnalyzer.Analyze(parent, library, Profile()).Single();
        TestHarness.Equal(ModDependencyState.AvailableMultipleVersions, available.State);
    }

    private static (ModLibraryIndex Library, ModBundleDefinition Bundle, ModLibraryItem First, ModLibraryItem Second, ModLibraryItem Standalone) CreateLibrary()
    {
        var first = Item('a', "Example.Product.Code", "1.0.0", [new ModDependencySummary("Shared.Framework", true, "1.5.0")]);
        var second = Item('b', "Example.Product.Content", "1.0.0", [new ModDependencySummary("Shared.Framework", true, "2.0.0")]);
        var standalone = Item('c', "Shared.Framework", "1.0.0");
        var now = DateTimeOffset.UtcNow;
        var bundle = new ModBundleDefinition(
            new string('e', 64),
            new string('f', 64),
            "Example Product",
            ModBundleOrigin.Detected,
            "example.zip",
            "Example Product",
            [
                new ModBundleMember(first.Manifest.UniqueId, first.LibraryItemId, "Example/Code"),
                new ModBundleMember(second.Manifest.UniqueId, second.LibraryItemId, "Example/Content"),
            ],
            now);
        var catalog = new ModBundleCatalog(
            ModBundleCatalog.CurrentSchema,
            1,
            now,
            [bundle],
            Array.Empty<ModBundleUnlockOverride>());
        var library = new ModLibraryIndex(ModLibraryIndex.CurrentSchema, 1, now, [first, second, standalone])
        {
            BundleCatalog = catalog,
        };
        return (library, bundle, first, second, standalone);
    }

    private static ModLibraryItem Item(
        char identity,
        string uniqueId,
        string version,
        IReadOnlyList<ModDependencySummary>? dependencies = null)
    {
        var id = new string(identity, 64);
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
                dependencies ?? Array.Empty<ModDependencySummary>()),
            $"library/{id}/files",
            DateTimeOffset.UtcNow,
            "test.zip",
            1,
            1);
    }

    private static ModProfileV2 Profile(params ModProfileMember[] members)
    {
        var now = DateTimeOffset.UtcNow;
        return new ModProfileV2(
            ModProfileV2.CurrentSchema,
            "default",
            "Default",
            1,
            null,
            members,
            now,
            now,
            null);
    }
}
