using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using JunimoGate.Mods;
using JunimoGate.Tests;

internal static class ModLibraryTests
{
    public static void DiscoversMultipleNestedMods()
    {
        using var fixture = new ModLibraryFixture();
        using var archive = CreateArchive(
            ("bundle/Alpha/manifest.json", "\uFEFF" + ManifestWithCommentsAndTrailingComma("Example.Alpha", "1.0.0", "Alpha.dll")),
            ("bundle/Alpha/Alpha.dll", "alpha"),
            ("bundle/Beta/manifest.json", Manifest("Example.Beta", "2.0.0", contentPackFor: "Pathoschild.ContentPatcher")),
            ("bundle/Beta/content.json", "{}"),
            ("README.txt", "outside"));
        var transaction = fixture.Repository.CreateInstallTransaction("bundle.zip");
        try
        {
            transaction.ScanAsync(archive).AsTask().GetAwaiter().GetResult();
            var scan = transaction.ScanResult ?? throw new InvalidOperationException("The scan result is missing.");
            TestHarness.True(scan.CanCommit);
            TestHarness.Equal(2, scan.Candidates.Count);
            TestHarness.Equal(1, scan.IgnoredFileCount);
            TestHarness.Equal("Example.Alpha", scan.Candidates[0].Manifest.UniqueId);
            TestHarness.Equal("Example.Beta", scan.Candidates[1].Manifest.UniqueId);
            TestHarness.Equal("Pathoschild.ContentPatcher", scan.Candidates[1].Manifest.ContentPackForUniqueId);
        }
        finally
        {
            transaction.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    public static void AllowsRepeatedDirectoryEntries()
    {
        using var fixture = new ModLibraryFixture();
        using var archive = CreateArchive(
            ("Wrapped/", ""),
            ("Wrapped/", ""),
            ("Wrapped/Mod/manifest.json", Manifest("Example.Directory", "1.0.0", entryDll: "Mod.dll")),
            ("Wrapped/Mod/Mod.dll", "content"));
        var transaction = fixture.Repository.CreateInstallTransaction("directories.zip");
        try
        {
            transaction.ScanAsync(archive).AsTask().GetAwaiter().GetResult();
            TestHarness.True(transaction.ScanResult!.CanCommit);
            TestHarness.Equal(1, transaction.ScanResult.Candidates.Count);
        }
        finally
        {
            transaction.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    public static void RejectsUnsafeArchivePaths()
    {
        using var fixture = new ModLibraryFixture();
        using (var traversal = CreateArchive(
                   ("../evil.txt", "bad"),
                   ("Safe/manifest.json", Manifest("Example.Safe", "1.0.0", entryDll: "Safe.dll")),
                   ("Safe/Safe.dll", "safe")))
        {
            var transaction = fixture.Repository.CreateInstallTransaction();
            try
            {
                transaction.ScanAsync(traversal).AsTask().GetAwaiter().GetResult();
                TestHarness.False(transaction.ScanResult!.CanCommit);
                TestHarness.True(transaction.ScanResult.Issues.Any(issue => issue.Code == "unsafe_path"));
            }
            finally
            {
                transaction.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        }
    }

    public static void UsesOutermostManifestRoot()
    {
        using var fixture = new ModLibraryFixture();
        using var archive = CreateArchive(
            ("Bush/manifest.json", Manifest("Example.Bush", "1.0.0", entryDll: "Bush.dll")),
            ("Bush/Bush.dll", "root"),
            ("Bush/examples/Valid/manifest.json", Manifest("Example.Valid", "1.0.0", contentPackFor: "Example.Bush")),
            ("Bush/examples/Valid/content.json", "{}"),
            ("Bush/examples/Invalid/manifest.json", "{ invalid"),
            ("Bush/examples/Invalid/readme.txt", "example"));
        var limits = ModArchiveImportLimits.Default with { MaximumMods = 1 };
        var transaction = fixture.Repository.CreateInstallTransaction("nested-examples.zip", limits);
        try
        {
            transaction.ScanAsync(archive).AsTask().GetAwaiter().GetResult();
            var scan = transaction.ScanResult ?? throw new InvalidOperationException("The scan result is missing.");
            TestHarness.True(scan.CanCommit);
            TestHarness.Equal(1, scan.Candidates.Count);
            TestHarness.Equal(0, scan.IgnoredFileCount);
            var candidate = scan.Candidates.Single();
            TestHarness.Equal("Bush", candidate.RootPath);
            TestHarness.Equal("Example.Bush", candidate.Manifest.UniqueId);
            TestHarness.Equal(6, candidate.FileCount);
            TestHarness.True(candidate.EntryPaths.Contains("Bush/examples/Invalid/manifest.json"));

            transaction.CommitAsync().AsTask().GetAwaiter().GetResult();
            var imported = transaction.ImportResult?.AddedItems.Single()
                ?? throw new InvalidOperationException("The imported Mod is missing.");
            var files = fixture.Repository.Layout.GetItemFilesDirectory(imported.LibraryItemId);
            TestHarness.Equal("{ invalid", File.ReadAllText(Path.Combine(files, "examples", "Invalid", "manifest.json")));
        }
        finally
        {
            transaction.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    public static void RejectsTooManyOutermostManifestRoots()
    {
        using var fixture = new ModLibraryFixture();
        using var archive = CreateArchive(
            ("Alpha/manifest.json", Manifest("Example.Alpha", "1.0.0", entryDll: "Alpha.dll")),
            ("Alpha/Alpha.dll", "alpha"),
            ("Beta/manifest.json", Manifest("Example.Beta", "1.0.0", entryDll: "Beta.dll")),
            ("Beta/Beta.dll", "beta"));
        var limits = ModArchiveImportLimits.Default with { MaximumMods = 1 };
        var transaction = fixture.Repository.CreateInstallTransaction("two-mods.zip", limits);
        try
        {
            transaction.ScanAsync(archive).AsTask().GetAwaiter().GetResult();
            var scan = transaction.ScanResult ?? throw new InvalidOperationException("The scan result is missing.");
            TestHarness.False(scan.CanCommit);
            TestHarness.Equal(2, scan.Candidates.Count);
            TestHarness.True(scan.Issues.Any(issue => issue.Code == "too_many_mods"));
        }
        finally
        {
            transaction.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    public static void RejectsInvalidOutermostManifest()
    {
        using var fixture = new ModLibraryFixture();
        using var archive = CreateArchive(
            ("Parent/manifest.json", "{ invalid"),
            ("Parent/data.json", "{}"),
            ("Parent/Nested/manifest.json", Manifest("Example.Nested", "1.0.0", entryDll: "Nested.dll")),
            ("Parent/Nested/Nested.dll", "nested"));
        var transaction = fixture.Repository.CreateInstallTransaction();
        try
        {
            transaction.ScanAsync(archive).AsTask().GetAwaiter().GetResult();
            var scan = transaction.ScanResult ?? throw new InvalidOperationException("The scan result is missing.");
            TestHarness.False(scan.CanCommit);
            TestHarness.Equal(0, scan.Candidates.Count);
            TestHarness.True(scan.Issues.Any(issue =>
                issue.Code == "invalid_manifest" && issue.Path == "Parent/manifest.json"));
            TestHarness.False(scan.Issues.Any(issue => issue.Path == "Parent/Nested/manifest.json"));
        }
        finally
        {
            transaction.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    public static void IgnoresBlankUpdateKeys()
    {
        using var fixture = new ModLibraryFixture();
        using var archive = CreateArchive(
            ("Mod/manifest.json", """
                {"Name":"Example","Author":"Test","Version":"1.0.0","UniqueID":"Example.Keys","EntryDll":"Mod.dll","UpdateKeys":["", "   ", " Nexus:123 "]}
                """),
            ("Mod/Mod.dll", "content"));
        var transaction = fixture.Repository.CreateInstallTransaction();
        try
        {
            transaction.ScanAsync(archive).AsTask().GetAwaiter().GetResult();
            var scan = transaction.ScanResult ?? throw new InvalidOperationException("The scan result is missing.");
            TestHarness.True(scan.CanCommit);
            TestHarness.Equal(1, scan.Candidates.Count);
            TestHarness.Equal(1, scan.Candidates[0].Manifest.UpdateKeys.Count);
            TestHarness.Equal("Nexus:123", scan.Candidates[0].Manifest.UpdateKeys[0]);
        }
        finally
        {
            transaction.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    public static void RejectsMalformedUpdateKeys()
    {
        using var fixture = new ModLibraryFixture();
        using (var nonArray = CreateArchive(
                   ("Mod/manifest.json", """
                       {"Name":"Example","Author":"Test","Version":"1.0.0","UniqueID":"Example.Keys","EntryDll":"Mod.dll","UpdateKeys":{}}
                       """),
                   ("Mod/Mod.dll", "content")))
        {
            AssertInvalidManifest(fixture.Repository, nonArray);
        }

        using (var nonString = CreateArchive(
                   ("Mod/manifest.json", """
                       {"Name":"Example","Author":"Test","Version":"1.0.0","UniqueID":"Example.Keys","EntryDll":"Mod.dll","UpdateKeys":[123]}
                       """),
                   ("Mod/Mod.dll", "content")))
        {
            AssertInvalidManifest(fixture.Repository, nonString);
        }

        var longKey = new string('x', 4097);
        using var oversized = CreateArchive(
            ("Mod/manifest.json", $"{{\"Name\":\"Example\",\"Author\":\"Test\",\"Version\":\"1.0.0\",\"UniqueID\":\"Example.Keys\",\"EntryDll\":\"Mod.dll\",\"UpdateKeys\":[\"{longKey}\"]}}"),
            ("Mod/Mod.dll", "content"));
        AssertInvalidManifest(fixture.Repository, oversized);
    }

    private static void AssertInvalidManifest(ModLibraryRepository repository, Stream archive)
    {
        var transaction = repository.CreateInstallTransaction();
        try
        {
            transaction.ScanAsync(archive).AsTask().GetAwaiter().GetResult();
            TestHarness.False(transaction.ScanResult!.CanCommit);
            TestHarness.True(transaction.ScanResult.Issues.Any(issue => issue.Code == "invalid_manifest"));
        }
        finally
        {
            transaction.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    public static void ImportsAndReusesContent()
    {
        using var fixture = new ModLibraryFixture();
        using var firstArchive = SingleModArchive("Example.Import", "1.2.3", "first-content");
        var first = Import(fixture.Repository, firstArchive, "first.zip");
        TestHarness.Equal(1, first.AddedItems.Count);
        TestHarness.Equal(0, first.ReusedItems.Count);
        var item = first.AddedItems[0];
        TestHarness.False(item.LibraryItemId == item.ImportedContentId);
        TestHarness.True(File.Exists(Path.Combine(fixture.Repository.Layout.GetItemFilesDirectory(item.LibraryItemId), "Mod.dll")));
        TestHarness.True(File.Exists(fixture.Repository.Layout.GetItemMetadataPath(item.LibraryItemId)));
        var serializedMetadata = File.ReadAllText(fixture.Repository.Layout.GetItemMetadataPath(item.LibraryItemId));
        TestHarness.True(serializedMetadata.Contains("\"contentId\"", StringComparison.Ordinal));
        TestHarness.False(serializedMetadata.Contains("\"importedContentId\"", StringComparison.Ordinal));
        var reopened = new ModLibraryRepository(fixture.Repository.Layout.Root)
            .ReadAsync().AsTask().GetAwaiter().GetResult().Items.Single();
        TestHarness.Equal(item.ImportedContentId, reopened.ImportedContentId);

        var afterFirst = fixture.Repository.ReadAsync().AsTask().GetAwaiter().GetResult();
        TestHarness.Equal(2L, afterFirst.Revision);
        TestHarness.Equal(1, afterFirst.Items.Count);

        using var secondArchive = SingleModArchive("Example.Import", "1.2.3", "first-content");
        var second = Import(fixture.Repository, secondArchive, "renamed.zip");
        TestHarness.Equal(0, second.AddedItems.Count);
        TestHarness.Equal(1, second.ReusedItems.Count);
        TestHarness.Equal(item.LibraryItemId, second.ReusedItems[0].LibraryItemId);
        var afterSecond = fixture.Repository.ReadAsync().AsTask().GetAwaiter().GetResult();
        TestHarness.Equal(afterFirst.Revision, afterSecond.Revision);
        TestHarness.Equal(1, afterSecond.Items.Count);

        var generated = Path.Combine(fixture.Repository.Layout.GetItemFilesDirectory(item.LibraryItemId), "config.toml");
        File.WriteAllText(generated, "speed = 2");
        using var modifiedRetryArchive = SingleModArchive("Example.Import", "1.2.3", "first-content");
        var modifiedRetry = Import(fixture.Repository, modifiedRetryArchive, "original-again.zip");
        TestHarness.Equal(1, modifiedRetry.AddedItems.Count);
        TestHarness.Equal(0, modifiedRetry.ReusedItems.Count);
        var freshItem = modifiedRetry.AddedItems.Single();
        TestHarness.False(item.LibraryItemId == freshItem.LibraryItemId);
        TestHarness.Equal(item.ImportedContentId, freshItem.ImportedContentId);
        TestHarness.Equal("speed = 2", File.ReadAllText(generated));
        TestHarness.False(File.Exists(Path.Combine(
            fixture.Repository.Layout.GetItemFilesDirectory(freshItem.LibraryItemId),
            "config.toml")));
        var afterModifiedRetry = fixture.Repository.ReadAsync().AsTask().GetAwaiter().GetResult();
        TestHarness.Equal(checked(afterSecond.Revision + 1), afterModifiedRetry.Revision);
        TestHarness.Equal(2, afterModifiedRetry.Items.Count);

        Directory.Delete(fixture.Repository.Layout.GetItemDirectory(freshItem.LibraryItemId), recursive: true);
        using var repairArchive = SingleModArchive("Example.Import", "1.2.3", "first-content");
        var repaired = Import(fixture.Repository, repairArchive, "repair.zip");
        TestHarness.Equal(1, repaired.ReusedItems.Count);
        TestHarness.Equal(freshItem.LibraryItemId, repaired.ReusedItems.Single().LibraryItemId);
        TestHarness.True(Directory.Exists(fixture.Repository.Layout.GetItemDirectory(freshItem.LibraryItemId)));
        var afterRepair = fixture.Repository.ReadAsync().AsTask().GetAwaiter().GetResult();
        TestHarness.Equal(afterModifiedRetry.Revision + 1, afterRepair.Revision);
        TestHarness.Equal(
            freshItem.ContentGeneration + 1,
            afterRepair.Items.Single(candidate => candidate.LibraryItemId == freshItem.LibraryItemId).ContentGeneration);
    }

    public static void RepositoryInstancesShareChangeSignals()
    {
        using var fixture = new ModLibraryFixture();
        var second = new ModLibraryRepository(fixture.Root);
        var changed = 0;
        second.Changed += () => changed++;

        using var archive = CreateArchive(
            ("Mod/manifest.json", Manifest("Example.Signal", "1.0.0", entryDll: "Mod.dll")),
            ("Mod/Mod.dll", "signal"));
        var transaction = fixture.Repository.CreateInstallTransaction("signal.zip");
        try
        {
            transaction.ScanAsync(archive).AsTask().GetAwaiter().GetResult();
            transaction.CommitAsync().AsTask().GetAwaiter().GetResult();
        }
        finally
        {
            transaction.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        TestHarness.Equal(1, changed);
    }

    public static void RepairsIndexedItemsWithMissingFilesDirectory()
    {
        using var fixture = new ModLibraryFixture();
        using var archive = SingleModArchive("Example.Incomplete", "1.0.0", "original");
        var item = Import(fixture.Repository, archive, "original.zip").AddedItems.Single();
        var before = fixture.Repository.ReadAsync().AsTask().GetAwaiter().GetResult();
        var files = fixture.Repository.Layout.GetItemFilesDirectory(item.LibraryItemId);
        Directory.Delete(files, recursive: true);

        using var retry = SingleModArchive("Example.Incomplete", "1.0.0", "original");
        var repaired = Import(fixture.Repository, retry, "repair.zip");

        TestHarness.Equal(0, repaired.AddedItems.Count);
        TestHarness.Equal(1, repaired.ReusedItems.Count);
        TestHarness.Equal(item.LibraryItemId, repaired.ReusedItems.Single().LibraryItemId);
        TestHarness.True(File.Exists(Path.Combine(files, "Mod.dll")));
        var after = fixture.Repository.ReadAsync().AsTask().GetAwaiter().GetResult();
        var restored = after.Items.Single();
        TestHarness.Equal(before.Revision + 1, after.Revision);
        TestHarness.Equal(item.ContentGeneration + 1, restored.ContentGeneration);
        TestHarness.Equal(item.FileCount, restored.CurrentFileCount);
        TestHarness.Equal(item.TotalBytes, restored.CurrentTotalBytes);
    }

    public static void KeepsDistinctContentCandidates()
    {
        using var fixture = new ModLibraryFixture();
        using var firstArchive = SingleModArchive("Example.Versioned", "1.0.0", "first");
        using var secondArchive = SingleModArchive("Example.Versioned", "1.0.0", "second");
        var first = Import(fixture.Repository, firstArchive, "first.zip").AddedItems.Single();
        var second = Import(fixture.Repository, secondArchive, "second.zip").AddedItems.Single();
        TestHarness.False(first.LibraryItemId == second.LibraryItemId);
        var index = fixture.Repository.ReadAsync().AsTask().GetAwaiter().GetResult();
        TestHarness.Equal(2, index.Items.Count);
        TestHarness.True(index.Items.All(item => item.Manifest.UniqueId == "Example.Versioned"));
        TestHarness.True(index.Items.All(item => item.Manifest.Version == "1.0.0"));
    }

    public static void PersistsBundlesAndUnlocksMembers()
    {
        using var fixture = new ModLibraryFixture();
        using var archive = BundledArchive();
        var imported = Import(fixture.Repository, archive, "example-bundle.zip");
        TestHarness.Equal(2, imported.AddedItems.Count);
        TestHarness.Equal(1, imported.Bundles.Count);
        var bundle = imported.Bundles.Single();
        TestHarness.Equal(2, bundle.Members.Count);

        var afterImport = fixture.Repository.ReadAsync().AsTask().GetAwaiter().GetResult();
        TestHarness.Equal(1, afterImport.BundleCatalog.Bundles.Count);
        TestHarness.Equal(bundle.BundleId, afterImport.BundleCatalog.Bundles[0].BundleId);
        TestHarness.True(File.Exists(fixture.Repository.Layout.BundleCatalogPath));
        TestHarness.False(File.ReadAllText(fixture.Repository.Layout.IndexPath)
            .Contains("\"bundleCatalog\":", StringComparison.Ordinal));
        TestHarness.True(File.ReadAllText(fixture.Repository.Layout.IndexPath)
            .Contains("bundleCatalogFile", StringComparison.Ordinal));
        var libraryIndexBytes = File.ReadAllBytes(fixture.Repository.Layout.IndexPath);

        using var repeatedArchive = BundledArchive();
        var repeated = Import(fixture.Repository, repeatedArchive, "renamed-bundle.zip");
        TestHarness.Equal(0, repeated.Bundles.Count);
        var afterRepeat = fixture.Repository.ReadAsync().AsTask().GetAwaiter().GetResult();
        TestHarness.Equal(afterImport.Revision, afterRepeat.Revision);

        var firstMember = bundle.Members[0];
        var bundles = new ModBundleCatalogRepository(fixture.Repository);
        var unlocked = bundles.SetMemberUnlockedAsync(
                bundle.BundleId,
                firstMember.UniqueId,
                unlocked: true)
            .AsTask().GetAwaiter().GetResult();
        TestHarness.True(unlocked.Changed);
        TestHarness.False(unlocked.BundleRemainsVisible);
        TestHarness.Equal(1, unlocked.Library.BundleCatalog.UnlockOverrides.Count);
        TestHarness.Equal(afterImport.Revision, unlocked.Library.Revision);
        TestHarness.True(unlocked.Library.BundleCatalog.Revision > afterImport.BundleCatalog.Revision);
        TestHarness.True(libraryIndexBytes.SequenceEqual(File.ReadAllBytes(fixture.Repository.Layout.IndexPath)));

        var unchanged = bundles.SetMemberUnlockedAsync(
                bundle.BundleId,
                firstMember.UniqueId,
                unlocked: true)
            .AsTask().GetAwaiter().GetResult();
        TestHarness.False(unchanged.Changed);
        TestHarness.Equal(unlocked.Library.Revision, unchanged.Library.Revision);

        var restored = bundles.SetMemberUnlockedAsync(
                bundle.BundleId,
                firstMember.UniqueId,
                unlocked: false)
            .AsTask().GetAwaiter().GetResult();
        TestHarness.True(restored.Changed);
        TestHarness.True(restored.BundleRemainsVisible);
        TestHarness.Equal(0, restored.Library.BundleCatalog.UnlockOverrides.Count);

        fixture.Repository.DeleteAsync(bundle.Members[0].LibraryItemId)
            .AsTask().GetAwaiter().GetResult();
        var afterDelete = fixture.Repository.ReadAsync().AsTask().GetAwaiter().GetResult();
        TestHarness.Equal(1, afterDelete.Items.Count);
        TestHarness.Equal(0, afterDelete.BundleCatalog.Bundles.Count);
    }

    public static void MigratesEmbeddedBundleCatalog()
    {
        using var fixture = new ModLibraryFixture();
        using var archive = BundledArchive();
        var imported = Import(fixture.Repository, archive, "legacy-bundle.zip");
        var catalog = JsonNode.Parse(File.ReadAllText(fixture.Repository.Layout.BundleCatalogPath));
        var legacy = JsonNode.Parse(File.ReadAllText(fixture.Repository.Layout.IndexPath))!.AsObject();
        legacy.Remove("bundleCatalogFile");
        legacy["bundleCatalog"] = catalog;
        File.WriteAllText(fixture.Repository.Layout.IndexPath, legacy.ToJsonString());
        File.Delete(fixture.Repository.Layout.BundleCatalogPath);

        var reopened = new ModLibraryRepository(fixture.Root);
        var migrated = reopened.ReadAsync().AsTask().GetAwaiter().GetResult();
        TestHarness.Equal(imported.Bundles.Single().BundleId, migrated.BundleCatalog.Bundles.Single().BundleId);
        TestHarness.True(File.Exists(reopened.Layout.BundleCatalogPath));
        TestHarness.False(File.ReadAllText(reopened.Layout.IndexPath)
            .Contains("\"bundleCatalog\":", StringComparison.Ordinal));
    }

    public static void RejectsMissingBundleCatalog()
    {
        using var fixture = new ModLibraryFixture();
        using var archive = BundledArchive();
        _ = Import(fixture.Repository, archive, "missing-bundle-catalog.zip");
        File.Delete(fixture.Repository.Layout.BundleCatalogPath);

        TestHarness.Throws<InvalidDataException>(() =>
            new ModLibraryRepository(fixture.Root).ReadAsync().AsTask().GetAwaiter().GetResult());
    }

    public static void RecoversInterruptedCatalogCommit()
    {
        using var fixture = new ModLibraryFixture();
        using var archive = BundledArchive();
        var imported = Import(fixture.Repository, archive, "catalog-recovery.zip");
        var indexBefore = File.ReadAllBytes(fixture.Repository.Layout.IndexPath);
        var catalogBefore = File.ReadAllBytes(fixture.Repository.Layout.BundleCatalogPath);
        var transactionDirectory = Path.Combine(
            fixture.Repository.Layout.StagingDirectory,
            $"catalog-{Guid.NewGuid():N}");
        Directory.CreateDirectory(transactionDirectory);
        File.WriteAllBytes(Path.Combine(transactionDirectory, "library-index.before.json"), indexBefore);
        File.WriteAllBytes(Path.Combine(transactionDirectory, "bundle-catalog.before.json"), catalogBefore);
        File.WriteAllText(
            Path.Combine(transactionDirectory, "transaction.json"),
            "{\"schema\":\"junimogate-mod-catalog-commit/v1\",\"phase\":\"prepared\",\"hadLibraryIndex\":true,\"hadBundleCatalog\":true}");
        File.WriteAllText(fixture.Repository.Layout.IndexPath, "{}");
        File.WriteAllText(fixture.Repository.Layout.BundleCatalogPath, "{}");

        var reopened = new ModLibraryRepository(fixture.Root);
        var recovered = reopened.ReadAsync().AsTask().GetAwaiter().GetResult();
        TestHarness.Equal(imported.Bundles.Single().BundleId, recovered.BundleCatalog.Bundles.Single().BundleId);
        TestHarness.True(indexBefore.SequenceEqual(File.ReadAllBytes(reopened.Layout.IndexPath)));
        TestHarness.True(catalogBefore.SequenceEqual(File.ReadAllBytes(reopened.Layout.BundleCatalogPath)));
        TestHarness.False(Directory.Exists(transactionDirectory));
    }

    public static void MutatesBundleProfileMembersAtomically()
    {
        using var fixture = new ModLibraryFixture();
        using var archive = BundledArchive();
        var imported = Import(fixture.Repository, archive, "example-bundle.zip");
        var bundle = imported.Bundles.Single();
        var profiles = new ModProfileV2Repository(Path.Combine(fixture.Root, "profiles"));
        var profile = profiles.CreateAsync("Bundle group").AsTask().GetAwaiter().GetResult();
        var memberMutations = new ModProfileMemberMutationService(profiles);
        var service = new ModBundleProfileMutationService(fixture.Repository, memberMutations);

        var added = service.AddOrReplaceAsync(ProfileId.Parse(profile.Id), bundle.BundleId, enabled: true)
            .AsTask().GetAwaiter().GetResult();
        TestHarness.Equal(2, added.AddedMembers);
        TestHarness.Equal(profile.Revision + 1, added.Profile.Revision);
        TestHarness.True(added.Profile.Members.All(member => member.Enabled));
        TestHarness.False(added.Profile.Members.Any(member =>
            member.UniqueId.Equals("Pathoschild.ContentPatcher", StringComparison.OrdinalIgnoreCase)));

        var disabled = service.SetEnabledAsync(ProfileId.Parse(profile.Id), bundle.BundleId, enabled: false)
            .AsTask().GetAwaiter().GetResult();
        TestHarness.Equal(2, disabled.ChangedMembers);
        TestHarness.Equal(added.Profile.Revision + 1, disabled.Profile.Revision);

        var removed = service.RemoveAsync(ProfileId.Parse(profile.Id), bundle.BundleId)
            .AsTask().GetAwaiter().GetResult();
        TestHarness.Equal(2, removed.ChangedMembers);
        TestHarness.Equal(disabled.Profile.Revision + 1, removed.Profile.Revision);
        TestHarness.Equal(0, removed.Profile.Members.Count);
    }

    public static void DeletesExactItem()
    {
        using var fixture = new ModLibraryFixture();
        using var firstArchive = SingleModArchive("Example.Delete", "1.0.0", "first");
        using var secondArchive = SingleModArchive("Example.Delete", "2.0.0", "second");
        var first = Import(fixture.Repository, firstArchive, "first.zip").AddedItems.Single();
        var second = Import(fixture.Repository, secondArchive, "second.zip").AddedItems.Single();
        TestHarness.True(fixture.Repository.DeleteAsync(first.LibraryItemId).AsTask().GetAwaiter().GetResult());
        TestHarness.False(Directory.Exists(fixture.Repository.Layout.GetItemDirectory(first.LibraryItemId)));
        TestHarness.True(Directory.Exists(fixture.Repository.Layout.GetItemDirectory(second.LibraryItemId)));
        var index = fixture.Repository.ReadAsync().AsTask().GetAwaiter().GetResult();
        TestHarness.Equal(1, index.Items.Count);
        TestHarness.Equal(second.LibraryItemId, index.Items[0].LibraryItemId);
        TestHarness.False(fixture.Repository.DeleteAsync(first.LibraryItemId).AsTask().GetAwaiter().GetResult());
    }

    public static void DeletesManyInOneRevision()
    {
        using var fixture = new ModLibraryFixture();
        using var firstArchive = SingleModArchive("Example.DeleteMany.First", "1.0.0", "first");
        using var secondArchive = SingleModArchive("Example.DeleteMany.Second", "1.0.0", "second");
        using var keptArchive = SingleModArchive("Example.DeleteMany.Kept", "1.0.0", "kept");
        var first = Import(fixture.Repository, firstArchive, "first.zip").AddedItems.Single();
        var second = Import(fixture.Repository, secondArchive, "second.zip").AddedItems.Single();
        var kept = Import(fixture.Repository, keptArchive, "kept.zip").AddedItems.Single();
        var before = fixture.Repository.ReadAsync().AsTask().GetAwaiter().GetResult();

        var result = fixture.Repository.DeleteManyAsync(new[] { first.LibraryItemId, second.LibraryItemId })
            .AsTask().GetAwaiter().GetResult();

        TestHarness.Equal(2, result.DeletedItems.Count);
        TestHarness.Equal(0, result.MissingItemIds.Count);
        TestHarness.Equal(before.Revision + 1, result.Revision);
        var after = fixture.Repository.ReadAsync().AsTask().GetAwaiter().GetResult();
        TestHarness.Equal(before.Revision + 1, after.Revision);
        TestHarness.Equal(1, after.Items.Count);
        TestHarness.Equal(kept.LibraryItemId, after.Items[0].LibraryItemId);
    }

    public static void RollsBackBatchWhenAnItemDirectoryIsMissing()
    {
        using var fixture = new ModLibraryFixture();
        using var firstArchive = SingleModArchive("Example.Rollback.First", "1.0.0", "first");
        using var secondArchive = SingleModArchive("Example.Rollback.Second", "1.0.0", "second");
        var first = Import(fixture.Repository, firstArchive, "first.zip").AddedItems.Single();
        var second = Import(fixture.Repository, secondArchive, "second.zip").AddedItems.Single();
        var before = fixture.Repository.ReadAsync().AsTask().GetAwaiter().GetResult();
        Directory.Delete(fixture.Repository.Layout.GetItemDirectory(second.LibraryItemId), recursive: true);

        TestHarness.Throws<InvalidDataException>(() => fixture.Repository
            .DeleteManyAsync(new[] { first.LibraryItemId, second.LibraryItemId })
            .AsTask().GetAwaiter().GetResult());

        TestHarness.True(Directory.Exists(fixture.Repository.Layout.GetItemDirectory(first.LibraryItemId)));
        var after = fixture.Repository.ReadAsync().AsTask().GetAwaiter().GetResult();
        TestHarness.Equal(before.Revision, after.Revision);
        TestHarness.Equal(2, after.Items.Count);
    }

    public static void DeletionPreservesProfileMetadataPlaceholders()
    {
        using var fixture = new ModLibraryFixture();
        using var archive = SingleModArchive("Example.Placeholder", "3.2.1", "content");
        var item = Import(fixture.Repository, archive, "placeholder.zip").AddedItems.Single();
        var profiles = new ModProfileV2Repository(Path.Combine(fixture.Root, "profiles"));
        var profile = profiles.CreateAsync("Placeholder group").AsTask().GetAwaiter().GetResult();
        new ModProfileMemberMutationService(profiles)
            .AddOrReplaceAsync(ProfileId.Parse(profile.Id), new[] { item }, enabled: true)
            .AsTask().GetAwaiter().GetResult();

        fixture.Repository.DeleteManyAsync(new[] { item.LibraryItemId })
            .AsTask().GetAwaiter().GetResult();

        var retained = profiles.ReadAsync(ProfileId.Parse(profile.Id)).AsTask().GetAwaiter().GetResult()
            .Members.Single();
        TestHarness.Equal(item.LibraryItemId, retained.LibraryItemId);
        TestHarness.Equal(item.Manifest.Name, retained.ExpectedName);
        TestHarness.Equal(item.Manifest.Version, retained.ExpectedVersion);
        TestHarness.Equal(item.Manifest.Author, retained.ExpectedAuthor);
        TestHarness.True(retained.Enabled);
        TestHarness.Equal(0, fixture.Repository.ReadAsync().AsTask().GetAwaiter().GetResult().Items.Count);
    }

    public static void RepairsRecoverableLibraryState()
    {
        using var fixture = new ModLibraryFixture();
        using var archive = SingleModArchive("Example.Orphan", "1.0.0", "orphan");
        var item = Import(fixture.Repository, archive, "orphan.zip").AddedItems.Single();
        var source = fixture.Repository.Layout.GetItemDirectory(item.LibraryItemId);
        var backup = Path.Combine(fixture.Root, "orphan-backup");
        CopyDirectory(source, backup);
        TestHarness.True(fixture.Repository.DeleteAsync(item.LibraryItemId).AsTask().GetAwaiter().GetResult());
        Directory.Move(backup, source);

        using var retry = SingleModArchive("Example.Orphan", "1.0.0", "orphan");
        var recovered = Import(fixture.Repository, retry, "retry.zip");
        TestHarness.Equal(0, recovered.AddedItems.Count);
        TestHarness.Equal(1, recovered.ReusedItems.Count);
        var index = fixture.Repository.ReadAsync().AsTask().GetAwaiter().GetResult();
        TestHarness.Equal(1, index.Items.Count);
        TestHarness.Equal(item.LibraryItemId, index.Items[0].LibraryItemId);
    }

    public static void DoesNotRecoverAnEditedOrphanAsTheOriginalImport()
    {
        using var fixture = new ModLibraryFixture();
        using var archive = SingleModArchive("Example.EditedOrphan", "1.0.0", "original");
        var item = Import(fixture.Repository, archive, "original.zip").AddedItems.Single();
        var source = fixture.Repository.Layout.GetItemDirectory(item.LibraryItemId);
        var backup = Path.Combine(fixture.Root, "edited-orphan-backup");
        CopyDirectory(source, backup);
        TestHarness.True(fixture.Repository.DeleteAsync(item.LibraryItemId).AsTask().GetAwaiter().GetResult());
        Directory.Move(backup, source);
        File.WriteAllText(Path.Combine(
            fixture.Repository.Layout.GetItemFilesDirectory(item.LibraryItemId),
            "config.toml"), "edited = true");

        using var retry = SingleModArchive("Example.EditedOrphan", "1.0.0", "original");
        var imported = Import(fixture.Repository, retry, "retry.zip");

        TestHarness.Equal(1, imported.AddedItems.Count);
        TestHarness.Equal(0, imported.ReusedItems.Count);
        TestHarness.False(item.LibraryItemId == imported.AddedItems.Single().LibraryItemId);
        TestHarness.True(File.Exists(Path.Combine(
            fixture.Repository.Layout.GetItemFilesDirectory(item.LibraryItemId),
            "config.toml")));
        TestHarness.Equal(1, fixture.Repository.ReadAsync().AsTask().GetAwaiter().GetResult().Items.Count);
    }

    private static ModArchiveImportResult Import(
        ModLibraryRepository repository,
        Stream archive,
        string sourceName)
    {
        var transaction = repository.CreateInstallTransaction(sourceName);
        try
        {
            transaction.ScanAsync(archive).AsTask().GetAwaiter().GetResult();
            TestHarness.True(transaction.ScanResult!.CanCommit);
            transaction.CommitAsync().AsTask().GetAwaiter().GetResult();
            return transaction.ImportResult ?? throw new InvalidOperationException("The import result is missing.");
        }
        finally
        {
            transaction.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private static MemoryStream SingleModArchive(
        string uniqueId,
        string version,
        string content) =>
        CreateArchive(
            ("Wrapped/Mod/manifest.json", Manifest(uniqueId, version, entryDll: "Mod.dll")),
            ("Wrapped/Mod/Mod.dll", content),
            ("Wrapped/Mod/assets/data.json", "{}"));

    private static MemoryStream BundledArchive() =>
        CreateArchive(
            ("Wrapped/Example Product/Code/manifest.json", BundleManifest("Example.Product.Code", "Code.dll")),
            ("Wrapped/Example Product/Code/Code.dll", "code"),
            ("Wrapped/Example Product/Content/manifest.json", BundleManifest("Example.Product.Content", null)),
            ("Wrapped/Example Product/Content/content.json", "{}"));

    private static string BundleManifest(string uniqueId, string? entryDll)
    {
        var load = entryDll is not null
            ? $",\"EntryDll\":\"{entryDll}\""
            : ",\"ContentPackFor\":{\"UniqueID\":\"Pathoschild.ContentPatcher\"}";
        return $$"""
        {"Name":"Example Product {{uniqueId.Split('.').Last()}}","Author":"Test","Version":"1.0.0","UniqueID":"{{uniqueId}}","UpdateKeys":["Nexus:12345"]{{load}}}
        """;
    }

    private static MemoryStream CreateArchive(params (string Path, string Content)[] entries)
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var entry in entries)
            {
                var zipEntry = archive.CreateEntry(entry.Path, CompressionLevel.Fastest);
                using var output = zipEntry.Open();
                var bytes = Encoding.UTF8.GetBytes(entry.Content);
                output.Write(bytes);
            }
        }
        stream.Position = 0;
        return stream;
    }

    private static string Manifest(
        string uniqueId,
        string version,
        string? entryDll = null,
        string? contentPackFor = null)
    {
        var load = entryDll is not null
            ? $",\"EntryDll\":\"{entryDll}\""
            : $",\"ContentPackFor\":{{\"UniqueID\":\"{contentPackFor}\"}}";
        return $"{{\"Name\":\"{uniqueId}\",\"Author\":\"Test\",\"Version\":\"{version}\",\"UniqueID\":\"{uniqueId}\"{load}}}";
    }

    private static string ManifestWithCommentsAndTrailingComma(
        string uniqueId,
        string version,
        string entryDll) =>
        $$"""
        {
          // SMAPI manifests commonly use JSON comments and trailing commas.
          "Name": "{{uniqueId}}",
          "Author": "Test",
          "Version": "{{version}}",
          "UniqueID": "{{uniqueId}}",
          "EntryDll": "{{entryDll}}",
        }
        """;

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(destination, Path.GetRelativePath(source, file)));
    }

    private sealed class ModLibraryFixture : IDisposable
    {
        public ModLibraryFixture()
        {
            Root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), $"junimogate-mod-library-{Guid.NewGuid():N}"));
            Directory.CreateDirectory(Root);
            Repository = new ModLibraryRepository(Root);
        }

        public string Root { get; }
        public ModLibraryRepository Repository { get; }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
