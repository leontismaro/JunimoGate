using System.IO.Compression;
using System.Text;
using JunimoGate.Mods;
using JunimoGate.Tests;

internal static class ModLibraryTests
{
    public static void DiscoversMultipleNestedMods()
    {
        using var fixture = new ModLibraryFixture();
        using var archive = CreateArchive(
            ("bundle/Alpha/manifest.json", ManifestWithCommentsAndTrailingComma("Example.Alpha", "1.0.0", "Alpha.dll")),
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

    public static void RejectsUnsafeArchiveShapes()
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

        using (var overlapping = CreateArchive(
                   ("manifest.json", Manifest("Example.Root", "1.0.0", entryDll: "Root.dll")),
                   ("Root.dll", "root"),
                   ("Nested/manifest.json", Manifest("Example.Nested", "1.0.0", entryDll: "Nested.dll")),
                   ("Nested/Nested.dll", "nested")))
        {
            var transaction = fixture.Repository.CreateInstallTransaction();
            try
            {
                transaction.ScanAsync(overlapping).AsTask().GetAwaiter().GetResult();
                TestHarness.False(transaction.ScanResult!.CanCommit);
                TestHarness.True(transaction.ScanResult.Issues.Any(issue => issue.Code == "overlapping_mod_roots"));
            }
            finally
            {
                transaction.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
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
        TestHarness.True(File.Exists(Path.Combine(fixture.Repository.Layout.GetItemFilesDirectory(item.LibraryItemId), "Mod.dll")));
        TestHarness.True(File.Exists(fixture.Repository.Layout.GetItemMetadataPath(item.LibraryItemId)));

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

        Directory.Delete(fixture.Repository.Layout.GetItemDirectory(item.LibraryItemId), recursive: true);
        using var repairArchive = SingleModArchive("Example.Import", "1.2.3", "first-content");
        var repaired = Import(fixture.Repository, repairArchive, "repair.zip");
        TestHarness.Equal(1, repaired.ReusedItems.Count);
        TestHarness.True(Directory.Exists(fixture.Repository.Layout.GetItemDirectory(item.LibraryItemId)));
        TestHarness.Equal(afterSecond.Revision, fixture.Repository.ReadAsync().AsTask().GetAwaiter().GetResult().Revision);
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
