using JunimoGate.Mods;
using JunimoGate.Tests;

internal static class ModBundleDetectorTests
{
    public static void SeparatesSveProductsAndFrameworks()
    {
        var candidates = new[]
        {
            Candidate("SVE/FarmTypeManager", "Farm Type Manager", "Esca", "1.26.1", "Esca.FarmTypeManager"),
            Candidate(
                "SVE/GrampletonFields",
                "Grampleton Fields",
                "FlashShifter",
                "1.0.0",
                "flashshifter.GrampletonFields",
                dependencies: [Dependency("FlashShifter.StardewValleyExpandedCP")]),
            Candidate(
                "SVE/Grandpa's Farm/[CP] Grandpa's Farm",
                "Grandpa's Farm",
                "FlashShifter",
                "1.15.8",
                "flashshifter.GrandpasFarm",
                dependencies:
                [
                    Dependency("FlashShifter.GrandpasFarmFTM"),
                    Dependency("FlashShifter.StardewValleyExpandedCP"),
                ]),
            Candidate(
                "SVE/Grandpa's Farm/[FTM] Grandpa's Farm",
                "Grandpa's Farm Forage Locations",
                "FlashShifter",
                "1.15.8",
                "FlashShifter.GrandpasFarmFTM",
                contentPackFor: "Esca.FarmTypeManager"),
            Candidate(
                "SVE/Stardew Valley Expanded/Stardew Valley Expanded Code",
                "Stardew Valley Expanded Code",
                "FlashShifter, Esca",
                "1.15.7",
                "FlashShifter.SVECode"),
            Candidate(
                "SVE/Stardew Valley Expanded/[CP] Stardew Valley Expanded",
                "Stardew Valley Expanded",
                "FlashShifter",
                "1.15.7",
                "FlashShifter.StardewValleyExpandedCP",
                dependencies:
                [
                    Dependency("FlashShifter.SVE-FTM"),
                    Dependency("FlashShifter.SVECode"),
                ]),
            Candidate(
                "SVE/Stardew Valley Expanded/[FTM] Stardew Valley Expanded",
                "Stardew Valley Expanded Farm Type Manager",
                "FlashShifter",
                "1.15.7",
                "FlashShifter.SVE-FTM",
                contentPackFor: "Esca.FarmTypeManager"),
        };

        var result = ModBundleDetector.Detect(candidates);

        TestHarness.Equal(2, result.Bundles.Count);
        TestHarness.Equal("Grandpa's Farm", result.Bundles[0].DisplayName);
        TestHarness.Equal(2, result.Bundles[0].Members.Count);
        TestHarness.Equal("Stardew Valley Expanded", result.Bundles[1].DisplayName);
        TestHarness.Equal(3, result.Bundles[1].Members.Count);
        TestHarness.Equal(2, result.Standalone.Count);
        TestHarness.True(result.Standalone.Any(item => item.Manifest.UniqueId == "Esca.FarmTypeManager"));
        TestHarness.True(result.Standalone.Any(item => item.Manifest.UniqueId == "flashshifter.GrampletonFields"));
    }

    public static void GroupsRidgesideWithoutAbsorbingDependencies()
    {
        var candidates = new[]
        {
            Candidate("Ridgeside Village/[CC] Ridgeside Village", "Ridgeside Village [Custom Companions component]", "Rafseazz", "2.5.17", "Rafseazz.RSVCC", contentPackFor: "PeacefulEnd.CustomCompanions"),
            Candidate("Ridgeside Village/[CP] Ridgeside Village", "Ridgeside Village [Content Patcher component]", "Rafseazz", "2.5.17", "Rafseazz.RSVCP", dependencies: [Dependency("Rafseazz.RSVCC", required: false), Dependency("Rafseazz.RSVFTM", required: false), Dependency("Rafseazz.RidgesideVillage"), Dependency("spacechase0.SpaceCore")]),
            Candidate("Ridgeside Village/[FTM] Ridgeside Village", "Ridgeside Village [Farm Type Manager component]", "Rafseazz", "2.5.17", "Rafseazz.RSVFTM", contentPackFor: "Esca.FarmTypeManager"),
            Candidate("Ridgeside Village/RidgesideVillage", "Ridgeside Village [SMAPI component]", "Rafseazz", "2.5.17", "Rafseazz.RidgesideVillage", dependencies: [Dependency("spacechase0.SpaceCore")]),
            Candidate("Ridgeside Village/SpaceCore", "SpaceCore", "spacechase0", "1.28.4", "spacechase0.SpaceCore"),
        };

        var result = ModBundleDetector.Detect(candidates);

        TestHarness.Equal(1, result.Bundles.Count);
        TestHarness.Equal(4, result.Bundles[0].Members.Count);
        TestHarness.Equal("Ridgeside Village", result.Bundles[0].DisplayName);
        TestHarness.Equal("spacechase0.SpaceCore", result.Standalone.Single().Manifest.UniqueId);
    }

