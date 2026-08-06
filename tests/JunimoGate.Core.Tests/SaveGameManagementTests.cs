using System.IO.Compression;
using JunimoGate.Core;
using JunimoGate.Tests;

internal static class SaveGameManagementTests
{
    public static void ReadsLiveSaveMetadataWithoutNestedNameConfusion()
    {
        using var fixture = new Fixture();
        fixture.CreateLiveSave("Farm_123", "Farmer", "Bluebird", extraXml: "<items><Item><name>Nested item</name></Item></items>");
        var entry = LiveSaveGameCatalog.Read(fixture.SavesRoot).Single();
        TestHarness.Equal("Farmer", entry.Metadata.PlayerName);
        TestHarness.Equal("Bluebird", entry.Metadata.FarmName);
        TestHarness.Equal(3, entry.Metadata.Year);
        TestHarness.Equal(TimeSpan.FromHours(2), entry.Metadata.PlayTime!.Value);
        TestHarness.Equal(SaveGameEntryStatus.Ready, entry.Status);
    }

    public static void FindsNestedAndRootSaveArchives()
    {
        using var fixture = new Fixture();
        var nested = fixture.CreateArchive("nested.zip", "wrapper/Saves/Farm_123", "Farmer", "Bluebird");
        var nestedInspection = SaveArchiveInspector.InspectZip(nested);
        TestHarness.Equal(1, nestedInspection.Candidates.Count);
        TestHarness.Equal("Farm_123", nestedInspection.Candidates[0].DirectoryName);

        var root = fixture.CreateArchive("root.zip", string.Empty, "Farmer", "Bluebird", "Farm_123");
        var rootInspection = SaveArchiveInspector.InspectZip(root);
        TestHarness.Equal("Farm_123", rootInspection.Candidates.Single().DirectoryName);
    }

    public static void ImportsNewSavesAndProtectsReplacements()
    {
        using var fixture = new Fixture();
        fixture.CreateLiveSave("Farm_123", "Old", "Old Farm");
        var archive = fixture.CreateArchive("replacement.zip", "Farm_123", "New", "New Farm");
        var candidate = SaveArchiveInspector.InspectZip(archive).Candidates.Single();
        var transaction = new SaveImportTransaction(fixture.SavesRoot, fixture.StagingRoot, fixture.BackupRoot);
        var result = transaction.ImportAsync(
            archive,
            [new SaveImportSelection(candidate.CandidateId, SaveImportConflictResolution.Replace)])
            .AsTask().GetAwaiter().GetResult();
        TestHarness.Equal(1, result.ImportedDirectoryNames.Count);
        TestHarness.True(result.SafetyBackupName is not null);
        TestHarness.True(File.Exists(Path.Combine(fixture.BackupRoot, result.SafetyBackupName!)));
        TestHarness.Equal("New", LiveSaveGameCatalog.Read(fixture.SavesRoot).Single().Metadata.PlayerName);
    }

    public static void RejectsTraversalAndPreservesSidecarFiles()
    {
        using var fixture = new Fixture();
        var malicious = Path.Combine(fixture.Root, "malicious.zip");
        using (var archive = ZipFile.Open(malicious, ZipArchiveMode.Create))
        {
            archive.CreateEntry("../escape/SaveGameInfo");
        }
        TestHarness.Throws<InvalidDataException>(() => SaveArchiveInspector.InspectZip(malicious));

        var valid = fixture.CreateArchive("sidecar.zip", "Farm_123", "Farmer", "Farm", sidecar: true);
        var candidate = SaveArchiveInspector.InspectZip(valid).Candidates.Single();
        var transaction = new SaveImportTransaction(fixture.SavesRoot, fixture.StagingRoot, fixture.BackupRoot);
        transaction.ImportAsync(valid, [new SaveImportSelection(candidate.CandidateId, SaveImportConflictResolution.Skip)])
            .AsTask().GetAwaiter().GetResult();
        TestHarness.True(File.Exists(Path.Combine(fixture.SavesRoot, "Farm_123", "spacecore-serialization.json")));
    }

    private sealed class Fixture : IDisposable
    {
        public Fixture()
        {
            Root = Path.Combine(Path.GetTempPath(), $"junimogate-save-tests-{Guid.NewGuid():N}");
            SavesRoot = Path.Combine(Root, "Saves");
            StagingRoot = Path.Combine(Root, "staging");
            BackupRoot = Path.Combine(Root, "backups");
            Directory.CreateDirectory(SavesRoot);
        }

        public string Root { get; }
        public string SavesRoot { get; }
        public string StagingRoot { get; }
        public string BackupRoot { get; }

        public void CreateLiveSave(string directory, string player, string farm, string extraXml = "")
        {
            var root = Path.Combine(SavesRoot, directory);
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "SaveGameInfo"), Metadata(player, farm, extraXml));
            File.WriteAllText(Path.Combine(root, directory), "save");
        }

        public string CreateArchive(
            string fileName,
            string prefix,
            string player,
            string farm,
            string rootPrimaryName = "Farm_123",
            bool sidecar = false)
        {
            var path = Path.Combine(Root, fileName);
            using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
            var normalized = string.IsNullOrEmpty(prefix) ? string.Empty : prefix.Trim('/') + "/";
            Write(archive, normalized + "SaveGameInfo", Metadata(player, farm, string.Empty));
            var directoryName = string.IsNullOrEmpty(prefix) ? rootPrimaryName : prefix.Trim('/').Split('/').Last();
            Write(archive, normalized + directoryName, "save");
            if (sidecar)
                Write(archive, normalized + "spacecore-serialization.json", "{}");
            return path;
        }

        private static string Metadata(string player, string farm, string extraXml) =>
            $"<Farmer><name>{player}</name>{extraXml}<farmName>{farm}</farmName><gameVersion>1.6.15.3</gameVersion><dayOfMonthForSaveGame>4</dayOfMonthForSaveGame><seasonForSaveGame>1</seasonForSaveGame><yearForSaveGame>3</yearForSaveGame><millisecondsPlayed>7200000</millisecondsPlayed></Farmer>";

        private static void Write(ZipArchive archive, string name, string value)
        {
            var entry = archive.CreateEntry(name);
            using var writer = new StreamWriter(entry.Open());
            writer.Write(value);
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
    }
}
