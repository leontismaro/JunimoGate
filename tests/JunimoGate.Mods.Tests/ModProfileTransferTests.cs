using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using JunimoGate.Mods;
using JunimoGate.Tests;

internal static class ModProfileTransferTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.General)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    public static void ManifestRoundtripPreservesPlaceholders()
    {
        using var fixture = new TransferFixture();
        var item = fixture.ImportMod("Example.Manifest", includeConfig: false);
        var missing = new ModProfileMember(
            "Example.Missing",
            null,
            Enabled: false,
            "Missing Mod",
            "2.0.0",
            "Test",
            DateTimeOffset.UtcNow);
        var source = fixture.Profiles.CreateImportedAsync(
                "Shared group",
                "Roundtrip",
                ModAssemblyBindingPolicy.Strict,
                new[] { ModProfileMember.FromLibraryItem(item, enabled: true), missing })
            .AsTask().GetAwaiter().GetResult();
        var service = new ModProfileTransferService(fixture.Library, fixture.Profiles);
        using var output = new MemoryStream();

        service.ExportManifestAsync(ProfileId.Parse(source.Id), output).AsTask().GetAwaiter().GetResult();
        output.Position = 0;
        var imported = service.ImportManifestAsync(output).AsTask().GetAwaiter().GetResult();

        TestHarness.False(imported.Profile.Id == source.Id);
        TestHarness.Equal("Shared group", imported.Profile.DisplayName);
        TestHarness.Equal(ModAssemblyBindingPolicy.Strict, imported.Profile.AssemblyBindingPolicyOverride);
        TestHarness.Equal(2, imported.Profile.Members.Count);
        TestHarness.Equal(item.LibraryItemId, imported.Profile.Members.Single(member => member.UniqueId == "Example.Manifest").LibraryItemId);
        TestHarness.Equal(null, imported.Profile.Members.Single(member => member.UniqueId == "Example.Missing").LibraryItemId);
        TestHarness.Equal(1, imported.MissingMembers);
    }

    public static void CompletePackageExcludesConfigAndBindsExportedContent()
    {
        using var sourceFixture = new TransferFixture();
        var sourceItem = sourceFixture.ImportMod("Example.Package", includeConfig: true);
        var sourceProfile = sourceFixture.Profiles.CreateImportedAsync(
                "Complete group",
                null,
                null,
                new[] { ModProfileMember.FromLibraryItem(sourceItem, enabled: true) })
            .AsTask().GetAwaiter().GetResult();
        var sourceService = new ModProfileTransferService(sourceFixture.Library, sourceFixture.Profiles);
        using var package = new MemoryStream();

        var exported = sourceService.ExportPackageAsync(ProfileId.Parse(sourceProfile.Id), package)
            .AsTask().GetAwaiter().GetResult();
        TestHarness.Equal(1, exported.PackagedItems);
        TestHarness.Equal(1, exported.ExcludedConfigFiles);
        var packagedId = exported.Document.Members.Single().PackagedContentId;
        TestHarness.True(packagedId is { Length: 64 } && packagedId.All(Uri.IsHexDigit));
        TestHarness.False(packagedId == sourceItem.LibraryItemId);
        package.Position = 0;
        using (var archive = new ZipArchive(package, ZipArchiveMode.Read, leaveOpen: true))
        {
            TestHarness.False(archive.Entries.Any(entry => entry.FullName.EndsWith("/config.json", StringComparison.OrdinalIgnoreCase)));
            TestHarness.True(archive.GetEntry(ModProfileTransferDocument.PackageEntryName) is not null);
        }

        using var destinationFixture = new TransferFixture();
        var destinationService = new ModProfileTransferService(destinationFixture.Library, destinationFixture.Profiles);
        package.Position = 0;
        using var transaction = new AsyncTransactionScope(destinationService.CreatePackageImportTransaction("group.zip"));
        transaction.Value.ScanAsync(package).AsTask().GetAwaiter().GetResult();
        transaction.Value.CommitAsync().AsTask().GetAwaiter().GetResult();
        var imported = transaction.Value.ImportResult
            ?? throw new InvalidOperationException("The complete group import result is missing.");

        TestHarness.Equal(1, imported.AddedItems.Count);
        TestHarness.Equal(packagedId, imported.AddedItems[0].LibraryItemId);
        TestHarness.Equal(packagedId, imported.Profile.Members.Single().LibraryItemId);
        TestHarness.Equal(0, imported.MissingMembers);
    }

    public static void RejectsForgedPackageIdentityWithoutLibraryChanges()
    {
        using var fixture = new TransferFixture();
        var service = new ModProfileTransferService(fixture.Library, fixture.Profiles);
        using var package = CreateForgedPackage();
        using var transaction = new AsyncTransactionScope(service.CreatePackageImportTransaction("forged.zip"));

        transaction.Value.ScanAsync(package).AsTask().GetAwaiter().GetResult();
        TestHarness.Throws<InvalidDataException>(() =>
            transaction.Value.CommitAsync().AsTask().GetAwaiter().GetResult());

        var library = fixture.Library.ReadAsync().AsTask().GetAwaiter().GetResult();
        TestHarness.Equal(0, library.Items.Count);
        TestHarness.Equal(1, library.Revision);
        TestHarness.Equal(1, fixture.Profiles.ListAsync().AsTask().GetAwaiter().GetResult().Count);
    }

    public static void CompletePackageSupportsAnEmptyGroup()
    {
        using var sourceFixture = new TransferFixture();
        var profile = sourceFixture.Profiles.CreateImportedAsync(
                "Empty shared group",
                null,
                null,
                Array.Empty<ModProfileMember>())
            .AsTask().GetAwaiter().GetResult();
        var service = new ModProfileTransferService(sourceFixture.Library, sourceFixture.Profiles);
        using var package = new MemoryStream();
        service.ExportPackageAsync(ProfileId.Parse(profile.Id), package).AsTask().GetAwaiter().GetResult();

        using var destinationFixture = new TransferFixture();
        var destination = new ModProfileTransferService(destinationFixture.Library, destinationFixture.Profiles);
        package.Position = 0;
        using var transaction = new AsyncTransactionScope(destination.CreatePackageImportTransaction("empty.zip"));
        transaction.Value.ScanAsync(package).AsTask().GetAwaiter().GetResult();
        transaction.Value.CommitAsync().AsTask().GetAwaiter().GetResult();

        TestHarness.Equal("Empty shared group", transaction.Value.ImportResult?.Profile.DisplayName);
        TestHarness.Equal(0, transaction.Value.ImportResult?.Profile.Members.Count ?? -1);
        TestHarness.Equal(0, destinationFixture.Library.ReadAsync().AsTask().GetAwaiter().GetResult().Items.Count);
    }

    private static MemoryStream CreateForgedPackage()
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(archive, "mods/forged/manifest.json", """
                {"Name":"Forged","Author":"Test","Version":"1.0.0","UniqueID":"Example.Forged","EntryDll":"Forged.dll"}
                """);
            WriteEntry(archive, "mods/forged/Forged.dll", "managed-placeholder");
            var now = DateTimeOffset.UtcNow;
            var document = new ModProfileTransferDocument(
                ModProfileTransferDocument.CurrentSchema,
                ModProfileTransferKind.Complete,
                "default",
                "Forged group",
                null,
                null,
                new[]
                {
                    new ModProfileTransferMember(
                        "Example.Forged",
                        null,
                        new string('f', 64),
                        Enabled: true,
                        "Forged",
                        "1.0.0",
                        "Test",
                        now),
                },
                now);
            var metadata = archive.CreateEntry(ModProfileTransferDocument.PackageEntryName);
            using var output = metadata.Open();
            JsonSerializer.Serialize(output, document, JsonOptions);
        }
        stream.Position = 0;
        return stream;
    }

    private sealed class TransferFixture : IDisposable
    {
        public TransferFixture()
        {
            Root = Path.Combine(Path.GetTempPath(), $"junimogate-transfer-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
            Library = new ModLibraryRepository(Path.Combine(Root, "mods"));
            Profiles = new ModProfileV2Repository(Path.Combine(Root, "profiles"));
        }

        public string Root { get; }
        public ModLibraryRepository Library { get; }
        public ModProfileV2Repository Profiles { get; }

        public ModLibraryItem ImportMod(string uniqueId, bool includeConfig)
        {
            using var archive = new MemoryStream();
            using (var zip = new ZipArchive(archive, ZipArchiveMode.Create, leaveOpen: true))
            {
                WriteEntry(zip, "Mod/manifest.json", $$"""
                    {"Name":"{{uniqueId}}","Author":"Test","Version":"1.0.0","UniqueID":"{{uniqueId}}","EntryDll":"Mod.dll"}
                    """);
                WriteEntry(zip, "Mod/Mod.dll", "managed-placeholder");
                if (includeConfig)
                    WriteEntry(zip, "Mod/config.json", "{\"private\":true}");
            }
            archive.Position = 0;
            var transaction = Library.CreateInstallTransaction("test.zip");
            try
            {
                transaction.ScanAsync(archive).AsTask().GetAwaiter().GetResult();
                transaction.CommitAsync().AsTask().GetAwaiter().GetResult();
                return transaction.ImportResult?.AllItems.Single()
                    ?? throw new InvalidOperationException("The test Mod import failed.");
            }
            finally
            {
                transaction.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
    }

    private sealed class AsyncTransactionScope(ModProfilePackageImportTransaction value) : IDisposable
    {
        public ModProfilePackageImportTransaction Value { get; } = value;

        public void Dispose() => Value.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private static void WriteEntry(ZipArchive archive, string path, string content)
    {
        var entry = archive.CreateEntry(path);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(content);
    }
}
