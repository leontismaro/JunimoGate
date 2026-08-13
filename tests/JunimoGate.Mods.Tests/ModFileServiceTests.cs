using System.IO.Compression;
using System.Text;
using JunimoGate.Mods;
using JunimoGate.Tests;

internal static class ModFileServiceTests
{
    public static void ListsAndEditsActualPrivateFiles()
    {
        using var fixture = new Fixture();
        var item = fixture.Import();
        var root = fixture.Repository.Layout.GetItemFilesDirectory(item.LibraryItemId);
        File.WriteAllText(Path.Combine(root, "config.toml"), "speed = 1\n");
        File.WriteAllText(Path.Combine(root, "settings"), "enabled=true\n");
        File.WriteAllBytes(Path.Combine(root, "binary.dat"), [0, 1, 2, 0, 4]);

        var entries = fixture.Files.ListAsync(item.LibraryItemId).AsTask().GetAwaiter().GetResult();
        TestHarness.True(entries.Single(entry => entry.Name == "assets").IsDirectory);
        TestHarness.True(entries.Single(entry => entry.Name == "config.toml").CanEdit);
        TestHarness.True(entries.Single(entry => entry.Name == "settings").CanEdit);
        TestHarness.False(entries.Single(entry => entry.Name == "binary.dat").CanEdit);
        TestHarness.False(entries.Single(entry => entry.Name == "manifest.json").CanEdit);
        TestHarness.False(entries.Single(entry => entry.Name == "Mod.dll").CanEdit);

        var opened = fixture.Files.ReadTextAsync(item.LibraryItemId, "config.toml")
            .AsTask().GetAwaiter().GetResult();
        var saved = fixture.Files.SaveTextAsync(item.LibraryItemId, opened, "speed = 2\n")
            .AsTask().GetAwaiter().GetResult();
        TestHarness.Equal("speed = 2\n", saved.Text);
        TestHarness.Equal("speed = 2\n", File.ReadAllText(Path.Combine(root, "config.toml")));
        TestHarness.Equal(item.ContentId, fixture.Repository.ReadAsync().AsTask().GetAwaiter().GetResult()
            .Items.Single().ContentId);
    }

    public static void RejectsUnsafeProtectedAndConcurrentWrites()
    {
        using var fixture = new Fixture();
        var item = fixture.Import();
        var root = fixture.Repository.Layout.GetItemFilesDirectory(item.LibraryItemId);
        var config = Path.Combine(root, "config.json");
        File.WriteAllText(config, "{}\n");
        var opened = fixture.Files.ReadTextAsync(item.LibraryItemId, "config.json")
            .AsTask().GetAwaiter().GetResult();

        File.WriteAllText(config, "{\"changed\":true}\n");
        File.SetLastWriteTimeUtc(config, opened.LastWriteTimeUtc.UtcDateTime.AddSeconds(2));
        TestHarness.Throws<InvalidOperationException>(() => fixture.Files
            .SaveTextAsync(item.LibraryItemId, opened, "{\"mine\":true}\n")
            .AsTask().GetAwaiter().GetResult());
        TestHarness.Throws<InvalidDataException>(() => fixture.Files
            .ReadTextAsync(item.LibraryItemId, "manifest.json")
            .AsTask().GetAwaiter().GetResult());
        TestHarness.Throws<InvalidDataException>(() => fixture.Files
            .ReadTextAsync(item.LibraryItemId, "../library-index.json")
            .AsTask().GetAwaiter().GetResult());
    }

    public static void CreatesTextFilesInTheCurrentDirectory()
    {
        using var fixture = new Fixture();
        var item = fixture.Import();
        var root = fixture.Repository.Layout.GetItemFilesDirectory(item.LibraryItemId);

        var created = fixture.Files.CreateTextAsync(item.LibraryItemId, "assets", "config.toml")
            .AsTask().GetAwaiter().GetResult();
        TestHarness.Equal("assets/config.toml", created.RelativePath);
        TestHarness.Equal(string.Empty, created.Text);
        TestHarness.True(File.Exists(Path.Combine(root, "assets", "config.toml")));
        TestHarness.Throws<IOException>(() => fixture.Files
            .CreateTextAsync(item.LibraryItemId, "assets", "config.toml")
            .AsTask().GetAwaiter().GetResult());
        TestHarness.Throws<InvalidDataException>(() => fixture.Files
            .CreateTextAsync(item.LibraryItemId, "assets", "../config.toml")
            .AsTask().GetAwaiter().GetResult());
        TestHarness.Throws<InvalidDataException>(() => fixture.Files
            .CreateTextAsync(item.LibraryItemId, "assets", "manifest.json")
            .AsTask().GetAwaiter().GetResult());
        TestHarness.Throws<InvalidDataException>(() => fixture.Files
            .CreateTextAsync(item.LibraryItemId, "assets", "mod.dll")
            .AsTask().GetAwaiter().GetResult());
    }

    public static void AcceptsUtf8SplitAtTheProbeBoundary()
    {
        using var fixture = new Fixture();
        var item = fixture.Import();
        var root = fixture.Repository.Layout.GetItemFilesDirectory(item.LibraryItemId);
        var path = Path.Combine(root, "boundary.txt");
        File.WriteAllText(path, new string('a', 8191) + "\u00e9" + "tail");

        var entry = fixture.Files.ListAsync(item.LibraryItemId).AsTask().GetAwaiter().GetResult()
            .Single(value => value.Name == "boundary.txt");
        TestHarness.True(entry.CanEdit);
        var opened = fixture.Files.ReadTextAsync(item.LibraryItemId, "boundary.txt")
            .AsTask().GetAwaiter().GetResult();
        TestHarness.True(opened.Text.EndsWith("\u00e9tail", StringComparison.Ordinal));
    }

    private sealed class Fixture : IDisposable
    {
        public Fixture()
        {
            Root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), $"junimogate-mod-files-{Guid.NewGuid():N}"));
            Directory.CreateDirectory(Root);
            Repository = new ModLibraryRepository(Root);
            Files = new ModFileService(Repository);
        }

        public string Root { get; }
        public ModLibraryRepository Repository { get; }
        public ModFileService Files { get; }

        public ModLibraryItem Import()
        {
            using var stream = new MemoryStream();
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            {
                Add(archive, "Mod/manifest.json",
                    "{\"Name\":\"File Test\",\"Author\":\"Test\",\"Version\":\"1.0.0\",\"UniqueID\":\"Example.Files\",\"EntryDll\":\"Mod.dll\"}");
                Add(archive, "Mod/Mod.dll", "not-a-real-assembly");
                Add(archive, "Mod/assets/data.json", "{}\n");
            }
            stream.Position = 0;
            var transaction = Repository.CreateInstallTransaction("files.zip");
            try
            {
                transaction.ScanAsync(stream).AsTask().GetAwaiter().GetResult();
                transaction.CommitAsync().AsTask().GetAwaiter().GetResult();
                return transaction.ImportResult!.AddedItems.Single();
            }
            finally
            {
                transaction.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);

        private static void Add(ZipArchive archive, string path, string content)
        {
            var entry = archive.CreateEntry(path, CompressionLevel.Fastest);
            using var output = entry.Open();
            output.Write(Encoding.UTF8.GetBytes(content));
        }
    }
}
