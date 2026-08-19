using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.Json;
using JunimoGate.Mods;
using JunimoGate.Tests;

internal static class ModTranslationInstallTests
{
    public static void MapsMemberRootsWithoutUsingArchiveNames()
    {
        using var fixture = new Fixture();
        var item = fixture.ImportSingle(
            "DazUki.UIInfoSuite2Alt",
            "UIInfoSuite2Alt",
            ("i18n/default.json", "{\"First\":\"One\",\"Second\":\"Two\"}"),
            ("assets/icon.png", "original"));
        using var translation = Archive(
            ("Completely Unrelated Wrapper/UIInfoSuite2Alt/i18n/zh.json", "{\"First\":\"一\",\"Extra\":\"额外\"}"));

        var transaction = fixture.Translations.CreateInstallTransaction(
            [ModTranslationTarget.FromLibraryItem(item)],
            "irrelevant-download-name.zip");
        try
        {
            transaction.ScanAsync(translation).AsTask().GetAwaiter().GetResult();
            var scan = transaction.ScanResult!;
            TestHarness.True(scan.CanCommit);
            TestHarness.Equal(1, scan.AddedFiles);
            TestHarness.Equal("i18n/zh.json", scan.Files.Single().TargetPath);
            var diagnostic = scan.LocaleDiagnostics.Single();
            TestHarness.Equal(1, diagnostic.MatchingKeys);
            TestHarness.Equal(1, diagnostic.MissingKeys);
            TestHarness.Equal(1, diagnostic.UnknownKeys);

            transaction.CommitAsync().AsTask().GetAwaiter().GetResult();
            var root = fixture.Repository.Layout.GetItemFilesDirectory(item.LibraryItemId);
            TestHarness.True(File.ReadAllText(Path.Combine(root, "i18n", "zh.json")).Contains("一", StringComparison.Ordinal));
            TestHarness.True(Directory.Exists(Path.Combine(
                fixture.Repository.Layout.TranslationsDirectory,
                transaction.InstallResult!.InstallationId)));
        }
        finally
        {
            transaction.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    public static void MapsMultipleBundleMembersAndLeavesWeakFilesUnmapped()
    {
        using var fixture = new Fixture();
        var imported = fixture.ImportBundle();
        var index = fixture.Repository.ReadAsync().AsTask().GetAwaiter().GetResult();
        var bundle = imported.Bundles.Single();
        var targets = bundle.Members.Select(member => ModTranslationTarget.FromLibraryItem(
            index.Items.Single(item => item.LibraryItemId == member.LibraryItemId),
            member.OriginalRootPath)).ToArray();
        using var translation = Archive(
            ("Example Product/Code/i18n/zh.json", "{\"Code\":\"代码\"}"),
            ("Example Product/Content/i18n/zh.json", "{\"Content\":\"内容\"}"),
            ("help.txt", "not part of any Mod"));

        var transaction = fixture.Translations.CreateInstallTransaction(targets, "bundle-language.zip");
        try
        {
            transaction.ScanAsync(translation).AsTask().GetAwaiter().GetResult();
            var scan = transaction.ScanResult!;
            TestHarness.True(scan.CanCommit, string.Join(" | ", scan.Issues.Select(issue =>
                $"{issue.Severity}:{issue.Code}:{issue.Path}:{issue.Detail}")));
            TestHarness.Equal(2, scan.Files.Count);
            TestHarness.Equal(1, scan.UnmappedFiles.Count);
            TestHarness.Equal("help.txt", scan.UnmappedFiles[0]);
            TestHarness.Equal(2, scan.Files.Select(file => file.LibraryItemId).Distinct().Count());
            transaction.CommitAsync().AsTask().GetAwaiter().GetResult();
            foreach (var plan in scan.Files)
            {
                TestHarness.True(File.Exists(Path.Combine(
                    fixture.Repository.Layout.GetItemFilesDirectory(plan.LibraryItemId),
                    plan.TargetPath.Replace('/', Path.DirectorySeparatorChar))),
                    $"Missing committed translation: {plan.LibraryItemId}/{plan.TargetPath}");
            }
        }
        finally
        {
            transaction.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    public static void MapsFlatLocalesOnlyToOneStructuralTarget()
    {
        using var fixture = new Fixture();
        var target = fixture.ImportSingle(
            "spacechase0.GenericModConfigMenu",
            "GenericModConfigMenu",
            ("i18n/default.json", "{\"Key\":\"Default\"}"),
            ("i18n/zh.json", "old"));
        var similar = fixture.ImportSingle(
            "Priff13.GenericModConfigMenuAndroid",
            "GenericModConfigMenuAndroid",
            ("assets/icon.png", "other"));
        using var translation = Archive(("zh.json", "{\"Key\":\"新\"}"));
        var transaction = fixture.Translations.CreateInstallTransaction(
            [ModTranslationTarget.FromLibraryItem(target), ModTranslationTarget.FromLibraryItem(similar)],
            "flat.zip");
        try
        {
            transaction.ScanAsync(translation).AsTask().GetAwaiter().GetResult();
            var plan = transaction.ScanResult!.Files.Single();
            TestHarness.Equal(target.LibraryItemId, plan.LibraryItemId);
            TestHarness.Equal(ModTranslationFileAction.Replace, plan.Action);
            transaction.CommitAsync().AsTask().GetAwaiter().GetResult();
            TestHarness.True(File.ReadAllText(Path.Combine(
                fixture.Repository.Layout.GetItemFilesDirectory(target.LibraryItemId), "i18n", "zh.json")).Contains("新", StringComparison.Ordinal));
        }
        finally
        {
            transaction.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    public static void RejectsWrongManifestAndIgnoresExecutablePayloads()
    {
        using var fixture = new Fixture();
        var target = fixture.ImportSingle(
            "Example.Target",
            "Target",
            ("i18n/default.json", "{\"Key\":\"Default\"}"));
        using var wrong = Archive(
            ("Target/manifest.json", Manifest("Example.Other", "Other.dll")),
            ("Target/Other.dll", "binary"),
            ("Target/i18n/zh.json", "{\"Key\":\"值\"}"));
        var transaction = fixture.Translations.CreateInstallTransaction(
            [ModTranslationTarget.FromLibraryItem(target)],
            "wrong.zip");
        try
        {
            transaction.ScanAsync(wrong).AsTask().GetAwaiter().GetResult();
            TestHarness.False(transaction.ScanResult!.CanCommit);
            TestHarness.True(transaction.ScanResult.Issues.Any(issue => issue.Code == "manifest_target_mismatch"));
            TestHarness.True(transaction.ScanResult.Issues.Any(issue => issue.Code == "protected_file_ignored"));
        }
        finally
        {
            transaction.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    public static void RestoresAddedAndReplacedFilesWithConflictChecks()
    {
        using var fixture = new Fixture();
        var target = fixture.ImportSingle(
            "Example.Restore",
            "Restore",
            ("i18n/default.json", "{\"Key\":\"Default\"}"),
            ("i18n/zh.json", "original"));
        using var translation = Archive(
            ("Restore/i18n/zh.json", "translated"),
            ("Restore/assets/new.png", "new-media"));
        var transaction = fixture.Translations.CreateInstallTransaction(
            [ModTranslationTarget.FromLibraryItem(target)],
            "restore.zip");
        string installationId;
        try
        {
            transaction.ScanAsync(translation).AsTask().GetAwaiter().GetResult();
            transaction.CommitAsync().AsTask().GetAwaiter().GetResult();
            installationId = transaction.InstallResult!.InstallationId;
        }
        finally
        {
            transaction.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        var root = fixture.Repository.Layout.GetItemFilesDirectory(target.LibraryItemId);
        TestHarness.Equal("translated", File.ReadAllText(Path.Combine(root, "i18n", "zh.json")));
        TestHarness.True(File.Exists(Path.Combine(root, "assets", "new.png")));
        var listed = fixture.Translations.ListAsync([target.LibraryItemId])
            .AsTask().GetAwaiter().GetResult();
        TestHarness.Equal(installationId, listed.Single().InstallationId);

        File.WriteAllText(Path.Combine(root, "assets", "new.png"), "changed");
        TestHarness.Throws<InvalidOperationException>(() => fixture.Translations.RestoreAsync(installationId)
            .AsTask().GetAwaiter().GetResult());
        File.WriteAllText(Path.Combine(root, "assets", "new.png"), "new-media");
        var restored = fixture.Translations.RestoreAsync(installationId)
            .AsTask().GetAwaiter().GetResult();
        TestHarness.Equal(2, restored.RestoredFiles);
        TestHarness.Equal("original", File.ReadAllText(Path.Combine(root, "i18n", "zh.json")));
        TestHarness.False(File.Exists(Path.Combine(root, "assets", "new.png")));
    }

    public static void SupportsContainedManualMappings()
    {
        using var fixture = new Fixture();
        var target = fixture.ImportSingle(
            "Example.Manual",
            "Manual",
            ("assets/existing.png", "old"));
        using var translation = Archive(("LanguagePack/new/location.png", "new"));
        var transaction = fixture.Translations.CreateInstallTransaction(
            [ModTranslationTarget.FromLibraryItem(target)],
            "manual.zip");
        try
        {
            transaction.ScanAsync(translation).AsTask().GetAwaiter().GetResult();
            TestHarness.Equal(1, transaction.ScanResult!.UnmappedFiles.Count);
            transaction.MapUnmappedAsync("LanguagePack", target.LibraryItemId, "assets")
                .AsTask().GetAwaiter().GetResult();
            var plan = transaction.ScanResult!.Files.Single();
            TestHarness.Equal("assets/new/location.png", plan.TargetPath);
            TestHarness.Throws<ArgumentException>(() => transaction.MapUnmappedAsync(
                    "LanguagePack", new string('a', 64), "assets")
                .AsTask().GetAwaiter().GetResult());
            transaction.CommitAsync().AsTask().GetAwaiter().GetResult();
            TestHarness.True(File.Exists(Path.Combine(
                fixture.Repository.Layout.GetItemFilesDirectory(target.LibraryItemId),
                "assets", "new", "location.png")));
        }
        finally
        {
            transaction.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    public static void DeletesItemsAndTheirTranslationRecords()
    {
        using var fixture = new Fixture();
        var target = fixture.ImportSingle(
            "Example.Delete",
            "Delete",
            ("i18n/default.json", "{\"Key\":\"Default\"}"));
        using var translation = Archive(("Delete/i18n/zh.json", "{\"Key\":\"Value\"}"));
        var transaction = fixture.Translations.CreateInstallTransaction(
            [ModTranslationTarget.FromLibraryItem(target)],
            "delete.zip");
        string installationId;
        try
        {
            transaction.ScanAsync(translation).AsTask().GetAwaiter().GetResult();
            transaction.CommitAsync().AsTask().GetAwaiter().GetResult();
            installationId = transaction.InstallResult!.InstallationId;
        }
        finally
        {
            transaction.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        TestHarness.True(fixture.Repository.DeleteAsync(target.LibraryItemId).AsTask().GetAwaiter().GetResult());
        TestHarness.False(Directory.Exists(Path.Combine(
            fixture.Repository.Layout.TranslationsDirectory,
            installationId)));
        TestHarness.Equal(0, fixture.Translations.ListAsync()
            .AsTask().GetAwaiter().GetResult().Count);
    }

    public static void DeletingOneTranslatedBundleMemberPreservesTheOther()
    {
        using var fixture = new Fixture();
        var imported = fixture.ImportBundle();
        var index = fixture.Repository.ReadAsync().AsTask().GetAwaiter().GetResult();
        var bundle = imported.Bundles.Single();
        var members = bundle.Members.Select(member => index.Items.Single(item =>
            item.LibraryItemId == member.LibraryItemId)).ToArray();
        var targets = bundle.Members.Select(member => ModTranslationTarget.FromLibraryItem(
            members.Single(item => item.LibraryItemId == member.LibraryItemId),
            member.OriginalRootPath)).ToArray();
        using var translation = Archive(
            ("Example Product/Code/i18n/zh.json", "{\"Code\":\"代码\"}"),
            ("Example Product/Content/i18n/zh.json", "{\"Content\":\"内容\"}"));
        var transaction = fixture.Translations.CreateInstallTransaction(targets, "bundle-delete.zip");
        string installationId;
        try
        {
            transaction.ScanAsync(translation).AsTask().GetAwaiter().GetResult();
            transaction.CommitAsync().AsTask().GetAwaiter().GetResult();
            installationId = transaction.InstallResult!.InstallationId;
        }
        finally
        {
            transaction.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        var deleted = members[0];
        var retained = members[1];
        TestHarness.True(fixture.Repository.DeleteAsync(deleted.LibraryItemId).AsTask().GetAwaiter().GetResult());
        var installation = fixture.Translations.ListAsync()
            .AsTask().GetAwaiter().GetResult().Single();
        TestHarness.Equal(installationId, installation.InstallationId);
        TestHarness.Equal(1, installation.AffectedLibraryItemIds.Count);
        TestHarness.Equal(retained.LibraryItemId, installation.AffectedLibraryItemIds[0]);
        TestHarness.Equal(1, installation.FileCount);
        TestHarness.False(Directory.Exists(fixture.Repository.Layout.GetItemDirectory(deleted.LibraryItemId)));
        TestHarness.True(Directory.Exists(fixture.Repository.Layout.GetItemDirectory(retained.LibraryItemId)));

        fixture.Translations.RestoreAsync(installationId).AsTask().GetAwaiter().GetResult();
        TestHarness.Equal(0, fixture.Translations.ListAsync()
            .AsTask().GetAwaiter().GetResult().Count);
    }

    public static void RecoversInterruptedRestoreWithItsInstallationRecord()
    {
        using var fixture = new Fixture();
        var target = fixture.ImportSingle(
            "Example.Recovery",
            "Recovery",
            ("i18n/default.json", "{\"Key\":\"Default\"}"),
            ("i18n/zh.json", "original"));
        using var translation = Archive(("Recovery/i18n/zh.json", "translated"));
        var transaction = fixture.Translations.CreateInstallTransaction(
            [ModTranslationTarget.FromLibraryItem(target)],
            "recovery.zip");
        string installationId;
        try
        {
            transaction.ScanAsync(translation).AsTask().GetAwaiter().GetResult();
            transaction.CommitAsync().AsTask().GetAwaiter().GetResult();
            installationId = transaction.InstallResult!.InstallationId;
        }
        finally
        {
            transaction.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        var live = fixture.Repository.Layout.GetItemFilesDirectory(target.LibraryItemId);
        var transactionId = Guid.NewGuid().ToString("N");
        var staging = Path.Combine(fixture.Repository.Layout.StagingDirectory, $"translation-{transactionId}");
        var old = Path.Combine(staging, $"{target.LibraryItemId}-old");
        Directory.CreateDirectory(staging);
        Directory.Move(live, old);
        CopyDirectory(old, live);
        File.WriteAllText(Path.Combine(live, "i18n", "zh.json"), "original");
        Directory.Move(
            Path.Combine(fixture.Repository.Layout.TranslationsDirectory, installationId),
            Path.Combine(staging, "removed-record"));
        File.WriteAllText(
            Path.Combine(staging, "transaction.json"),
            JsonSerializer.Serialize(new
            {
                schema = "junimogate-mod-translation-transaction/v1",
                transactionId,
                phase = "prepared",
                libraryItemIds = new[] { target.LibraryItemId },
                removedInstallationId = installationId,
            }));

        var reopened = new ModLibraryRepository(fixture.Root);
        _ = reopened.ReadAsync().AsTask().GetAwaiter().GetResult();
        TestHarness.Equal("translated", File.ReadAllText(Path.Combine(live, "i18n", "zh.json")));
        var reopenedTranslations = new ModTranslationHistoryRepository(reopened);
        TestHarness.Equal(installationId, reopenedTranslations.ListAsync([target.LibraryItemId])
            .AsTask().GetAwaiter().GetResult().Single().InstallationId);
        reopenedTranslations.RestoreAsync(installationId).AsTask().GetAwaiter().GetResult();
        TestHarness.Equal("original", File.ReadAllText(Path.Combine(live, "i18n", "zh.json")));
    }

    public static void RecoversInterruptedInstallWithItsGeneration()
    {
        using var fixture = new Fixture();
        var target = fixture.ImportSingle(
            "Example.InstallRecovery",
            "InstallRecovery",
            ("i18n/default.json", "{\"Key\":\"Default\"}"),
            ("i18n/zh.json", "original"));
        var before = fixture.Repository.ReadAsync().AsTask().GetAwaiter().GetResult();
        var transactionId = Guid.NewGuid().ToString("N");
        var staging = Path.Combine(fixture.Repository.Layout.StagingDirectory, $"translation-{transactionId}");
        var live = fixture.Repository.Layout.GetItemFilesDirectory(target.LibraryItemId);
        var old = Path.Combine(staging, $"{target.LibraryItemId}-old");
        Directory.CreateDirectory(staging);
        File.Copy(
            fixture.Repository.Layout.IndexPath,
            Path.Combine(staging, "library-index.before.json"));
        Directory.Move(live, old);
        CopyDirectory(old, live);
        File.WriteAllText(Path.Combine(live, "i18n", "zh.json"), "translated");
        Directory.CreateDirectory(Path.Combine(fixture.Repository.Layout.TranslationsDirectory, transactionId));
        var changed = before with
        {
            Revision = before.Revision + 1,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            Items = before.Items.Select(item => item.LibraryItemId == target.LibraryItemId
                ? item with { ContentGeneration = item.ContentGeneration + 1 }
                : item).ToArray(),
        };
        File.WriteAllText(
            fixture.Repository.Layout.IndexPath,
            JsonSerializer.Serialize(changed, JsonOptions));
        File.WriteAllText(
            Path.Combine(staging, "transaction.json"),
            JsonSerializer.Serialize(new
            {
                schema = "junimogate-mod-translation-transaction/v1",
                transactionId,
                phase = "prepared",
                libraryItemIds = new[] { target.LibraryItemId },
            }, JsonOptions));

        var reopened = new ModLibraryRepository(fixture.Root);
        var recovered = reopened.ReadAsync().AsTask().GetAwaiter().GetResult();
        TestHarness.Equal("original", File.ReadAllText(Path.Combine(live, "i18n", "zh.json")));
        TestHarness.Equal(target.ContentGeneration, recovered.Items.Single().ContentGeneration);
        TestHarness.False(Directory.Exists(Path.Combine(
            fixture.Repository.Layout.TranslationsDirectory,
            transactionId)));
        TestHarness.False(Directory.Exists(staging));
    }

    public static void RejectsTargetsChangedAfterPreview()
    {
        using var fixture = new Fixture();
        var target = fixture.ImportSingle(
            "Example.Concurrent",
            "Concurrent",
            ("i18n/default.json", "{\"Key\":\"Default\"}"),
            ("i18n/zh.json", "original"));
        using var translation = Archive(("Concurrent/i18n/zh.json", "translated"));
        var transaction = fixture.Translations.CreateInstallTransaction(
            [ModTranslationTarget.FromLibraryItem(target)],
            "concurrent.zip");
        try
        {
            transaction.ScanAsync(translation).AsTask().GetAwaiter().GetResult();
            var targetPath = Path.Combine(
                fixture.Repository.Layout.GetItemFilesDirectory(target.LibraryItemId),
                "i18n",
                "zh.json");
            File.WriteAllText(targetPath, "changed-after-preview");
            TestHarness.Throws<InvalidOperationException>(() =>
                transaction.CommitAsync().AsTask().GetAwaiter().GetResult());
            TestHarness.Equal("changed-after-preview", File.ReadAllText(targetPath));
        }
        finally
        {
            transaction.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    public static void SerializesRepositoriesForTheSameRoot()
    {
        using var fixture = new Fixture();
        var reopened = new ModLibraryRepository(fixture.Root);
        var field = typeof(ModLibraryRepository).GetField(
            "operationLock",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("The Mod library operation lock is missing.");
        var firstLock = (SemaphoreSlim)field.GetValue(fixture.Repository)!;
        var secondLock = (SemaphoreSlim)field.GetValue(reopened)!;
        TestHarness.True(ReferenceEquals(firstLock, secondLock));

        Task<ModLibraryIndex> read;
        firstLock.Wait();
        try
        {
            read = reopened.ReadAsync().AsTask();
            TestHarness.False(read.Wait(TimeSpan.FromMilliseconds(100)));
        }
        finally
        {
            firstLock.Release();
        }
        _ = read.GetAwaiter().GetResult();
    }

    public static void AcceptsUnixModeOnlyDirectories()
    {
        using var fixture = new Fixture();
        var target = fixture.ImportSingle(
            "Example.UnixZip",
            "UnixZip",
            ("i18n/default.json", "{\"Key\":\"Default\"}"));
        using var translation = new MemoryStream();
        using (var archive = new ZipArchive(translation, ZipArchiveMode.Create, leaveOpen: true))
        {
            var directory = archive.CreateEntry("UnixZip/i18n");
            directory.ExternalAttributes = (0x4000 | 0x1ED) << 16;
            var locale = archive.CreateEntry("UnixZip/i18n/zh.json", CompressionLevel.Fastest);
            using var output = locale.Open();
            output.Write(Encoding.UTF8.GetBytes("{\"Key\":\"值\"}"));
        }
        translation.Position = 0;

        var transaction = fixture.Translations.CreateInstallTransaction(
            [ModTranslationTarget.FromLibraryItem(target)],
            "unix-directory.zip");
        try
        {
            transaction.ScanAsync(translation).AsTask().GetAwaiter().GetResult();
            TestHarness.True(transaction.ScanResult!.CanCommit);
            TestHarness.Equal(1, transaction.ScanResult.Files.Count);
            TestHarness.Equal("i18n/zh.json", transaction.ScanResult.Files[0].TargetPath);
            transaction.CommitAsync().AsTask().GetAwaiter().GetResult();
            TestHarness.True(File.Exists(Path.Combine(
                fixture.Repository.Layout.GetItemFilesDirectory(target.LibraryItemId),
                "i18n",
                "zh.json")));
        }
        finally
        {
            transaction.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.General)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private sealed class Fixture : IDisposable
    {
        public Fixture()
        {
            Root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), $"junimogate-translation-{Guid.NewGuid():N}"));
            Directory.CreateDirectory(Root);
            Repository = new ModLibraryRepository(Root);
            Translations = new ModTranslationHistoryRepository(Repository);
        }

        public string Root { get; }
        public ModLibraryRepository Repository { get; }
        public ModTranslationHistoryRepository Translations { get; }

        public ModLibraryItem ImportSingle(
            string uniqueId,
            string rootName,
            params (string Path, string Content)[] files)
        {
            var entries = new List<(string Path, string Content)>
            {
                ($"Wrapper/{rootName}/manifest.json", Manifest(uniqueId, "Mod.dll")),
                ($"Wrapper/{rootName}/Mod.dll", "binary"),
            };
            entries.AddRange(files.Select(file => ($"Wrapper/{rootName}/{file.Path}", file.Content)));
            using var archive = Archive(entries.ToArray());
            return Import(archive).AddedItems.Single();
        }

        public ModArchiveImportResult ImportBundle()
        {
            using var archive = Archive(
                ("Wrapped/Example Product/Code/manifest.json", Manifest("Example.Product.Code", "Code.dll")),
                ("Wrapped/Example Product/Code/Code.dll", "binary"),
                ("Wrapped/Example Product/Code/i18n/default.json", "{\"Code\":\"Code\"}"),
                ("Wrapped/Example Product/Content/manifest.json", ContentManifest("Example.Product.Content")),
                ("Wrapped/Example Product/Content/content.json", "{}"),
                ("Wrapped/Example Product/Content/i18n/default.json", "{\"Content\":\"Content\"}"));
            return Import(archive);
        }

        private ModArchiveImportResult Import(Stream archive)
        {
            var transaction = Repository.CreateInstallTransaction("source.zip");
            try
            {
                transaction.ScanAsync(archive).AsTask().GetAwaiter().GetResult();
                transaction.CommitAsync().AsTask().GetAwaiter().GetResult();
                return transaction.ImportResult!;
            }
            finally
            {
                transaction.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }

    private static MemoryStream Archive(params (string Path, string Content)[] entries)
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, content) in entries)
            {
                var entry = archive.CreateEntry(path, CompressionLevel.Fastest);
                using var output = entry.Open();
                output.Write(Encoding.UTF8.GetBytes(content));
            }
        }
        stream.Position = 0;
        return stream;
    }

    private static string Manifest(string uniqueId, string entryDll) =>
        $"{{\"Name\":\"{uniqueId}\",\"Author\":\"Test\",\"Version\":\"1.0.0\",\"UniqueID\":\"{uniqueId}\",\"EntryDll\":\"{entryDll}\",\"UpdateKeys\":[\"Nexus:123\"]}}";

    private static string ContentManifest(string uniqueId) =>
        $"{{\"Name\":\"{uniqueId}\",\"Author\":\"Test\",\"Version\":\"1.0.0\",\"UniqueID\":\"{uniqueId}\",\"ContentPackFor\":{{\"UniqueID\":\"Pathoschild.ContentPatcher\"}},\"UpdateKeys\":[\"Nexus:123\"]}}";
}
