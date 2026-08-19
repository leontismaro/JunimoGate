using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
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
        TestHarness.Equal("Roundtrip", imported.Profile.Description);
        TestHarness.Equal(ModAssemblyBindingPolicy.Strict, imported.Profile.AssemblyBindingPolicyOverride);
        TestHarness.Equal(2, imported.Profile.Members.Count);
        var installedMember = imported.Profile.Members.Single(member => member.UniqueId == "Example.Manifest");
        var missingMember = imported.Profile.Members.Single(member => member.UniqueId == "Example.Missing");
        TestHarness.Equal(item.LibraryItemId, installedMember.LibraryItemId);
        TestHarness.True(installedMember.Enabled);
        TestHarness.Equal(null, missingMember.LibraryItemId);
        TestHarness.False(missingMember.Enabled);
        TestHarness.Equal(1, imported.MissingMembers);
    }

    public static void ManifestBindsInstalledModsAcrossDevices()
    {
        using var sourceFixture = new TransferFixture();
        var sourceItem = sourceFixture.ImportMod("Example.Portable", includeConfig: false);
        var sourceProfile = sourceFixture.Profiles.CreateImportedAsync(
                "Portable group",
                null,
                null,
                [ModProfileMember.FromLibraryItem(sourceItem, enabled: true)])
            .AsTask().GetAwaiter().GetResult();
        using var manifest = new MemoryStream();
        new ModProfileTransferService(sourceFixture.Library, sourceFixture.Profiles)
            .ExportManifestAsync(ProfileId.Parse(sourceProfile.Id), manifest)
            .AsTask().GetAwaiter().GetResult();

        using var destinationFixture = new TransferFixture();
        var destinationItem = destinationFixture.ImportMod("Example.Portable", includeConfig: false);
        TestHarness.False(sourceItem.LibraryItemId == destinationItem.LibraryItemId);
        manifest.Position = 0;
        var imported = new ModProfileTransferService(destinationFixture.Library, destinationFixture.Profiles)
            .ImportManifestAsync(manifest)
            .AsTask().GetAwaiter().GetResult();

        TestHarness.Equal(destinationItem.LibraryItemId, imported.Profile.Members.Single().LibraryItemId);
        TestHarness.Equal(0, imported.MissingMembers);
    }

    public static void ManifestPreservesMissingMetadataOnAnEmptyDevice()
    {
        using var sourceFixture = new TransferFixture();
        var sourceItem = sourceFixture.ImportMod("Example.Uninstalled", includeConfig: false, version: "2.3.4");
        var sourceProfile = sourceFixture.Profiles.CreateImportedAsync(
                "Missing group",
                null,
                null,
                [ModProfileMember.FromLibraryItem(sourceItem, enabled: false)])
            .AsTask().GetAwaiter().GetResult();
        using var manifest = new MemoryStream();
        new ModProfileTransferService(sourceFixture.Library, sourceFixture.Profiles)
            .ExportManifestAsync(ProfileId.Parse(sourceProfile.Id), manifest)
            .AsTask().GetAwaiter().GetResult();

        using var destinationFixture = new TransferFixture();
        manifest.Position = 0;
        var imported = new ModProfileTransferService(destinationFixture.Library, destinationFixture.Profiles)
            .ImportManifestAsync(manifest)
            .AsTask().GetAwaiter().GetResult();

        var member = imported.Profile.Members.Single();
        TestHarness.Equal<string?>(null, member.LibraryItemId);
        TestHarness.Equal("Example.Uninstalled", member.ExpectedName);
        TestHarness.Equal("2.3.4", member.ExpectedVersion);
        TestHarness.Equal("Test", member.ExpectedAuthor);
        TestHarness.False(member.Enabled);
        TestHarness.Equal(1, imported.MissingMembers);
    }

    public static void ManifestUsesExpectedVersionToResolveMultipleInstalledVersions()
    {
        using var sourceFixture = new TransferFixture();
        var sourceItem = sourceFixture.ImportMod("Example.Versioned", includeConfig: false, version: "2.0.0");
        var sourceProfile = sourceFixture.Profiles.CreateImportedAsync(
                "Versioned group",
                null,
                null,
                [ModProfileMember.FromLibraryItem(sourceItem, enabled: true)])
            .AsTask().GetAwaiter().GetResult();
        using var manifest = new MemoryStream();
        new ModProfileTransferService(sourceFixture.Library, sourceFixture.Profiles)
            .ExportManifestAsync(ProfileId.Parse(sourceProfile.Id), manifest)
            .AsTask().GetAwaiter().GetResult();

        using var destinationFixture = new TransferFixture();
        _ = destinationFixture.ImportMod("Example.Versioned", includeConfig: false, version: "1.0.0");
        var expected = destinationFixture.ImportMod("Example.Versioned", includeConfig: false, version: "2.0.0");
        manifest.Position = 0;
        var imported = new ModProfileTransferService(destinationFixture.Library, destinationFixture.Profiles)
            .ImportManifestAsync(manifest)
            .AsTask().GetAwaiter().GetResult();

        TestHarness.Equal(expected.LibraryItemId, imported.Profile.Members.Single().LibraryItemId);
        TestHarness.Equal(0, imported.MissingMembers);
    }

    public static void ManifestKeepsAmbiguousInstalledModsMissing()
    {
        using var sourceFixture = new TransferFixture();
        var sourceItem = sourceFixture.ImportMod("Example.Ambiguous", includeConfig: false);
        var sourceProfile = sourceFixture.Profiles.CreateImportedAsync(
                "Ambiguous group",
                null,
                null,
                [ModProfileMember.FromLibraryItem(sourceItem, enabled: true)])
            .AsTask().GetAwaiter().GetResult();
        using var manifest = new MemoryStream();
        new ModProfileTransferService(sourceFixture.Library, sourceFixture.Profiles)
            .ExportManifestAsync(ProfileId.Parse(sourceProfile.Id), manifest)
            .AsTask().GetAwaiter().GetResult();

        using var destinationFixture = new TransferFixture();
        _ = destinationFixture.ImportMod("Example.Ambiguous", includeConfig: false, dllContent: "first");
        _ = destinationFixture.ImportMod("Example.Ambiguous", includeConfig: false, dllContent: "second");
        manifest.Position = 0;
        var imported = new ModProfileTransferService(destinationFixture.Library, destinationFixture.Profiles)
            .ImportManifestAsync(manifest)
            .AsTask().GetAwaiter().GetResult();

        var member = imported.Profile.Members.Single();
        TestHarness.Equal<string?>(null, member.LibraryItemId);
        TestHarness.Equal("Example.Ambiguous", member.ExpectedName);
        TestHarness.Equal("1.0.0", member.ExpectedVersion);
        TestHarness.Equal("Test", member.ExpectedAuthor);
        TestHarness.Equal(1, imported.MissingMembers);
    }

    public static void ImportsLegacyV1ManifestWithoutBundles()
    {
        using var fixture = new TransferFixture();
        var service = new ModProfileTransferService(fixture.Library, fixture.Profiles);
        using var manifest = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("""
            {
              "schema": "junimogate-mod-profile-transfer/v1",
              "kind": "Manifest",
              "displayName": "Legacy shared group",
              "members": [],
              "exportedAtUtc": "2026-08-01T00:00:00+00:00"
            }
            """));

        var imported = service.ImportManifestAsync(manifest).AsTask().GetAwaiter().GetResult();

        TestHarness.Equal("Legacy shared group", imported.Profile.DisplayName);
        TestHarness.Equal(0, imported.Profile.Members.Count);
    }

    public static void CompletePackageExcludesConfigAndBindsExportedContent()
    {
        using var sourceFixture = new TransferFixture();
        var sourceItem = sourceFixture.ImportMod("Example.Package", includeConfig: true);
        var sourceProfile = sourceFixture.Profiles.CreateImportedAsync(
                "Complete group",
                "Complete package description",
                ModAssemblyBindingPolicy.FirstLoaded,
                new[] { ModProfileMember.FromLibraryItem(sourceItem, enabled: false) })
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
        TestHarness.Equal(packagedId, imported.AddedItems[0].ImportedContentId);
        TestHarness.False(packagedId == imported.AddedItems[0].LibraryItemId);
        TestHarness.Equal(imported.AddedItems[0].LibraryItemId, imported.Profile.Members.Single().LibraryItemId);
        TestHarness.Equal("Complete group", imported.Profile.DisplayName);
        TestHarness.Equal("Complete package description", imported.Profile.Description);
        TestHarness.Equal(ModAssemblyBindingPolicy.FirstLoaded, imported.Profile.AssemblyBindingPolicyOverride);
        TestHarness.False(imported.Profile.Members.Single().Enabled);
        TestHarness.Equal(0, imported.MissingMembers);
    }

    public static void CompletePackageIncludesConfigWhenRequested()
    {
        using var sourceFixture = new TransferFixture();
        var sourceItem = sourceFixture.ImportMod("Example.PackageWithConfig", includeConfig: true);
        var sourceProfile = sourceFixture.Profiles.CreateImportedAsync(
                "Configured group",
                null,
                null,
                new[] { ModProfileMember.FromLibraryItem(sourceItem, enabled: true) })
            .AsTask().GetAwaiter().GetResult();
        var sourceService = new ModProfileTransferService(sourceFixture.Library, sourceFixture.Profiles);
        using var package = new MemoryStream();

        var exported = sourceService.ExportPackageAsync(
                ProfileId.Parse(sourceProfile.Id),
                package,
                includeConfig: true)
            .AsTask().GetAwaiter().GetResult();
        TestHarness.Equal(1, exported.PackagedItems);
        TestHarness.Equal(0, exported.ExcludedConfigFiles);
        TestHarness.Equal(1, exported.IncludedConfigFiles);
        TestHarness.True(exported.Document.IncludesConfigFiles);
        var packagedId = exported.Document.Members.Single().PackagedContentId;
        package.Position = 0;
        using (var archive = new ZipArchive(package, ZipArchiveMode.Read, leaveOpen: true))
        {
            TestHarness.True(archive.Entries.Any(entry => entry.FullName.EndsWith("/config.json", StringComparison.OrdinalIgnoreCase)));
        }

        using var destinationFixture = new TransferFixture();
        var destinationService = new ModProfileTransferService(destinationFixture.Library, destinationFixture.Profiles);
        package.Position = 0;
        using var transaction = new AsyncTransactionScope(destinationService.CreatePackageImportTransaction("configured.zip"));
        transaction.Value.ScanAsync(package).AsTask().GetAwaiter().GetResult();
        transaction.Value.CommitAsync().AsTask().GetAwaiter().GetResult();
        var imported = transaction.Value.ImportResult
            ?? throw new InvalidOperationException("The configured group import result is missing.");

        TestHarness.Equal(packagedId, imported.AddedItems.Single().ImportedContentId);
        var importedRoot = destinationFixture.Library.Layout.GetItemFilesDirectory(imported.AddedItems.Single().LibraryItemId);
        TestHarness.True(File.Exists(Path.Combine(importedRoot, "config.json")));
        TestHarness.Equal("{\"private\":true}", File.ReadAllText(Path.Combine(importedRoot, "config.json")));
    }

    public static void PromotesScannedCompletePackagesAndLeavesModArchivesUnchanged()
    {
        using var sourceFixture = new TransferFixture();
        var sourceItem = sourceFixture.ImportMod("Example.RoutedPackage", includeConfig: false);
        var sourceProfile = sourceFixture.Profiles.CreateImportedAsync(
                "Routed group",
                null,
                null,
                [ModProfileMember.FromLibraryItem(sourceItem, enabled: true)])
            .AsTask().GetAwaiter().GetResult();
        using var package = new MemoryStream();
        new ModProfileTransferService(sourceFixture.Library, sourceFixture.Profiles)
            .ExportPackageAsync(ProfileId.Parse(sourceProfile.Id), package)
            .AsTask().GetAwaiter().GetResult();

        using var destinationFixture = new TransferFixture();
        var destination = new ModProfileTransferService(destinationFixture.Library, destinationFixture.Profiles);
        package.Position = 0;
        var scannedPackage = destinationFixture.Library.CreateInstallTransaction("group.zip");
        scannedPackage.ScanAsync(package).AsTask().GetAwaiter().GetResult();
        var promoted = destination.TryPromotePackageImportTransactionAsync(scannedPackage)
            .AsTask().GetAwaiter().GetResult()
            ?? throw new InvalidOperationException("The complete package was not detected.");
        using (var transaction = new AsyncTransactionScope(promoted))
            transaction.Value.CommitAsync().AsTask().GetAwaiter().GetResult();

        TestHarness.Equal("Routed group", promoted.ImportResult?.Profile.DisplayName);
        TestHarness.Equal(1, promoted.ImportResult?.Profile.Members.Count ?? 0);
        TestHarness.Equal(2, destinationFixture.Profiles.ListAsync().AsTask().GetAwaiter().GetResult().Count);

        using var ordinaryArchive = new MemoryStream();
        using (var zip = new ZipArchive(ordinaryArchive, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(zip, "Mod/manifest.json", """
                {"Name":"Ordinary","Author":"Test","Version":"1.0.0","UniqueID":"Example.Ordinary","EntryDll":"Mod.dll"}
                """);
            WriteEntry(zip, "Mod/Mod.dll", "managed-placeholder");
        }
        ordinaryArchive.Position = 0;
        var ordinary = destinationFixture.Library.CreateInstallTransaction("ordinary.zip");
        try
        {
            ordinary.ScanAsync(ordinaryArchive).AsTask().GetAwaiter().GetResult();
            TestHarness.Equal<ModProfilePackageImportTransaction?>(
                null,
                destination.TryPromotePackageImportTransactionAsync(ordinary).AsTask().GetAwaiter().GetResult());
            ordinary.CommitAsync().AsTask().GetAwaiter().GetResult();
        }
        finally
        {
            ordinary.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        TestHarness.Equal(2, destinationFixture.Library.ReadAsync().AsTask().GetAwaiter().GetResult().Items.Count);
        TestHarness.Equal(2, destinationFixture.Profiles.ListAsync().AsTask().GetAwaiter().GetResult().Count);
    }

    public static void PromotesLegacyCompletePackagesWithoutBundles()
    {
        using var sourceFixture = new TransferFixture();
        var sourceItem = sourceFixture.ImportMod("Example.LegacyPackage", includeConfig: false);
        var sourceProfile = sourceFixture.Profiles.CreateImportedAsync(
                "Legacy package group",
                null,
                null,
                [ModProfileMember.FromLibraryItem(sourceItem, enabled: true)])
            .AsTask().GetAwaiter().GetResult();
        using var package = new MemoryStream();
        new ModProfileTransferService(sourceFixture.Library, sourceFixture.Profiles)
            .ExportPackageAsync(ProfileId.Parse(sourceProfile.Id), package)
            .AsTask().GetAwaiter().GetResult();
        RemovePackageBundles(package);

        using var destinationFixture = new TransferFixture();
        var destination = new ModProfileTransferService(destinationFixture.Library, destinationFixture.Profiles);
        var scanned = destinationFixture.Library.CreateInstallTransaction("legacy-group.zip");
        scanned.ScanAsync(package).AsTask().GetAwaiter().GetResult();
        var promoted = destination.TryPromotePackageImportTransactionAsync(scanned)
            .AsTask().GetAwaiter().GetResult()
            ?? throw new InvalidOperationException("The legacy complete package was not detected.");
        using (var transaction = new AsyncTransactionScope(promoted))
            transaction.Value.CommitAsync().AsTask().GetAwaiter().GetResult();

        TestHarness.Equal("Legacy package group", promoted.ImportResult?.Profile.DisplayName);
        TestHarness.Equal(1, promoted.ImportResult?.Profile.Members.Count ?? 0);
    }

    public static void CompletePackageCommitUsesMutationGate()
    {
        using var sourceFixture = new TransferFixture();
        var sourceItem = sourceFixture.ImportMod("Example.GatedPackage", includeConfig: false);
        var sourceProfile = sourceFixture.Profiles.CreateImportedAsync(
                "Gated group",
                null,
                null,
                [ModProfileMember.FromLibraryItem(sourceItem, enabled: true)])
            .AsTask().GetAwaiter().GetResult();
        using var package = new MemoryStream();
        new ModProfileTransferService(sourceFixture.Library, sourceFixture.Profiles)
            .ExportPackageAsync(ProfileId.Parse(sourceProfile.Id), package)
            .AsTask().GetAwaiter().GetResult();

        using var destinationFixture = new TransferFixture();
        var existing = destinationFixture.ImportMod("Example.GatedPackage", includeConfig: false);
        var before = destinationFixture.Library.ReadAsync().AsTask().GetAwaiter().GetResult();
        var gate = new RejectingMutationGate();
        var service = new ModProfileTransferService(destinationFixture.Library, destinationFixture.Profiles, gate);
        package.Position = 0;
        using var transaction = new AsyncTransactionScope(service.CreatePackageImportTransaction("gated.zip"));
        transaction.Value.ScanAsync(package).AsTask().GetAwaiter().GetResult();

        TestHarness.Throws<ModContentInUseException>(() =>
            transaction.Value.CommitAsync().AsTask().GetAwaiter().GetResult());

        TestHarness.Equal(existing.LibraryItemId, gate.AffectedItemIds.Single());
        var after = destinationFixture.Library.ReadAsync().AsTask().GetAwaiter().GetResult();
        TestHarness.Equal(before.Revision, after.Revision);
        TestHarness.Equal(before.Items.Single().LibraryItemId, after.Items.Single().LibraryItemId);
        TestHarness.Equal(1, destinationFixture.Profiles.ListAsync().AsTask().GetAwaiter().GetResult().Count);
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

    public static void CompletePackagePreservesBundles()
    {
        using var sourceFixture = new TransferFixture();
        var sourceItems = sourceFixture.ImportBundle();
        var sourceProfile = sourceFixture.Profiles.CreateImportedAsync(
                "Bundled group",
                "Bundle roundtrip",
                null,
                sourceItems.Select((item, index) => ModProfileMember.FromLibraryItem(item, enabled: index == 0)).ToArray())
            .AsTask().GetAwaiter().GetResult();
        var sourceService = new ModProfileTransferService(sourceFixture.Library, sourceFixture.Profiles);
        using var package = new MemoryStream();

        var exported = sourceService.ExportPackageAsync(ProfileId.Parse(sourceProfile.Id), package)
            .AsTask().GetAwaiter().GetResult();
        TestHarness.Equal(1, exported.Document.Bundles.Count);
        TestHarness.Equal(2, exported.Document.Bundles[0].Members.Count);
        using var singleBundle = new MemoryStream();
        var bundleExport = sourceService.ExportBundlePackageAsync(
                exported.Document.Bundles[0].PortableBundleId,
                singleBundle)
            .AsTask().GetAwaiter().GetResult();
        TestHarness.Equal(2, bundleExport.PackagedItems);
        TestHarness.Equal(1, bundleExport.Document.Bundles.Count);
        TestHarness.Equal(2, bundleExport.Document.Members.Count);

        using var destinationFixture = new TransferFixture();
        var destinationService = new ModProfileTransferService(destinationFixture.Library, destinationFixture.Profiles);
        package.Position = 0;
        using var transaction = new AsyncTransactionScope(destinationService.CreatePackageImportTransaction("bundle.zip"));
        transaction.Value.ScanAsync(package).AsTask().GetAwaiter().GetResult();
        transaction.Value.CommitAsync().AsTask().GetAwaiter().GetResult();

        var imported = transaction.Value.ImportResult ?? throw new InvalidOperationException("The bundled import result is missing.");
        TestHarness.Equal(2, imported.Profile.Members.Count);
        TestHarness.Equal(1, imported.Profile.Members.Count(member => member.Enabled));
        var library = destinationFixture.Library.ReadAsync().AsTask().GetAwaiter().GetResult();
        TestHarness.Equal(1, library.BundleCatalog.Bundles.Count);
        TestHarness.Equal(ModBundleOrigin.Transfer, library.BundleCatalog.Bundles[0].Origin);
        TestHarness.Equal("Example Product", library.BundleCatalog.Bundles[0].DisplayName);
        TestHarness.Equal(2, ModManagementProjection.Create(library).Items.Single().Members.Count);
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
                Array.Empty<ModProfileTransferBundle>(),
                now);
            var metadata = archive.CreateEntry(ModProfileTransferDocument.PackageEntryName);
            using var output = metadata.Open();
            JsonSerializer.Serialize(output, document, JsonOptions);
        }
        stream.Position = 0;
        return stream;
    }

    private static void RemovePackageBundles(MemoryStream package)
    {
        package.Position = 0;
        using (var archive = new ZipArchive(package, ZipArchiveMode.Update, leaveOpen: true))
        {
            var entry = archive.GetEntry(ModProfileTransferDocument.PackageEntryName)
                ?? throw new InvalidOperationException("The exported package metadata is missing.");
            JsonObject metadata;
            using (var input = entry.Open())
                metadata = JsonNode.Parse(input)?.AsObject()
                    ?? throw new InvalidOperationException("The exported package metadata is empty.");
            entry.Delete();
            TestHarness.True(metadata.Remove("bundles"));
            var replacement = archive.CreateEntry(ModProfileTransferDocument.PackageEntryName);
            using var output = replacement.Open();
            JsonSerializer.Serialize(output, metadata, JsonOptions);
        }
        package.Position = 0;
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

        public ModLibraryItem ImportMod(
            string uniqueId,
            bool includeConfig,
            string version = "1.0.0",
            string dllContent = "managed-placeholder")
        {
            using var archive = new MemoryStream();
            using (var zip = new ZipArchive(archive, ZipArchiveMode.Create, leaveOpen: true))
            {
                WriteEntry(zip, "Mod/manifest.json", $$"""
                    {"Name":"{{uniqueId}}","Author":"Test","Version":"{{version}}","UniqueID":"{{uniqueId}}","EntryDll":"Mod.dll"}
                    """);
                WriteEntry(zip, "Mod/Mod.dll", dllContent);
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

        public IReadOnlyList<ModLibraryItem> ImportBundle()
        {
            using var archive = new MemoryStream();
            using (var zip = new ZipArchive(archive, ZipArchiveMode.Create, leaveOpen: true))
            {
                WriteEntry(zip, "Example Product/Code/manifest.json", """
                    {"Name":"Example Product Code","Author":"Test","Version":"1.0.0","UniqueID":"Example.Product.Code","EntryDll":"Code.dll","UpdateKeys":["Nexus:12345"]}
                    """);
                WriteEntry(zip, "Example Product/Code/Code.dll", "code");
                WriteEntry(zip, "Example Product/Content/manifest.json", """
                    {"Name":"Example Product Content","Author":"Test","Version":"1.0.0","UniqueID":"Example.Product.Content","ContentPackFor":{"UniqueID":"Pathoschild.ContentPatcher"},"UpdateKeys":["Nexus:12345"]}
                    """);
                WriteEntry(zip, "Example Product/Content/content.json", "{}");
            }
            archive.Position = 0;
            var transaction = Library.CreateInstallTransaction("bundle.zip");
            try
            {
                transaction.ScanAsync(archive).AsTask().GetAwaiter().GetResult();
                transaction.CommitAsync().AsTask().GetAwaiter().GetResult();
                return transaction.ImportResult?.AllItems
                    ?? throw new InvalidOperationException("The bundled test Mod import failed.");
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

    private sealed class RejectingMutationGate : IModContentMutationGate
    {
        public IReadOnlyList<string> AffectedItemIds { get; private set; } = Array.Empty<string>();

        public ValueTask<IAsyncDisposable> AcquireAsync(
            IReadOnlyCollection<string> affectedLibraryItemIds,
            CancellationToken cancellationToken = default)
        {
            AffectedItemIds = affectedLibraryItemIds.ToArray();
            throw new ModContentInUseException(AffectedItemIds);
        }
    }

    private static void WriteEntry(ZipArchive archive, string path, string content)
    {
        var entry = archive.CreateEntry(path);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(content);
    }
}
