using JunimoGate.Mods;
using JunimoGate.Tests;

return TestHarness.Run(
    ("SafeArchivePath normalizes separators and redundant slashes", () =>
    {
        TestHarness.Equal("Folder/Sub/mod.dll", SafeArchivePath.Parse("Folder\\Sub//mod.dll").Value);
        TestHarness.Equal("folder", SafeArchivePath.Parse("folder/").Value);
    }),
    ("SafeArchivePath rejects traversal", () =>
    {
        foreach (var candidate in new[] { "../mod.dll", "mods/../evil.dll", "./mod.dll", "mods/./mod.dll" })
        {
            TestHarness.False(SafeArchivePath.TryParse(candidate, out _), candidate);
        }
    }),
    ("SafeArchivePath rejects absolute drive and UNC paths", () =>
    {
        foreach (var candidate in new[] { "/etc/passwd", "\\rooted\\file", "C:\\mods\\mod.dll", "C:relative.dll", "\\\\server\\share\\mod.dll" })
        {
            TestHarness.False(SafeArchivePath.TryParse(candidate, out _), candidate);
        }
    }),
    ("SafeArchivePath rejects empty and NUL paths", () =>
    {
        TestHarness.False(SafeArchivePath.TryParse("", out _));
        TestHarness.False(SafeArchivePath.TryParse("   ", out _));
        TestHarness.False(SafeArchivePath.TryParse("mods/evil\0.dll", out _));
    }),
    ("SafeArchivePath rejects filesystem-sized segments", () =>
    {
        TestHarness.False(SafeArchivePath.TryParse(new string('a', 256) + "/file.dll", out _));
        TestHarness.True(SafeArchivePath.TryParse(new string('a', 255) + "/file.dll", out _));
    }),
    ("ProfileId enforces conservative stable syntax", () =>
    {
        TestHarness.Equal("farm-2", ProfileId.Parse("farm-2").Value);
        TestHarness.True(ProfileId.TryParse(new string('a', 64), out _));
        foreach (var candidate in new[] { "", "-farm", "Farm", "farm_name", new string('a', 65) })
        {
            TestHarness.False(ProfileId.TryParse(candidate, out _), candidate);
        }
    }),
    ("ProfileLayout produces absolute per-profile paths", () =>
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "junimogate-profiles"));
        var layout = new ProfileLayout(root, ProfileId.Parse("main"));
        var profile = Path.Combine(root, "main");
        TestHarness.Equal(Path.Combine(profile, "profile.json"), layout.ProfileJsonPath);
        TestHarness.Equal(Path.Combine(profile, "Mods"), layout.ModsDirectory);
        TestHarness.Equal(Path.Combine(profile, "Mods", "enabled"), layout.EnabledDirectory);
        TestHarness.Equal(Path.Combine(profile, "Mods", "disabled"), layout.DisabledDirectory);
        TestHarness.Equal(Path.Combine(profile, "downloads"), layout.DownloadsDirectory);
        TestHarness.Equal(Path.Combine(profile, "staging"), layout.StagingDirectory);
        TestHarness.True(Path.IsPathFullyQualified(layout.StagingDirectory));
        TestHarness.Throws<ArgumentException>(() => new ProfileLayout("relative/profiles", ProfileId.Parse("main")));
    }),
    ("Profile repository creates and updates a versioned default", () =>
    {
        using var fixture = new ProfileRepositoryFixture();
        var id = ProfileId.Parse("default");
        var created = fixture.Repository.OpenOrCreateAsync(id).AsTask().GetAwaiter().GetResult();
        TestHarness.Equal(ModProfile.CurrentSchema, created.Schema);
        TestHarness.Equal(1L, created.Revision);
        TestHarness.Equal(ModAssemblyBindingPolicy.HighestCompatible, created.AssemblyBindingPolicy);

        var updated = fixture.Repository.UpdateBindingPolicyAsync(
            id,
            created.Revision,
            ModAssemblyBindingPolicy.Strict).AsTask().GetAwaiter().GetResult();
        TestHarness.Equal(2L, updated.Revision);
        TestHarness.Equal(ModAssemblyBindingPolicy.Strict, updated.AssemblyBindingPolicy);
        TestHarness.Equal(updated, fixture.Repository.ReadAsync(id).AsTask().GetAwaiter().GetResult());
        var layout = new ProfileLayout(fixture.Root, id);
        Directory.Delete(layout.EnabledDirectory);
        _ = fixture.Repository.OpenOrCreateAsync(id).AsTask().GetAwaiter().GetResult();
        TestHarness.True(Directory.Exists(layout.EnabledDirectory));
        TestHarness.Throws<InvalidOperationException>(() => fixture.Repository.UpdateBindingPolicyAsync(
            id,
            created.Revision,
            ModAssemblyBindingPolicy.FirstLoaded).AsTask().GetAwaiter().GetResult());
    }),
    ("Profile repository rejects a malformed document", () =>
    {
        using var fixture = new ProfileRepositoryFixture();
        var id = ProfileId.Parse("broken");
        var layout = new ProfileLayout(fixture.Root, id);
        Directory.CreateDirectory(layout.ProfileDirectory);
        File.WriteAllText(layout.ProfileJsonPath, "{\"schema\":\"wrong\",\"id\":\"broken\"}");
        TestHarness.Throws<InvalidDataException>(() =>
            fixture.Repository.ReadAsync(id).AsTask().GetAwaiter().GetResult());
    }),
    ("Launcher settings create versioned defaults and update atomically", () =>
        LauncherSettingsTests.CreatesDefaultsAndUpdatesAtomically()),
    ("Launcher settings persist the manual update-check time", () =>
        LauncherSettingsTests.PersistsUpdateCheckTime()),
    ("Launcher settings reject malformed JSON", () =>
        LauncherSettingsTests.RejectsMalformedJson()),
    ("Launcher settings migrate the legacy default policy once", () =>
        LauncherSettingsTests.MigratesAndClearsTheDefaultProfileOverrideOnce()),
    ("Profile v2 repository preserves legacy profiles and creates groups", () =>
        ModProfileV2Tests.CreatesSystemAndUserProfiles()),
    ("Profile v2 repository updates members atomically", () =>
        ModProfileV2Tests.UpdatesMembersAtomically()),
    ("Profile v2 repository rejects duplicate members and deletes exactly", () =>
        ModProfileV2Tests.RejectsDuplicateMembersAndDeletesExactly()),
    ("Profile member mutations add replace and batch update atomically", () =>
        ModProfileV2Tests.MutatesProfileMembersAtomically()),
    ("Profile member mutations reject ambiguous versions and no-Mods", () =>
        ModProfileV2Tests.RejectsInvalidMemberMutations()),
    ("Profile v2 migration preserves the legacy fallback", () =>
        ModProfileV2Tests.MigratesLegacyDirectoriesWithoutRemovingFallback()),
    ("Profile v2 migration reuses resolved library identities", () =>
        ModProfileV2Tests.ReusesResolvedLibraryIdentityAcrossLegacyProfiles()),
    ("Profile v2 migration rejects ambiguous enabled Mods", () =>
        ModProfileV2Tests.RejectsAmbiguousLegacyEnabledMods()),
    ("Active Profile selection uses revision-checked updates", () =>
        ModProfileV2Tests.PersistsActiveProfileWithRevisionChecks()),
    ("Imported Mods join the active group without replacing versions", () =>
        ModProfileAutoAssignmentTests.AddsUniqueImportsWithoutReplacingExistingVersions()),
    ("Ambiguous imported versions remain library-only", () =>
        ModProfileAutoAssignmentTests.LeavesAmbiguousVersionsInTheLibraryOnly()),
    ("An exact imported version reconnects a missing group member", () =>
        ModProfileAutoAssignmentTests.ReconnectsAnExactMissingVersion()),
    ("Imported Mods cannot modify the no-Mods group", () =>
        ModProfileAutoAssignmentTests.DoesNotModifyTheNoModsProfile()),
    ("Mod launch selection freezes enabled library items", () =>
        ModLaunchSelectionTests.FreezesOnlyEnabledLibraryItems()),
    ("Mod launch selection rejects missing enabled members", () =>
        ModLaunchSelectionTests.RejectsMissingEnabledMembers()),
    ("Mod launch selection freezes the global binding policy", () =>
        ModLaunchSelectionTests.MatchesTheFrozenGlobalBindingPolicy()),
    ("Mod launch selection resolves contained existing roots", () =>
        ModLaunchSelectionTests.ResolvesOnlyContainedExistingRoots()),
    ("Mod Profile manifest roundtrip preserves placeholders", () =>
        ModProfileTransferTests.ManifestRoundtripPreservesPlaceholders()),
    ("Mod Profile v1 manifest without bundles remains importable", () =>
        ModProfileTransferTests.ImportsLegacyV1ManifestWithoutBundles()),
    ("Complete Mod Profile package excludes config and binds content", () =>
        ModProfileTransferTests.CompletePackageExcludesConfigAndBindsExportedContent()),
    ("Complete Mod Profile package includes config when requested", () =>
        ModProfileTransferTests.CompletePackageIncludesConfigWhenRequested()),
    ("Complete Mod Profile package rejects forged content identity", () =>
        ModProfileTransferTests.RejectsForgedPackageIdentityWithoutLibraryChanges()),
    ("Complete Mod Profile package supports an empty group", () =>
        ModProfileTransferTests.CompletePackageSupportsAnEmptyGroup()),
    ("Complete Mod Profile package preserves bundled Mods", () =>
        ModProfileTransferTests.CompletePackagePreservesBundles()),
    ("Mod archive scanner accepts common SMAPI JSON in nested Mods", () =>
        ModLibraryTests.DiscoversMultipleNestedMods()),
    ("Mod bundle detector separates SVE products and frameworks", () =>
        ModBundleDetectorTests.SeparatesSveProductsAndFrameworks()),
    ("Mod bundle detector groups Ridgeside without shared dependencies", () =>
        ModBundleDetectorTests.GroupsRidgesideWithoutAbsorbingDependencies()),
    ("Mod bundle detector leaves a user collection flat", () =>
        ModBundleDetectorTests.LeavesUserCollectionsFlat()),
    ("Mod bundle detector excludes an unrelated East Scarp add-on", () =>
        ModBundleDetectorTests.GroupsEastScarpButLeavesBarberShopStandalone()),
    ("Mod bundle detector compares complete valid update keys", () =>
        ModBundleDetectorTests.UsesOnlyCompleteValidUpdateKeys()),
    ("Mod bundle detector leaves duplicate UniqueID versions standalone", () =>
        ModBundleDetectorTests.LeavesDuplicateUniqueIdVersionsStandalone()),
    ("Mod archive scanner accepts repeated directory entries", () =>
        ModLibraryTests.AllowsRepeatedDirectoryEntries()),
    ("Mod archive scanner rejects traversal and overlapping roots", () =>
        ModLibraryTests.RejectsUnsafeArchiveShapes()),
    ("Mod library imports atomically and reuses identical content", () =>
        ModLibraryTests.ImportsAndReusesContent()),
    ("Mod files list and edit the actual private directory", () =>
        ModFileServiceTests.ListsAndEditsActualPrivateFiles()),
    ("Mod files reject unsafe protected and concurrent writes", () =>
        ModFileServiceTests.RejectsUnsafeProtectedAndConcurrentWrites()),
    ("Mod files create text files in the current directory", () =>
        ModFileServiceTests.CreatesTextFilesInTheCurrentDirectory()),
    ("Mod files accept UTF-8 split at the text probe boundary", () =>
        ModFileServiceTests.AcceptsUtf8SplitAtTheProbeBoundary()),
    ("Mod translations map member roots without archive-name guesses", () =>
        ModTranslationInstallTests.MapsMemberRootsWithoutUsingArchiveNames()),
    ("Mod translations map multiple bundle members atomically", () =>
        ModTranslationInstallTests.MapsMultipleBundleMembersAndLeavesWeakFilesUnmapped()),
    ("Mod translations map flat locales to one structural target", () =>
        ModTranslationInstallTests.MapsFlatLocalesOnlyToOneStructuralTarget()),
    ("Mod translations reject wrong manifests and ignore executables", () =>
        ModTranslationInstallTests.RejectsWrongManifestAndIgnoresExecutablePayloads()),
    ("Mod translations restore files and detect later conflicts", () =>
        ModTranslationInstallTests.RestoresAddedAndReplacedFilesWithConflictChecks()),
    ("Mod translations support contained manual mappings", () =>
        ModTranslationInstallTests.SupportsContainedManualMappings()),
    ("Mod deletion removes its translation records", () =>
        ModTranslationInstallTests.DeletesItemsAndTheirTranslationRecords()),
    ("Mod deletion preserves translated bundle members not deleted", () =>
        ModTranslationInstallTests.DeletingOneTranslatedBundleMemberPreservesTheOther()),
    ("Interrupted translation restore recovers files and its record", () =>
        ModTranslationInstallTests.RecoversInterruptedRestoreWithItsInstallationRecord()),
    ("Mod library instances serialize translation recovery for one root", () =>
        ModTranslationInstallTests.SerializesRepositoriesForTheSameRoot()),
    ("Mod translations accept Unix mode-only ZIP directories", () =>
        ModTranslationInstallTests.AcceptsUnixModeOnlyDirectories()),
    ("Mod translation commit rejects targets changed after preview", () =>
        ModTranslationInstallTests.RejectsTargetsChangedAfterPreview()),
    ("Mod library keeps same-version different-content items", () =>
        ModLibraryTests.KeepsDistinctContentCandidates()),
    ("Mod library persists bundles and unlock overrides", () =>
        ModLibraryTests.PersistsBundlesAndUnlocksMembers()),
    ("Mod bundle Profile operations mutate once without dependencies", () =>
        ModLibraryTests.MutatesBundleProfileMembersAtomically()),
    ("Mod management projects bundles and unlocked members", () =>
        ModManagementProjectionTests.ProjectsBundlesAndUnlockedMembers()),
    ("Mod management diagnoses dependencies without blocking", () =>
        ModManagementProjectionTests.DiagnosesDependenciesWithoutBlocking()),
    ("Mod library repairs missing and orphaned item directories", () =>
        ModLibraryTests.RepairsRecoverableLibraryState()),
    ("Mod library deletes one exact item", () =>
        ModLibraryTests.DeletesExactItem()),
    ("Mod library deletes many in one revision", () =>
        ModLibraryTests.DeletesManyInOneRevision()),
    ("Mod library rolls back incomplete batch", () =>
        ModLibraryTests.RollsBackBatchWhenAnItemDirectoryIsMissing()),
    ("Mod library deletion preserves Profile placeholders", () =>
        ModLibraryTests.DeletionPreservesProfileMetadataPlaceholders()),
    ("Game play sessions exclude startup time", () =>
        GamePlaySessionTests.DoesNotCountStartupTime()),
    ("Game play sessions count only running foreground intervals", () =>
        GamePlaySessionTests.CountsOnlyRunningForegroundIntervals()),
    ("Game play sessions cap inactive uncheckpointed time", () =>
        GamePlaySessionTests.CapsAnInactiveUncheckpointedSession()),
    ("Game play sessions archive stale current state", () =>
        GamePlaySessionTests.ArchivesAStaleSessionBeforeBeginningAnother()),
    ("Game play sessions record failures atomically", () =>
        GamePlaySessionTests.RecordsFailureAndRemovesCurrentSession()),
    ("Game play sessions prune history after completion", () =>
        GamePlaySessionTests.PrunesHistoryAfterCompletingEachSession()),
    ("Game play sessions reject malformed JSON", () =>
        GamePlaySessionTests.RejectsMalformedSessionJson()),
    ("SMAPI binding planner ignores files outside the real dependency closure", () =>
        AssemblyBindingPlannerTests.IgnoresUnreferencedFiles()),
    ("SMAPI binding planner ignores non-local framework references", () =>
        AssemblyBindingPlannerTests.IgnoresNonLocalFrameworkReferences()),
    ("SMAPI binding planner retains analyzed assembly bytes", () =>
        AssemblyBindingPlannerTests.RetainsTheAnalyzedSourceSnapshot()),
    ("SMAPI binding planner isolates a malformed referenced dependency", () =>
        AssemblyBindingPlannerTests.IsolatesMalformedDependencies()),
    ("SMAPI Strict binding shares identical bytes and rejects different bytes", () =>
        AssemblyBindingPlannerTests.StrictUsesByteIdentity()),
    ("SMAPI FirstLoaded binding uses stable Mod ID order", () =>
        AssemblyBindingPlannerTests.FirstLoadedIsStable()),
    ("SMAPI HighestCompatible selects the highest compatible ABI", () =>
        AssemblyBindingPlannerTests.HighestCompatibleValidatesConsumerReferences()),
    ("SMAPI HighestCompatible rejects different highest-version ties", () =>
        AssemblyBindingPlannerTests.HighestCompatibleRejectsAmbiguousTies()),
    ("SMAPI HighestCompatible preserves assembly scope in ABI signatures", () =>
        AssemblyBindingPlannerTests.HighestCompatiblePreservesTypeAssemblyScope()),
    ("SMAPI HighestCompatible resolves inherited ABI members", () =>
        AssemblyBindingPlannerTests.HighestCompatibleResolvesInheritedMembers()),
    ("SMAPI Mod rewrite cache keys source and rewrite context", () =>
        ModRewriteCacheTests.HitsOnlyForTheSameSourceAndContext()),
    ("SMAPI Mod rewrite cache stores warning-free unchanged results", () =>
        ModRewriteCacheTests.StoresAnUnchangedAnalysisResult()),
    ("SMAPI Mod rewrite cache keys PDB bytes and replays safe warnings", () =>
        ModRewriteCacheTests.KeysExternalSymbolsAndReplaysWarnings()),
    ("SMAPI Mod rewrite cache rejects malformed entries safely", () =>
        ModRewriteCacheTests.RejectsMalformedEntriesAsSafeMisses()),
    ("SMAPI Mod rewrite cache does not publish warning results", () =>
        ModRewriteCacheTests.DoesNotPublishNonCacheableResults()),
    ("SMAPI platform mapping rewrites nested custom-attribute type scopes", () =>
        CustomAttributeTypeScopeRewriterTests.RewritesNestedTypeArguments()),
    ("SMAPI platform mapping includes publicly visible nested types", () =>
        CustomAttributeTypeScopeRewriterTests.IncludesOnlyPubliclyVisibleNestedTypes()),
    ("SMAPI Android loading log keeps the newest bounded lines", () =>
        AndroidLoadingLogBufferTests.KeepsNewestLinesInDisplayOrder()),
    ("SMAPI Android loading log preserves platform newline semantics", () =>
        AndroidLoadingLogBufferTests.SplitsPlatformNewlinesWithoutLosingEmptyLines()),
    ("SMAPI Android loading log limits snapshots and clears state", () =>
        AndroidLoadingLogBufferTests.LimitsSnapshotsAndClearsState()),
    ("SMAPI Android background tracker follows blocked work", () =>
        AndroidBackgroundTaskTrackerTests.TracksBlockedWorkUntilCompletion()),
    ("SMAPI Android background tracker releases failed work", () =>
        AndroidBackgroundTaskTrackerTests.ReleasesFailedWork()),
    ("SMAPI Android Mod entry queue pumps one task in FIFO order", () =>
        AndroidMainThreadTaskQueueTests.RunsOneQueuedTaskPerPumpInFifoOrder()),
    ("SMAPI Android Mod entry queue preserves task failures", () =>
        AndroidMainThreadTaskQueueTests.PreservesTaskFailureForTheWaitingProducer()),
    ("SMAPI Android main-thread queue executes reentrant work inline", () =>
        AndroidMainThreadTaskQueueTests.ExecutesInlineWhenAlreadyOnTheGameThread()),
    ("SMAPI Android main-thread queue releases pending work on reset", () =>
        AndroidMainThreadTaskQueueTests.ResetFaultsPendingProducers()),
    ("SMAPI Android save serializer uses the native fallback", () =>
        AndroidSaveSerializerRegistryTests.UsesTheNativeSerializerUntilOverridden()),
    ("SMAPI Android save serializer publishes overrides", () =>
        AndroidSaveSerializerRegistryTests.PublishesOverridesThroughTheGameLookup()),
    ("SMAPI Android save serializer rolls back rejected overrides", () =>
        AndroidSaveSerializerRegistryTests.RollsBackAnUnobservableOverride()),
    ("SMAPI Android save serializer rejects ambiguous caches", () =>
        AndroidSaveSerializerRegistryTests.RejectsAnAmbiguousCacheShape()),
    ("SMAPI Android culture policy covers Indonesian game threads", () =>
        AndroidCulturePolicyTests.AppliesInvariantDataCultureToGameThreads()),
    ("GameHost reads v7 snapshots without binding the SMAPI bundle", () =>
        GameLaunchSchemaTests.ReadsLegacySnapshotWithoutBundleIdentity()),
    ("GameHost rejects unsupported snapshot schemas", () =>
        GameLaunchSchemaTests.RejectsUnknownSnapshotSchemas()),
    ("GameHost snapshots omit the SMAPI bundle identity", () =>
        GameLaunchSchemaTests.DoesNotPersistSmapiBundleIdentity()),
    ("GameHost retains pending v4 descriptor compatibility", () =>
        GameLaunchSchemaTests.RetainsThePreviousDescriptorSchemaForPendingLaunches()));

internal sealed class ProfileRepositoryFixture : IDisposable
{
    public ProfileRepositoryFixture()
    {
        Root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), $"junimogate-profiles-{Guid.NewGuid():N}"));
        Directory.CreateDirectory(Root);
        Repository = new ModProfileRepository(Root);
    }

    public string Root { get; }
    public ModProfileRepository Repository { get; }

    public void Dispose() => Directory.Delete(Root, recursive: true);
}