    public static void LeavesUserCollectionsFlat()
    {
        var candidates = new[]
        {
            Candidate("Mods/ContentPatcher", "Content Patcher", "Pathoschild", "2.9.1", "Pathoschild.ContentPatcher"),
            Candidate("Mods/EarthyRecolour", "Earthy Recolour", "DaisyNiko", "1.0.0", "DaisyNiko.EarthyRecolour", contentPackFor: "Pathoschild.ContentPatcher"),
            Candidate("Mods/SeasonalCharacters", "Seasonal Cute Characters", "Poltergeister", "5.0.0", "Poltergeister.SeasonalCuteCharacters", contentPackFor: "Pathoschild.ContentPatcher"),
            Candidate("Mods/CJBCheatsMenu", "CJB Cheats Menu", "CJBok and Pathoschild", "1.36.2", "CJBok.CheatsMenu"),
            Candidate("Mods/CJBItemSpawner", "CJB Item Spawner", "CJBok and Pathoschild", "2.5.0", "CJBok.ItemSpawner"),
        };

        var result = ModBundleDetector.Detect(candidates);

        TestHarness.Equal(0, result.Bundles.Count);
        TestHarness.Equal(candidates.Length, result.Standalone.Count);
    }

    public static void GroupsEastScarpButLeavesBarberShopStandalone()
    {
        var candidates = new[]
        {
            Candidate("East Scarp REMASTERED/East Scarp C#", "East Scarp: C#", "atravita", "3.0.9", "atravita.EastScarp"),
            Candidate("East Scarp REMASTERED/East Scarp Core", "East Scarp: Locations", "LemurKat", "3.0.9", "Lemurkat.EastScarp", dependencies: [Dependency("atravita.EastScarp", required: false)]),
            Candidate("East Scarp REMASTERED/East Scarp FTM", "Forage Settings East Scarp", "LemurKat", "3.0.9", "Lemurkat.EastScarpe.FTM", contentPackFor: "Esca.FarmTypeManager"),
            Candidate("East Scarp REMASTERED/East Scarp NPCs", "East Scarp: NPCs", "LemurKat", "3.0.9", "Lemurkat.EastScarpNPCs", dependencies: [Dependency("Lemurkat.EastScarp")]),
            Candidate("East Scarp REMASTERED/ESBarberShop", "East Scarp: Barber Shop", "mushymato", "3.0.9", "ES.BarberShop", updateKeys: ["GitHub:Mushymato/ESBarberShop"]),
        };

        var result = ModBundleDetector.Detect(candidates);

        TestHarness.Equal(1, result.Bundles.Count);
        TestHarness.Equal(4, result.Bundles[0].Members.Count);
        TestHarness.Equal("East Scarp REMASTERED", result.Bundles[0].DisplayName);
        TestHarness.Equal("ES.BarberShop", result.Standalone.Single().Manifest.UniqueId);
    }

    public static void UsesOnlyCompleteValidUpdateKeys()
    {
        var same = ModBundleDetector.Detect(
        [
            Candidate("Package/One", "First Component", "One", "1.0.0", "Example.One", updateKeys: ["Nexus:1234"]),
            Candidate("Package/Two", "Second Component", "Two", "2.0.0", "Example.Two", updateKeys: ["nexus:1234"]),
        ]);
        TestHarness.Equal(1, same.Bundles.Count);

        var differentSubkeys = ModBundleDetector.Detect(
        [
            Candidate("Package/One", "First Component", "One", "1.0.0", "Example.One", updateKeys: ["Nexus:1234"]),
            Candidate("Package/Two", "Second Component", "Two", "2.0.0", "Example.Two", updateKeys: ["Nexus:1234@Optional"]),
            Candidate("Package/Three", "Third Component", "Three", "3.0.0", "Example.Three", updateKeys: ["Nexus:???"]),
        ]);
        TestHarness.Equal(0, differentSubkeys.Bundles.Count);
        TestHarness.Equal(3, differentSubkeys.Standalone.Count);

        var semanticVersions = ModBundleDetector.Detect(
        [
            Candidate("Example Product/Code", "Example Product Code", "Test", "1.0", "Example.Product.Code"),
            Candidate("Example Product/Content", "Example Product Content", "Test", "1.0.0", "Example.Product.Content"),
        ]);
        TestHarness.Equal(1, semanticVersions.Bundles.Count);
    }

    public static void LeavesDuplicateUniqueIdVersionsStandalone()
    {
        var result = ModBundleDetector.Detect(
        [
            Candidate("Package/Code-v1", "Example Product Code", "Test", "1.0.0", "Example.Product.Code", updateKeys: ["Nexus:1234"]),
            Candidate("Package/Code-v2", "Example Product Code", "Test", "2.0.0", "Example.Product.Code", updateKeys: ["Nexus:1234"]),
            Candidate("Package/Content", "Example Product Content", "Test", "2.0.0", "Example.Product.Content", updateKeys: ["Nexus:1234"]),
        ]);

        TestHarness.Equal(0, result.Bundles.Count);
        TestHarness.Equal(3, result.Standalone.Count);
    }

    private static ModArchiveCandidate Candidate(
        string root,
        string name,
        string author,
        string version,
        string uniqueId,
        IReadOnlyList<string>? updateKeys = null,
        IReadOnlyList<ModDependencySummary>? dependencies = null,
        string? contentPackFor = null)
    {
        var manifest = new ModManifestSummary(
            name,
            author,
            version,
            uniqueId,
            null,
            contentPackFor is null ? "Mod.dll" : null,
            contentPackFor,
            dependencies ?? Array.Empty<ModDependencySummary>())
        {
            UpdateKeys = updateKeys ?? Array.Empty<string>(),
        };
        return new ModArchiveCandidate(root, manifest, 2, 32, [$"{root}/manifest.json", $"{root}/content.bin"]);
    }

    private static ModDependencySummary Dependency(string uniqueId, bool required = true) =>
        new(uniqueId, required, null);
}
