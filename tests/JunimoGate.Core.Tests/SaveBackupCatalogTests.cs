using System.IO.Compression;
using JunimoGate.Core;
using JunimoGate.Tests;

internal static class SaveBackupCatalogTests
{
    public static void ListsOnlyCompleteTopLevelZipBackups()
    {
        using var fixture = new Fixture();
        fixture.CreateBackup("valid.zip", "Farm_1/SaveGameInfo", "save");
        File.WriteAllText(Path.Combine(fixture.Root, "incomplete.zip"), "not a ZIP");
        Directory.CreateDirectory(Path.Combine(fixture.Root, "fallback-backup"));

        var snapshot = fixture.Catalog.Read();
        TestHarness.Equal(1, snapshot.Entries.Count);
        TestHarness.Equal("valid.zip", snapshot.Entries[0].FileName);
        TestHarness.Equal(1, snapshot.Entries[0].SaveEntryCount);
        TestHarness.Equal(2, snapshot.UnavailableEntryCount);
    }

    public static void ExportsOneExactBackup()
    {
        using var fixture = new Fixture();
        fixture.CreateBackup("valid.zip", "Farm_1/SaveGameInfo", "save");
        using var output = new MemoryStream();
        fixture.Catalog.ExportAsync("valid.zip", output).AsTask().GetAwaiter().GetResult();
        output.Position = 0;
        using var archive = new ZipArchive(output, ZipArchiveMode.Read);
        TestHarness.Equal("Farm_1/SaveGameInfo", archive.Entries.Single().FullName);
    }

    public static void RejectsTraversalAndIncompleteSelections()
    {
        using var fixture = new Fixture();
        File.WriteAllText(Path.Combine(fixture.Root, "incomplete.zip"), "not a ZIP");
        TestHarness.Throws<InvalidDataException>(() => fixture.Catalog.ExportAsync(
            "../escape.zip",
            new MemoryStream()).AsTask().GetAwaiter().GetResult());
        TestHarness.Throws<InvalidDataException>(() => fixture.Catalog.ExportAsync(
            "incomplete.zip",
            new MemoryStream()).AsTask().GetAwaiter().GetResult());
    }

    private sealed class Fixture : IDisposable
    {
        public Fixture()
        {
            Root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), $"junimogate-save-backups-{Guid.NewGuid():N}"));
            Directory.CreateDirectory(Root);
            Catalog = new SaveBackupCatalog(Root);
        }

        public string Root { get; }
        public SaveBackupCatalog Catalog { get; }

        public void CreateBackup(string name, string entryName, string value)
        {
            using var archive = ZipFile.Open(Path.Combine(Root, name), ZipArchiveMode.Create);
            var entry = archive.CreateEntry(entryName);
            using var writer = new StreamWriter(entry.Open());
            writer.Write(value);
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
