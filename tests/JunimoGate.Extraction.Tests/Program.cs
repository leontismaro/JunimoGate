using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using JunimoGate.Core;
using JunimoGate.Extraction;
using JunimoGate.Tests;
using K4os.Compression.LZ4;

var digestA = Sha256Digest.Parse(new string('a', 64));
var digestB = Sha256Digest.Parse(new string('b', 64));
var digestC = Sha256Digest.Parse(new string('c', 64));

WorkspaceCacheKey Key(
    string packageName = "com.chucklefish.stardewvalley",
    long versionCode = 123,
    string abi = "arm64-v8a",
    IEnumerable<Sha256Digest>? sources = null,
    SigningIdentity? signing = null,
    string extractor = "extract-v1",
    string recipe = "rewrite-v1",
    string smapi = "smapi-build-1") =>
    WorkspaceCacheKey.Create(packageName, versionCode, abi, signing ?? new SigningIdentity([digestA]), sources ?? [digestA, digestB], extractor, recipe, smapi);

return TestHarness.Run(
    ("APK classifier recognizes content with case and slash normalization", () =>
    {
        TestHarness.Equal(ApkContentRole.GameContent, ApkEntryRoleClassifier.Classify("ASSETS\\content\\Maps\\Farm.xnb"));
        TestHarness.Equal(ApkContentRole.GameContent, ApkEntryRoleClassifier.Classify("assets/Content/"));
    }),
    ("APK classifier recognizes exact legacy assembly shape", () =>
    {
        TestHarness.Equal(ApkContentRole.LegacyAssemblyBlob, ApkEntryRoleClassifier.Classify("Assemblies\\assemblies.blob"));
        TestHarness.Equal(ApkContentRole.None, ApkEntryRoleClassifier.Classify("other/assemblies/foo.blob"));
        TestHarness.Equal(ApkContentRole.None, ApkEntryRoleClassifier.Classify("assemblies/nested/foo.blob"));
        TestHarness.Equal(ApkContentRole.None, ApkEntryRoleClassifier.Classify("assemblies/foo.blob.so"));
    }),
    ("APK classifier recognizes exact modern assembly shape", () =>
    {
        TestHarness.Equal(ApkContentRole.ModernAssemblyBlob, ApkEntryRoleClassifier.Classify("LIB\\ARM64-V8A\\LIBASSEMBLIES.ARM64-V8A.BLOB.SO"));
        TestHarness.Equal(ApkContentRole.None, ApkEntryRoleClassifier.Classify("lib/arm64-v8a/libassemblies.x86_64.blob.so"));
        TestHarness.Equal(ApkContentRole.None, ApkEntryRoleClassifier.Classify("lib/arm64-v8a/notlibassemblies.arm64-v8a.blob.so"));
        TestHarness.Equal(ApkContentRole.None, ApkEntryRoleClassifier.Classify("prefix/lib/arm64-v8a/libassemblies.arm64-v8a.blob.so"));
    }),
    ("APK inventory combines roles and skips null entries without split assumptions", () =>
    {
        var inventory = ApkEntryInventory.Classify(new string?[]
        {
            "assets/Content/Data/ObjectInformation.xnb",
            null,
            "lib/arm64-v8a/libassemblies.arm64-v8a.blob.so",
        });
        TestHarness.True(inventory.Contains(ApkContentRole.GameContent));
        TestHarness.True(inventory.Contains(ApkContentRole.ModernAssemblyBlob));
        TestHarness.False(inventory.Contains(ApkContentRole.LegacyAssemblyBlob));
    }),
    ("Synthetic legacy AssemblyStore v1 parses selected ABI and copies MZ/XALZ images", () =>
    {
        var stardew = "MZ-stardew-v1"u8.ToArray();
        var monoGame = "MZ-monogame-v1"u8.ToArray();
        var directory = CreateTestDirectory();
        try
        {
            var path = WriteLegacyApk(directory, "legacy-v1.apk", stardew, monoGame, includeContent: false);
            using var archive = ZipFile.OpenRead(path);
            using var store = LegacyAssemblyStoreSet.Open(archive, "arm64-v8a");
            TestHarness.Equal(2, store.Items.Count);
            var stardewItem = store.Items.Single(item => item.Name == "StardewValley.dll");
            var monoGameItem = store.Items.Single(item => item.Name == "MonoGame.Framework.dll");
            using var stardewOutput = new MemoryStream();
            using var monoGameOutput = new MemoryStream();
            store.CopyAssemblyImageToAsync(stardewItem, stardewOutput).AsTask().GetAwaiter().GetResult();
            store.CopyAssemblyImageToAsync(monoGameItem, monoGameOutput).AsTask().GetAwaiter().GetResult();
            TestHarness.True(stardewOutput.ToArray().SequenceEqual(stardew));
            TestHarness.True(monoGameOutput.ToArray().SequenceEqual(monoGame));
            TestHarness.Equal(LegacyAssemblyStoreApkPath.BaseStorePath, stardewItem.SourceEntry);
            TestHarness.Equal(LegacyAssemblyStoreApkPath.GetAbiStorePath("arm64-v8a"), monoGameItem.SourceEntry);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }),
    ("Synthetic legacy AssemblyStore rejects an out-of-range manifest mapping", () =>
    {
        var directory = CreateTestDirectory();
        try
        {
            var path = WriteLegacyApk(
                directory,
                "legacy-invalid.apk",
                "MZ-stardew-v1"u8.ToArray(),
                "MZ-monogame-v1"u8.ToArray(),
                includeContent: false,
                monoGameStoreIndex: 10);
            using var archive = ZipFile.OpenRead(path);
            TestHarness.Throws<AssemblyStoreFormatException>(() => LegacyAssemblyStoreSet.Open(archive, "arm64-v8a"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }),
    ("Synthetic ELF64 AssemblyStore v2 parses and copies MZ image", () =>
    {
        var image = "MZ-synthetic-managed-image"u8.ToArray();
        using var fixture = BuildElfStore(image);
        using var store = AssemblyStoreV2.Open(fixture, leaveOpen: true, sourceName: "synthetic.so");
        TestHarness.Equal(AssemblyStoreV2.Arm64Version, store.RawVersion);
        TestHarness.Equal("arm64-v8a", store.Abi);
        TestHarness.Equal(1, store.Items.Count);
        TestHarness.Equal("Synthetic.dll", store.Items[0].Name);
        TestHarness.Equal((uint)image.Length, store.Items[0].DataSize);
        using var output = new MemoryStream();
        store.CopyAssemblyImageTo(store.Items[0], output);
        TestHarness.True(output.ToArray().SequenceEqual(image));
    }),
    ("Synthetic AssemblyStore rejects bad XABA magic", () =>
    {
        using var fixture = BuildElfStore("MZ-image"u8.ToArray());
        fixture.Position = 0x100;
        fixture.WriteByte(0);
        fixture.Position = 0;
        TestHarness.Throws<AssemblyStoreFormatException>(() => AssemblyStoreV2.Open(fixture, leaveOpen: true));
    }),
    ("Synthetic AssemblyStore rejects unsupported full version", () =>
    {
        using var fixture = BuildElfStore("MZ-image"u8.ToArray());
        fixture.Position = 0x104;
        fixture.Write([0x03, 0x00, 0x01, 0x80]);
        fixture.Position = 0;
        TestHarness.Throws<AssemblyStoreFormatException>(() => AssemblyStoreV2.Open(fixture, leaveOpen: true));
    }),
    ("Synthetic ELF rejects missing payload section", () =>
    {
        using var fixture = BuildElfStore("MZ-image"u8.ToArray(), includePayloadSection: false);
        TestHarness.Throws<AssemblyStoreFormatException>(() => AssemblyStoreV2.Open(fixture, leaveOpen: true));
    }),
    ("Synthetic AssemblyStore rejects out-of-bounds data offset", () =>
    {
        using var fixture = BuildElfStore("MZ-image"u8.ToArray(), dataOffsetOverride: 0xFFFF_FFF0);
        TestHarness.Throws<AssemblyStoreFormatException>(() => AssemblyStoreV2.Open(fixture, leaveOpen: true));
    }),
    ("Synthetic AssemblyStore rejects oversized images and metadata overlap", () =>
    {
        using var oversized = BuildElfStore(
            "MZ-image"u8.ToArray(),
            dataSizeOverride: checked((uint)AssemblyStoreV2.MaximumAssemblyImageSize + 1));
        TestHarness.Throws<AssemblyStoreFormatException>(() => AssemblyStoreV2.Open(oversized, leaveOpen: true));

        using var overlapping = BuildElfStore("MZ-image"u8.ToArray(), dataOffsetOverride: 60);
        TestHarness.Throws<AssemblyStoreFormatException>(() => AssemblyStoreV2.Open(overlapping, leaveOpen: true));
    }),
    ("Synthetic AssemblyStore rejects descriptor index out of bounds", () =>
    {
        using var fixture = BuildElfStore("MZ-image"u8.ToArray(), descriptorIndexOverride: 1);
        TestHarness.Throws<AssemblyStoreFormatException>(() => AssemblyStoreV2.Open(fixture, leaveOpen: true));
    }),
    ("Synthetic XALZ payload decompresses and validates output", () =>
    {
        var image = new byte[16 * 1024];
        image[0] = (byte)'M';
        image[1] = (byte)'Z';
        Array.Fill(image, (byte)0x5A, 2, image.Length - 2);
        var xalz = BuildXalz(image, descriptorIndex: 0);
        using var fixture = BuildElfStore(xalz);
        using var store = AssemblyStoreV2.Open(fixture, leaveOpen: true);
        using var output = new MemoryStream();
        store.CopyAssemblyImageTo(store.Items[0], output);
        TestHarness.True(output.ToArray().SequenceEqual(image));
    }),
    ("XALZ descriptor mismatch is rejected during extraction", () =>
    {
        var xalz = BuildXalz("MZ-image"u8.ToArray(), descriptorIndex: 4);
        using var fixture = BuildElfStore(xalz);
        using var store = AssemblyStoreV2.Open(fixture, leaveOpen: true);
        TestHarness.Throws<AssemblyStoreFormatException>(() => store.CopyAssemblyImageTo(store.Items[0], new MemoryStream()));
    }),
    ("APK classifier and real reader use the same modern path matcher", () =>
    {
        using var apkBytes = new MemoryStream();
        using (var archive = new ZipArchive(apkBytes, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry("LIB/ARM64-V8A/LIBASSEMBLIES.ARM64-V8A.BLOB.SO", CompressionLevel.NoCompression);
            using var target = entry.Open();
            using var fixture = BuildElfStore("MZ-image"u8.ToArray());
            fixture.CopyTo(target);
        }

        apkBytes.Position = 0;
        using var readArchive = new ZipArchive(apkBytes, ZipArchiveMode.Read, leaveOpen: true);
        var entryNames = readArchive.Entries.Select(entry => entry.FullName).ToArray();
        TestHarness.Equal(ApkContentRole.ModernAssemblyBlob, ApkEntryInventory.Classify(entryNames).Roles);
        var candidates = AssemblyStoreV2.FindInApk(readArchive);
        TestHarness.Equal(1, candidates.Count);
        using var store = candidates[0].Open();
        TestHarness.Equal(1, store.Items.Count);
    }),
    ("Extraction transaction rejects unsafe and duplicate basenames", () =>
    {
        foreach (var unsafeName in new[] { "../bad.dll", ".", "manifest.json", "CON.dll", "bad:.dll", " bad.dll" })
        {
            TestHarness.Throws<ArgumentException>(() => AssemblyExtractionTransaction.ValidateAssemblyBaseName(unsafeName));
        }
        var directory = Path.Combine(Path.GetTempPath(), $"junimogate-test-{Guid.NewGuid():N}");
        try
        {
            using var fixture = BuildElfStore("MZ-image"u8.ToArray());
            using var store = AssemblyStoreV2.Open(fixture, leaveOpen: true);
            using var transaction = new AssemblyExtractionTransaction(directory);
            var first = transaction.ExtractAsync(store, store.Items[0]).AsTask().GetAwaiter().GetResult();
            TestHarness.True(File.Exists(first.FullPath));
            TestHarness.Equal(64, first.Sha256.Length);
            TestHarness.Throws<IOException>(() => transaction.ExtractAsync(store, store.Items[0]).AsTask().GetAwaiter().GetResult());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }),
    ("Workspace cache key ignores APK digest order", () =>
    {
        TestHarness.Equal(Key(sources: [digestA, digestB]), Key(sources: [digestB, digestA]));
        TestHarness.Throws<ArgumentException>(() => Key(sources: [default]));
    }),
    ("Workspace cache key canonicalizes current signers and preserves rotation order", () =>
    {
        TestHarness.Equal(
            Key(signing: new SigningIdentity([digestA, digestB])),
            Key(signing: new SigningIdentity([digestB, digestA])));
        TestHarness.False(Key(signing: new SigningIdentity([digestA], [digestB, digestC, digestA])).Equals(
            Key(signing: new SigningIdentity([digestA], [digestC, digestB, digestA]))));
    }),
    ("Workspace cache key changes for every identity field", () =>
    {
        var baseline = Key();
        var variants = new[]
        {
            Key(packageName: "com.chucklefish.stardewvalleysamsung"),
            Key(versionCode: 124),
            Key(abi: "x86_64"),
            Key(sources: [digestA, digestC]),
            Key(signing: new SigningIdentity([digestB])),
            Key(signing: new SigningIdentity([digestA], [digestC, digestA])),
            Key(extractor: "extract-v2"),
            Key(recipe: "rewrite-v2"),
            Key(smapi: "smapi-build-2"),
        };

        foreach (var variant in variants)
        {
            TestHarness.False(baseline.Equals(variant), "An identity-field change must invalidate the cache key.");
        }
    }),
    ("APK inventory records native and modern AssemblyStore ABIs", () =>
    {
        var inventory = ApkEntryInventory.Classify([
            "lib/x86_64/libnative.so",
            "lib/arm64-v8a/libassemblies.arm64-v8a.blob.so",
            "lib/ARM64-V8A/libother.so",
        ]);
        TestHarness.True(inventory.NativeAbis.SequenceEqual(["arm64-v8a", "x86_64"], StringComparer.OrdinalIgnoreCase));
        TestHarness.True(inventory.ModernAssemblyStoreAbis.SequenceEqual(["arm64-v8a"], StringComparer.OrdinalIgnoreCase));
    }),
    ("Coordinator returns both candidates independent of split names and source order", () =>
    {
        var directory = CreateTestDirectory();
        try
        {
            var playBase = WriteApk(directory, "one.apk", ["assets/Content/Data/game.xnb"]);
            var playAlpha = WriteApk(directory, "two.apk", ["assets/other.txt"]);
            var playZeta = WriteApk(directory, "three.apk", ["lib/arm64-v8a/libassemblies.arm64-v8a.blob.so"]);
            var galaxyBase = WriteApk(directory, "galaxy.apk", [
                "assets/Content/Maps/Farm.xnb",
                "assemblies/game.blob",
                "lib/arm64-v8a/libgame.so",
            ]);
            var signing = new SigningIdentity([digestA]);
            const string playPackage = "com.chucklefish.stardewvalley";
            const string galaxyPackage = "com.chucklefish.stardewvalleysamsung";
            var playFirst = Snapshot(playPackage, signing,
                new PackageApkSourceSnapshot(playZeta, false, "feature.zeta"),
                new PackageApkSourceSnapshot(playBase, true, null),
                new PackageApkSourceSnapshot(playAlpha, false, "config.alpha"));
            var playSecond = Snapshot(playPackage, new SigningIdentity([digestA]),
                new PackageApkSourceSnapshot(playAlpha, false, "config.alpha"),
                new PackageApkSourceSnapshot(playZeta, false, "feature.zeta"),
                new PackageApkSourceSnapshot(playBase, true, null));
            var galaxy = Snapshot(galaxyPackage, signing, new PackageApkSourceSnapshot(galaxyBase, true, null));
            var provider = new QueueSnapshotProvider(
                (playPackage, [playFirst, playSecond]),
                (galaxyPackage, [galaxy, galaxy]));
            var report = new GameInstallationDiscoveryCoordinator(provider)
                .AnalyzeAsync([playPackage, galaxyPackage])
                .AsTask().GetAwaiter().GetResult();

            TestHarness.Equal(2, report.Packages.Count);
            TestHarness.Equal(2, report.Candidates.Count);
            var playCandidate = report.Packages[0].Candidate!;
            var playSources = playCandidate.Installation.ApkSources;
            TestHarness.True(playSources.Select(static source => source.Label).SequenceEqual(["base", "split-1", "split-2"]));
            TestHarness.Equal("config.alpha", playSources[1].SplitName);
            TestHarness.Equal("feature.zeta", playSources[2].SplitName);
            TestHarness.Equal(new FileInfo(playBase).Length, playSources[0].Size);
            TestHarness.True(playSources.All(static source => source.Digest.IsValid));
            var playInventories = playCandidate.SourceInventories;
            TestHarness.True(playInventories.Select(static inventory => inventory.SourceLabel).SequenceEqual(["base", "split-1", "split-2"]));
            TestHarness.True(playInventories[0].Roles.SequenceEqual([ApkSourceRoleNames.GameContent]));
            TestHarness.Equal(0, playInventories[1].Roles.Count);
            TestHarness.True(playInventories[2].Roles.SequenceEqual([ApkSourceRoleNames.ModernAssemblyBlob]));
            TestHarness.True(playInventories[2].NativeAbis.SequenceEqual(["arm64-v8a"]));
            TestHarness.True(playInventories[2].AssemblyStoreAbis.SequenceEqual(["arm64-v8a"]));
            TestHarness.Equal(GameCertificateStatus.Unrecognized, playCandidate.CertificateVerification.Status);
            TestHarness.False(playCandidate.CertificateVerification.AllowsCodeExecution);
            TestHarness.True(HasCode(report.Packages[0], GameDiscoveryErrorCodes.GameCertificateUnrecognized));
            var galaxyCandidate = report.Packages[1].Candidate!;
            TestHarness.Equal(GameInstallationDiscoveryCoordinator.SupportedAbi, galaxyCandidate.Installation.SelectedAbi);
            TestHarness.Equal(GameCertificateStatus.NotConfigured, galaxyCandidate.CertificateVerification.Status);
            TestHarness.False(galaxyCandidate.CertificateVerification.AllowsCodeExecution);
            TestHarness.True(HasCode(report.Packages[1], GameDiscoveryErrorCodes.GameCertificatePolicyNotConfigured));
            TestHarness.True(galaxyCandidate.SourceInventories[0].Roles.SequenceEqual([
                ApkSourceRoleNames.GameContent,
                ApkSourceRoleNames.LegacyAssemblyBlob,
            ]));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }),
    ("Coordinator reports missing content and assembly roles", () =>
    {
        var directory = CreateTestDirectory();
        try
        {
            const string packageName = "com.example.game";
            var signing = new SigningIdentity([digestA]);
            var contentOnly = WriteApk(directory, "content.apk", ["assets/Content/file.xnb", "lib/arm64-v8a/libgame.so"]);
            var assemblyOnly = WriteApk(directory, "assembly.apk", ["lib/arm64-v8a/libassemblies.arm64-v8a.blob.so"]);
            var contentSnapshot = Snapshot(packageName, signing, new PackageApkSourceSnapshot(contentOnly, true, null));
            var assemblySnapshot = Snapshot(packageName, signing, new PackageApkSourceSnapshot(assemblyOnly, true, null));

            var contentReport = AnalyzeSingle(packageName, contentSnapshot, contentSnapshot);
            TestHarness.True(HasCode(contentReport, GameDiscoveryErrorCodes.AssemblySourceMissing));
            var assemblyReport = AnalyzeSingle(packageName, assemblySnapshot, assemblySnapshot);
            TestHarness.True(HasCode(assemblyReport, GameDiscoveryErrorCodes.ContentSourceMissing));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }),
    ("Coordinator distinguishes unsupported and conflicting ABI evidence", () =>
    {
        var directory = CreateTestDirectory();
        try
        {
            const string packageName = "com.example.game";
            var signing = new SigningIdentity([digestA]);
            var unsupported = WriteApk(directory, "unsupported.apk", [
                "assets/Content/file.xnb",
                "lib/x86_64/libassemblies.x86_64.blob.so",
            ]);
            var conflictBase = WriteApk(directory, "conflict-base.apk", [
                "assets/Content/file.xnb",
                "lib/arm64-v8a/libgame.so",
            ]);
            var conflictAssembly = WriteApk(directory, "conflict-assembly.apk", [
                "lib/x86_64/libassemblies.x86_64.blob.so",
            ]);
            var unsupportedSnapshot = Snapshot(packageName, signing, new PackageApkSourceSnapshot(unsupported, true, null));
            var conflictSnapshot = Snapshot(packageName, signing,
                new PackageApkSourceSnapshot(conflictAssembly, false, "assembly.x86"),
                new PackageApkSourceSnapshot(conflictBase, true, null));

            TestHarness.True(HasCode(
                AnalyzeSingle(packageName, unsupportedSnapshot, unsupportedSnapshot),
                GameDiscoveryErrorCodes.AbiUnsupported));
            TestHarness.True(HasCode(
                AnalyzeSingle(packageName, conflictSnapshot, conflictSnapshot),
                GameDiscoveryErrorCodes.AbiConflict));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }),
    ("Coordinator detects package version path and signer races", () =>
    {
        var directory = CreateTestDirectory();
        try
        {
            const string packageName = "com.example.game";
            var apk = WriteApk(directory, "base.apk", [
                "assets/Content/file.xnb",
                "lib/arm64-v8a/libassemblies.arm64-v8a.blob.so",
            ]);
            var signingA = new SigningIdentity([digestA]);
            var signingB = new SigningIdentity([digestB]);
            var baseline = Snapshot(packageName, signingA, new PackageApkSourceSnapshot(apk, true, null));
            var changedSnapshots = new[]
            {
                new PackageInstallationSnapshot("com.example.changed", "1.0", 1, signingA, [new PackageApkSourceSnapshot(apk, true, null)]),
                new PackageInstallationSnapshot(packageName, "2.0", 2, signingA, [new PackageApkSourceSnapshot(apk, true, null)]),
                Snapshot(packageName, signingA, new PackageApkSourceSnapshot(Path.Combine(directory, "changed.apk"), true, null)),
                Snapshot(packageName, signingB, new PackageApkSourceSnapshot(apk, true, null)),
            };

            foreach (var changed in changedSnapshots)
            {
                var report = AnalyzeSingle(packageName, baseline, changed);
                TestHarness.True(HasCode(report, GameDiscoveryErrorCodes.PackageChangedDuringScan));
                TestHarness.False(report.IsSuccess);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }),
    ("Coordinator reports missing bad and unreadable APK sources without paths", () =>
    {
        var directory = CreateTestDirectory();
        try
        {
            const string packageName = "com.example.game";
            var signing = new SigningIdentity([digestA]);
            var missing = Path.Combine(directory, "secret-missing.apk");
            var missingSnapshot = Snapshot(packageName, signing, new PackageApkSourceSnapshot(missing, true, null));
            var missingReport = AnalyzeSingle(packageName, missingSnapshot, missingSnapshot);
            TestHarness.True(HasCode(missingReport, GameDiscoveryErrorCodes.ApkSourceMissing));
            TestHarness.False(missingReport.Diagnostics.Any(diagnostic => diagnostic.Detail?.Contains(missing, StringComparison.Ordinal) == true));

            var invalidZip = Path.Combine(directory, "secret-invalid.apk");
            File.WriteAllText(invalidZip, "not a ZIP archive");
            var invalidSnapshot = Snapshot(packageName, signing, new PackageApkSourceSnapshot(invalidZip, true, null));
            var invalidReport = AnalyzeSingle(packageName, invalidSnapshot, invalidSnapshot);
            TestHarness.True(HasCode(invalidReport, GameDiscoveryErrorCodes.ApkSourceInvalidZip));
            TestHarness.False(invalidReport.Diagnostics.Any(diagnostic => diagnostic.Detail?.Contains(invalidZip, StringComparison.Ordinal) == true));

            var unreadableSource = new PackageApkSourceSnapshot(directory, true, null);
            var unreadable = new ApkSourceAnalyzer().AnalyzeAsync(unreadableSource, "base").AsTask().GetAwaiter().GetResult();
            TestHarness.Equal(GameDiscoveryErrorCodes.ApkSourceUnreadable, unreadable.Diagnostic!.Code);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }),
    ("Coordinator reports invalid metadata signing split identity and absence", () =>
    {
        const string packageName = "com.example.game";
        var signing = new SigningIdentity([digestA]);
        var provider = new QueueSnapshotProvider(
            ("missing.package", [null]),
            (packageName, [new PackageInstallationSnapshot(packageName, null, -1, null, [])]),
            ("com.example.split", [new PackageInstallationSnapshot(
                "com.example.split",
                "1.0",
                1,
                signing,
                [
                    new PackageApkSourceSnapshot("relative.apk", true, "wrong-base-name"),
                    new PackageApkSourceSnapshot("relative.apk", false, null),
                ])]));
        var report = new GameInstallationDiscoveryCoordinator(provider)
            .AnalyzeAsync(["missing.package", packageName, "com.example.split"])
            .AsTask().GetAwaiter().GetResult();

        TestHarness.True(HasCode(report.Packages[0], GameDiscoveryErrorCodes.PackageNotFoundOrNotVisible));
        TestHarness.True(HasCode(report.Packages[1], GameDiscoveryErrorCodes.MetadataInvalid));
        TestHarness.True(HasCode(report.Packages[1], GameDiscoveryErrorCodes.SigningInfoMissing));
        TestHarness.True(HasCode(report.Packages[2], GameDiscoveryErrorCodes.SplitIdentityMismatch));
    }),
    ("Cancellation is a stable report result for every requested package", () =>
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var report = new GameInstallationDiscoveryCoordinator(new QueueSnapshotProvider())
            .AnalyzeAsync(["com.example.one", "com.example.two"], cancellation.Token)
            .AsTask().GetAwaiter().GetResult();
        TestHarness.Equal(2, report.Packages.Count);
        TestHarness.True(report.Packages.All(package => HasCode(package, GameDiscoveryErrorCodes.Cancelled)));

        var source = new PackageApkSourceSnapshot(Path.Combine(Path.GetTempPath(), "unused.apk"), true, null);
        var scan = new ApkSourceAnalyzer().AnalyzeAsync(source, "base", cancellation.Token).AsTask().GetAwaiter().GetResult();
        TestHarness.Equal(GameDiscoveryErrorCodes.Cancelled, scan.Diagnostic!.Code);
    }),
    ("Product Deep Prepare reuses one verified APK session without payload rehash", (Action)(() =>
    {
        var directory = CreateTestDirectory();
        try
        {
            var fixture = CreateValidWorkspaceCandidate(
                directory,
                "product-session",
                "1.0",
                [0x11, 0x22, 0x33]);
            var installation = fixture.Installation;
            var snapshot = new PackageInstallationSnapshot(
                installation.PackageName,
                installation.VersionName,
                installation.LongVersionCode,
                installation.SigningIdentity,
                installation.ApkSources.Select(source => new PackageApkSourceSnapshot(
                    source.SourcePath,
                    source.Label == "base",
                    source.SplitName,
                    source.Size,
                    File.GetLastWriteTimeUtc(source.SourcePath).Ticks / TimeSpan.TicksPerMillisecond)));
            var session = GameInstallationPreparationSession
                .OpenAsync(snapshot, installation.PackageName)
                .AsTask().GetAwaiter().GetResult();
            try
            {
                var revalidator = new SequenceCandidateRevalidator(fixture);
                var root = Path.Combine(directory, "runtime");
                var result = new GameWorkspacePreparer(revalidator)
                    .PrepareAsync(
                        new WorkspacePreparationRequest(
                            root,
                            session.Candidate,
                            new WorkspacePreparationOptions
                            {
                                ValidateWrittenPayloadHashes = false,
                                RevalidateInstallation = false,
                                ActivateWorkspace = false,
                            }),
                        session)
                    .AsTask().GetAwaiter().GetResult();

                TestHarness.Equal(WorkspacePreparationStatus.Built, result.Status);
                TestHarness.True(result.ExecutionPlan is not null);
                TestHarness.Equal(0, revalidator.CallCount);
                TestHarness.False(File.Exists(Path.Combine(root, "workspace-state.json")));
                TestHarness.Equal(session.ApkSourceCount, result.Metrics!.ApkSourceOpenCount);
                TestHarness.Equal(session.ApkSourceCount, result.Metrics.ApkFullHashCount);
                TestHarness.Equal(session.ApkBytesHashed, result.Metrics.ApkBytesHashed);
                TestHarness.Equal(0, result.Metrics.WorkspacePayloadHashPassCount);
                var native = new NativeEntryInventoryProbe()
                    .ProbeAsync(result.ExecutionPlan!, session)
                    .AsTask().GetAwaiter().GetResult();
                TestHarness.True(native.IsSuccess);
            }
            finally
            {
                session.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    })),
    ("M4 builds a synthetic multi-APK workspace and cache hit preserves state", () =>
    {
        var directory = CreateTestDirectory();
        try
        {
            var candidate = CreateValidWorkspaceCandidate(directory, "one", "1.0", [0x11, 0x22, 0x33]);
            var root = Path.Combine(directory, "workspace-root");
            var preparer = new GameWorkspacePreparer(new FixedCandidateRevalidator(candidate));
            var builtProgress = new RecordingProgress();
            var first = Prepare(preparer, root, candidate, new WorkspacePreparationOptions { Progress = builtProgress });
            TestHarness.Equal(WorkspacePreparationStatus.Built, first.Status);
            TestHarness.True(first.WorkspacePath is not null && Directory.Exists(first.WorkspacePath));
            TestHarness.Equal(2, first.Statistics!.AssemblyFileCount);
            TestHarness.Equal(1, first.Statistics.ContentFileCount);

            var sourceJson = File.ReadAllText(Path.Combine(first.WorkspacePath!, "source-manifest.json"));
            var extractionJson = File.ReadAllText(Path.Combine(first.WorkspacePath!, "extraction-manifest.json"));
            var rewriteJson = File.ReadAllText(Path.Combine(first.WorkspacePath!, "rewrite-manifest.json"));
            TestHarness.False(sourceJson.Contains("sourcePath", StringComparison.OrdinalIgnoreCase));
            TestHarness.False(extractionJson.Contains("sourcePath", StringComparison.OrdinalIgnoreCase));
            TestHarness.False(rewriteJson.Contains("sourcePath", StringComparison.OrdinalIgnoreCase));
            TestHarness.False(candidate.Installation.ApkSources.Any(source =>
                sourceJson.Contains(source.SourcePath, StringComparison.Ordinal) ||
                extractionJson.Contains(source.SourcePath, StringComparison.Ordinal) ||
                rewriteJson.Contains(source.SourcePath, StringComparison.Ordinal)));
            using (var rewrite = JsonDocument.Parse(rewriteJson))
            {
                var rewriteRoot = rewrite.RootElement;
                TestHarness.True(rewriteRoot.EnumerateObject().Select(static property => property.Name).ToHashSet(StringComparer.Ordinal)
                    .SetEquals(["format", "schema", "cacheKey", "recipe", "status"]));
                TestHarness.Equal("junimogate-rewrite-manifest", rewriteRoot.GetProperty("format").GetString());
                TestHarness.Equal(WorkspacePreparationOptions.DefaultManifestSchema, rewriteRoot.GetProperty("schema").GetString());
                TestHarness.Equal(first.WorkspaceKey, rewriteRoot.GetProperty("cacheKey").GetString());
                TestHarness.Equal("unrewritten:v1", rewriteRoot.GetProperty("recipe").GetString());
                TestHarness.Equal("not-applied", rewriteRoot.GetProperty("status").GetString());
            }
            using (var extraction = JsonDocument.Parse(extractionJson))
            {
                var payloadPaths = extraction.RootElement.GetProperty("files").EnumerateArray()
                    .Select(static file => file.GetProperty("relativePath").GetString())
                    .ToHashSet(StringComparer.Ordinal);
                TestHarness.False(payloadPaths.Overlaps([
                    "source-manifest.json",
                    "extraction-manifest.json",
                    "rewrite-manifest.json",
                ]));
                var actualFiles = Directory.EnumerateFiles(first.WorkspacePath!, "*", SearchOption.AllDirectories)
                    .Select(path => Path.GetRelativePath(first.WorkspacePath!, path).Replace(Path.DirectorySeparatorChar, '/'))
                    .ToHashSet(StringComparer.Ordinal);
                TestHarness.True(actualFiles.SetEquals(payloadPaths.OfType<string>().Concat([
                    "source-manifest.json",
                    "extraction-manifest.json",
                    "rewrite-manifest.json",
                ])));
            }

            TestHarness.Equal(Hash([0x11, 0x22, 0x33]), Hash(File.ReadAllBytes(Path.Combine(first.WorkspacePath!, "Content", "Data", "game.xnb"))));
            TestHarness.Equal(Hash("MZ-stardew"u8.ToArray()), Hash(File.ReadAllBytes(Path.Combine(first.WorkspacePath!, "assemblies", "StardewValley.dll"))));
            TestHarness.Equal(Hash("MZ-monogame"u8.ToArray()), Hash(File.ReadAllBytes(Path.Combine(first.WorkspacePath!, "assemblies", "MonoGame.Framework.dll"))));

            var payloadBytes = first.Statistics.ContentBytes + first.Statistics.AssemblyBytes;
            var actualWorkspaceBytes = Directory.EnumerateFiles(first.WorkspacePath!, "*", SearchOption.AllDirectories)
                .Sum(static path => new FileInfo(path).Length);
            TestHarness.True(first.Metrics is not null);
            var firstMetrics = first.Metrics!;
            TestHarness.True(firstMetrics.DurationMilliseconds > 0);
            TestHarness.True(firstMetrics.PeakTemporaryBytes > payloadBytes);
            TestHarness.Equal(actualWorkspaceBytes, firstMetrics.PeakTemporaryBytes);
            TestHarness.Equal(actualWorkspaceBytes, firstMetrics.FinalWorkspaceBytes);
            TestHarness.True(builtProgress.DistinctStages.SequenceEqual([
                WorkspaceProgressStage.AcquiringLock,
                WorkspaceProgressStage.VerifyingCertificate,
                WorkspaceProgressStage.CleaningStaging,
                WorkspaceProgressStage.VerifyingSources,
                WorkspaceProgressStage.ScanningContent,
                WorkspaceProgressStage.ExtractingContent,
                WorkspaceProgressStage.ExtractingAssemblies,
                WorkspaceProgressStage.WritingManifests,
                WorkspaceProgressStage.ValidatingOutputs,
                WorkspaceProgressStage.Committing,
                WorkspaceProgressStage.RevalidatingInstallation,
                WorkspaceProgressStage.Activating,
                WorkspaceProgressStage.Completed,
            ]));

            var initialState = ReadState(root);
            TestHarness.Equal(first.WorkspaceKey, initialState.Active);
            TestHarness.True(initialState.Previous is null);
            var cacheProgress = new RecordingProgress();
            var second = Prepare(preparer, root, candidate, new WorkspacePreparationOptions { Progress = cacheProgress });
            TestHarness.Equal(WorkspacePreparationStatus.CacheHit, second.Status);
            TestHarness.True(second.Metrics is not null);
            var secondMetrics = second.Metrics!;
            TestHarness.True(secondMetrics.DurationMilliseconds > 0);
            TestHarness.Equal(0L, secondMetrics.PeakTemporaryBytes);
            TestHarness.Equal(firstMetrics.FinalWorkspaceBytes, secondMetrics.FinalWorkspaceBytes);
            TestHarness.True(cacheProgress.DistinctStages.SequenceEqual([
                WorkspaceProgressStage.AcquiringLock,
                WorkspaceProgressStage.VerifyingCertificate,
                WorkspaceProgressStage.CleaningStaging,
                WorkspaceProgressStage.ValidatingCache,
                WorkspaceProgressStage.RevalidatingInstallation,
                WorkspaceProgressStage.Activating,
                WorkspaceProgressStage.Completed,
            ]));
            var secondState = ReadState(root);
            TestHarness.Equal(first.WorkspaceKey, secondState.Active);
            TestHarness.True(secondState.Previous is null);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }),
    ("M4 switching candidates retains the prior active workspace key", () =>
    {
        var directory = CreateTestDirectory();
        try
        {
            var root = Path.Combine(directory, "workspace-root");
            var firstCandidate = CreateValidWorkspaceCandidate(directory, "first", "1.0", [0x01]);
            var first = Prepare(new GameWorkspacePreparer(new FixedCandidateRevalidator(firstCandidate)), root, firstCandidate);
            var secondCandidate = CreateValidWorkspaceCandidate(directory, "second", "2.0", [0x02]);
            var second = Prepare(new GameWorkspacePreparer(new FixedCandidateRevalidator(secondCandidate)), root, secondCandidate);
            TestHarness.Equal(WorkspacePreparationStatus.Built, second.Status);
            var state = ReadState(root);
            TestHarness.Equal(second.WorkspaceKey, state.Active);
            TestHarness.Equal(first.WorkspaceKey, state.Previous);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }),
    ("M4 failed candidate revalidation does not pollute active or previous state", () =>
    {
        var directory = CreateTestDirectory();
        try
        {
            var root = Path.Combine(directory, "workspace-root");
            var candidateA = CreateValidWorkspaceCandidate(directory, "active-a", "1.0", [0x01]);
            var activatedA = Prepare(new GameWorkspacePreparer(new FixedCandidateRevalidator(candidateA)), root, candidateA);
            TestHarness.Equal(WorkspacePreparationStatus.Built, activatedA.Status);
            var before = ReadState(root);
            TestHarness.Equal(activatedA.WorkspaceKey, before.Active);
            TestHarness.True(before.Previous is null);

            var candidateB = CreateValidWorkspaceCandidate(directory, "candidate-b", "2.0", [0x02]);
            var mismatched = CreateValidWorkspaceCandidate(directory, "changed-during-b", "1.0", [0x03]);
            var failedB = Prepare(new GameWorkspacePreparer(new FixedCandidateRevalidator(mismatched)), root, candidateB);
            TestHarness.Equal(WorkspacePreparationStatus.Failed, failedB.Status);
            TestHarness.True(HasWorkspaceCode(failedB, WorkspaceErrorCodes.SourceIdentityMismatch));
            TestHarness.True(failedB.WorkspaceKey != activatedA.WorkspaceKey);
            TestHarness.True(failedB.WorkspacePath is not null && Directory.Exists(failedB.WorkspacePath));

            var after = ReadState(root);
            TestHarness.Equal(activatedA.WorkspaceKey, after.Active);
            TestHarness.True(after.Previous is null);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }),
    ("M4 blocks an unknown signer before creating payload staging", () =>
    {
        var directory = CreateTestDirectory();
        try
        {
            var candidate = CreateValidWorkspaceCandidate(directory, "blocked", "1.0", [0x01], new SigningIdentity([digestA]));
            var root = Path.Combine(directory, "workspace-root");
            var result = Prepare(new GameWorkspacePreparer(new FixedCandidateRevalidator(candidate)), root, candidate);
            TestHarness.Equal(WorkspacePreparationStatus.Blocked, result.Status);
            TestHarness.True(HasWorkspaceCode(result, WorkspaceErrorCodes.CertificateBlocked));
            TestHarness.False(Directory.Exists(Path.Combine(root, "staging")));
            TestHarness.False(Directory.Exists(Path.Combine(root, "workspaces")));
            TestHarness.False(File.Exists(Path.Combine(root, "workspace-state.json")));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }),
    ("M4 rejects hostile Content paths and special archive entries", () =>
    {
        var cases = new (string Name, (string Name, byte[] Data, int? Attributes)[] Entries, string Code)[]
        {
            ("zip-slip", [("assets/Content/../evil.xnb", [1], null)], WorkspaceErrorCodes.UnsafeContentEntry),
            ("backslash", [("assets/Content/Data\\evil.xnb", [1], null)], WorkspaceErrorCodes.UnsafeContentEntry),
            ("absolute", [("assets/Content//evil.xnb", [1], null)], WorkspaceErrorCodes.UnsafeContentEntry),
            ("drive", [("assets/Content/C:/evil.xnb", [1], null)], WorkspaceErrorCodes.UnsafeContentEntry),
            ("case-duplicate", [("assets/Content/Data/Foo.xnb", [1], null), ("assets/Content/data/foo.xnb", [2], null)], WorkspaceErrorCodes.DuplicateOutput),
            ("unicode-duplicate", [("assets/Content/Data/é.xnb", [1], null), ("assets/Content/Data/é.xnb", [2], null)], WorkspaceErrorCodes.DuplicateOutput),
            ("file-directory", [("assets/Content/Data", [1], null), ("assets/Content/Data/file.xnb", [2], null)], WorkspaceErrorCodes.DuplicateOutput),
            ("symlink", [("assets/Content/link.xnb", [1], unchecked((int)0xA1FF0000))], WorkspaceErrorCodes.UnsafeContentEntry),
        };

        foreach (var hostile in cases)
        {
            var directory = CreateTestDirectory();
            try
            {
                var candidate = CreateWorkspaceCandidate(directory, hostile.Name, "1.0", hostile.Entries, includeStardew: true, includeMonoGame: true);
                var result = Prepare(
                    new GameWorkspacePreparer(new FixedCandidateRevalidator(candidate)),
                    Path.Combine(directory, "workspace-root"),
                    candidate);
                TestHarness.Equal(WorkspacePreparationStatus.Failed, result.Status);
                TestHarness.True(HasWorkspaceCode(result, hostile.Code), hostile.Name);
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }),
    ("M4 enforces Content limits and duplicate outputs across APKs", () =>
    {
        var directory = CreateTestDirectory();
        try
        {
            var limited = CreateWorkspaceCandidate(
                directory,
                "limited",
                "1.0",
                [("assets/Content/a.xnb", [1], null), ("assets/Content/b.xnb", [2], null)],
                includeStardew: true,
                includeMonoGame: true);
            var options = new WorkspacePreparationOptions
            {
                Limits = new WorkspaceExtractionLimits { MaximumContentEntries = 1 },
            };
            var limitedResult = Prepare(
                new GameWorkspacePreparer(new FixedCandidateRevalidator(limited)),
                Path.Combine(directory, "limited-root"),
                limited,
                options);
            TestHarness.True(HasWorkspaceCode(limitedResult, WorkspaceErrorCodes.ContentLimitsExceeded));

            var duplicate = CreateWorkspaceCandidate(
                directory,
                "duplicate",
                "1.0",
                [("assets/Content/shared.xnb", [1], null)],
                includeStardew: true,
                includeMonoGame: true,
                assemblyContentEntries: [("assets/Content/SHARED.xnb", [2], null)]);
            var duplicateResult = Prepare(
                new GameWorkspacePreparer(new FixedCandidateRevalidator(duplicate)),
                Path.Combine(directory, "duplicate-root"),
                duplicate);
            TestHarness.True(HasWorkspaceCode(duplicateResult, WorkspaceErrorCodes.DuplicateOutput));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }),
    ("M4 rejects a highly compressed Content entry at the configured ratio limit", () =>
    {
        var directory = CreateTestDirectory();
        try
        {
            var compressedContent = new byte[128 * 1024];
            Array.Fill(compressedContent, (byte)0x41);
            var candidate = CreateWorkspaceCandidate(
                directory,
                "compression-ratio",
                "1.0",
                [("assets/Content/high-ratio.xnb", compressedContent, null)],
                includeStardew: true,
                includeMonoGame: true,
                contentCompressionLevel: CompressionLevel.Optimal);
            var options = new WorkspacePreparationOptions
            {
                Limits = new WorkspaceExtractionLimits
                {
                    CompressionRatioMinimumFileBytes = 1,
                    MaximumCompressionRatio = 2,
                },
            };
            var result = Prepare(
                new GameWorkspacePreparer(new FixedCandidateRevalidator(candidate)),
                Path.Combine(directory, "workspace-root"),
                candidate,
                options);
            TestHarness.Equal(WorkspacePreparationStatus.Failed, result.Status);
            TestHarness.True(HasWorkspaceCode(result, WorkspaceErrorCodes.ContentLimitsExceeded));
            TestHarness.False(File.Exists(Path.Combine(directory, "workspace-root", "workspace-state.json")));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }),
    ("M4 reports APK hash mismatch without exposing installed paths", () =>
    {
        var directory = CreateTestDirectory();
        try
        {
            var candidate = CreateValidWorkspaceCandidate(directory, "hash", "1.0", [1]);
            candidate = ReplaceSourceDigest(candidate, "base", digestA);
            var result = Prepare(
                new GameWorkspacePreparer(new FixedCandidateRevalidator(candidate)),
                Path.Combine(directory, "workspace-root"),
                candidate);
            TestHarness.True(HasWorkspaceCode(result, WorkspaceErrorCodes.SourceHashMismatch));
            TestHarness.False(result.Diagnostics.Any(diagnostic => candidate.Installation.ApkSources.Any(source =>
                diagnostic.Message.Contains(source.SourcePath, StringComparison.Ordinal) ||
                diagnostic.Detail?.Contains(source.SourcePath, StringComparison.Ordinal) == true)));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }),
    ("M4 extracts legacy v1 stores and rejects malformed legacy or missing required outputs", () =>
    {
        var directory = CreateTestDirectory();
        try
        {
            var signing = TrustedSigning();
            var legacyPath = WriteLegacyApk(
                directory,
                "legacy.apk",
                "MZ-stardew"u8.ToArray(),
                "MZ-monogame"u8.ToArray(),
                includeContent: true);
            var legacy = MakeCandidate("1.0", signing,
                new ApkFixture(legacyPath, "base", null, [ApkSourceRoleNames.GameContent, ApkSourceRoleNames.LegacyAssemblyBlob]));
            var legacyResult = Prepare(new GameWorkspacePreparer(new FixedCandidateRevalidator(legacy)), Path.Combine(directory, "legacy-root"), legacy);
            TestHarness.Equal(WorkspacePreparationStatus.Built, legacyResult.Status);
            TestHarness.True(File.Exists(Path.Combine(legacyResult.WorkspacePath!, "assemblies", "StardewValley.dll")));
            TestHarness.True(File.Exists(Path.Combine(legacyResult.WorkspacePath!, "assemblies", "MonoGame.Framework.dll")));

            var malformedPath = WriteBinaryApk(directory, "legacy-malformed.apk", [
                ("assets/Content/game.xnb", new byte[] { 1 }, (int?)null),
                ("assemblies/game.blob", new byte[] { 2 }, (int?)null),
            ]);
            var malformed = MakeCandidate("1.0", signing,
                new ApkFixture(malformedPath, "base", null, [ApkSourceRoleNames.GameContent, ApkSourceRoleNames.LegacyAssemblyBlob]));
            var malformedResult = Prepare(
                new GameWorkspacePreparer(new FixedCandidateRevalidator(malformed)),
                Path.Combine(directory, "legacy-malformed-root"),
                malformed);
            TestHarness.True(HasWorkspaceCode(malformedResult, WorkspaceErrorCodes.UnsupportedAssemblyStore));

            var missingAssembly = CreateWorkspaceCandidate(
                directory,
                "missing-assembly",
                "1.0",
                [("assets/Content/game.xnb", [1], null)],
                includeStardew: true,
                includeMonoGame: false);
            var assemblyResult = Prepare(new GameWorkspacePreparer(new FixedCandidateRevalidator(missingAssembly)), Path.Combine(directory, "assembly-root"), missingAssembly);
            TestHarness.True(HasWorkspaceCode(assemblyResult, WorkspaceErrorCodes.RequiredOutputMissing));

            var missingContent = CreateWorkspaceCandidate(
                directory,
                "missing-content",
                "1.0",
                [],
                includeStardew: true,
                includeMonoGame: true);
            var contentResult = Prepare(new GameWorkspacePreparer(new FixedCandidateRevalidator(missingContent)), Path.Combine(directory, "content-root"), missingContent);
            TestHarness.True(HasWorkspaceCode(contentResult, WorkspaceErrorCodes.RequiredOutputMissing));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }),
    ("M4 refuses activation when revalidation identity changes", () =>
    {
        var directory = CreateTestDirectory();
        try
        {
            var candidate = CreateValidWorkspaceCandidate(directory, "original", "1.0", [1]);
            var changed = CreateValidWorkspaceCandidate(directory, "changed", "2.0", [2]);
            var root = Path.Combine(directory, "workspace-root");
            var result = Prepare(new GameWorkspacePreparer(new FixedCandidateRevalidator(changed)), root, candidate);
            TestHarness.Equal(WorkspacePreparationStatus.Failed, result.Status);
            TestHarness.True(HasWorkspaceCode(result, WorkspaceErrorCodes.SourceIdentityMismatch));
            TestHarness.False(File.Exists(Path.Combine(root, "workspace-state.json")));
            TestHarness.True(result.WorkspacePath is not null && Directory.Exists(result.WorkspacePath));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }),
    ("M4 corrupt workspace state fails activation without overwriting it", () =>
    {
        var directory = CreateTestDirectory();
        try
        {
            var candidate = CreateValidWorkspaceCandidate(directory, "corrupt-state", "1.0", [1]);
            var root = Path.Combine(directory, "workspace-root");
            var preparer = new GameWorkspacePreparer(new FixedCandidateRevalidator(candidate));
            var first = Prepare(preparer, root, candidate);
            TestHarness.Equal(WorkspacePreparationStatus.Built, first.Status);

            var statePath = Path.Combine(root, "workspace-state.json");
            var corruptBytes = "{\"format\":\"wrong\",\"activeKey\":\"uncertain\"}"u8.ToArray();
            File.WriteAllBytes(statePath, corruptBytes);
            var second = Prepare(preparer, root, candidate);
            TestHarness.Equal(WorkspacePreparationStatus.Failed, second.Status);
            TestHarness.True(HasWorkspaceCode(second, WorkspaceErrorCodes.ActivationFailed));
            TestHarness.True(File.ReadAllBytes(statePath).SequenceEqual(corruptBytes));
            TestHarness.True(second.Metrics is null);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }),
    ("M4 cancellation cleans owned staging and stale staging only", () =>
    {
        var directory = CreateTestDirectory();
        try
        {
            var candidate = CreateValidWorkspaceCandidate(directory, "cancel", "1.0", [1]);
            var root = Path.Combine(directory, "workspace-root");
            var staging = Path.Combine(root, "staging");
            Directory.CreateDirectory(staging);
            var stale = Path.Combine(staging, $"{new string('a', 64)}-{new string('b', 32)}");
            var unrelated = Path.Combine(staging, "keep-me");
            Directory.CreateDirectory(stale);
            Directory.CreateDirectory(unrelated);
            using var cancellation = new CancellationTokenSource();
            var options = new WorkspacePreparationOptions
            {
                Progress = new CallbackProgress(progress =>
                {
                    if (progress.Stage == WorkspaceProgressStage.ScanningContent)
                    {
                        cancellation.Cancel();
                    }
                }),
            };
            var result = Prepare(
                new GameWorkspacePreparer(new FixedCandidateRevalidator(candidate)),
                root,
                candidate,
                options,
                cancellation.Token);
            TestHarness.Equal(WorkspacePreparationStatus.Cancelled, result.Status);
            TestHarness.False(Directory.Exists(stale));
            TestHarness.True(Directory.Exists(unrelated));
            TestHarness.False(Directory.EnumerateDirectories(staging).Any(path => Path.GetFileName(path).Length == 97));
            TestHarness.False(File.Exists(Path.Combine(root, "workspace-state.json")));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }),
    ("M4 strictly validates the rewrite manifest before cache activation", () =>
    {
        var directory = CreateTestDirectory();
        try
        {
            var candidate = CreateValidWorkspaceCandidate(directory, "rewrite-strict", "1.0", [1, 2, 3]);
            var root = Path.Combine(directory, "workspace-root");
            var preparer = new GameWorkspacePreparer(new FixedCandidateRevalidator(candidate));
            var first = Prepare(preparer, root, candidate);
            TestHarness.Equal(WorkspacePreparationStatus.Built, first.Status);

            var rewritePath = Path.Combine(first.WorkspacePath!, "rewrite-manifest.json");
            using (var original = JsonDocument.Parse(File.ReadAllBytes(rewritePath)))
            {
                var fields = original.RootElement.EnumerateObject()
                    .Select(static property => property.Name)
                    .ToHashSet(StringComparer.Ordinal);
                TestHarness.True(fields.SetEquals(["format", "schema", "cacheKey", "recipe", "status"]));
            }
            File.WriteAllText(rewritePath, $$"""
                {
                  "format": "junimogate-rewrite-manifest",
                  "schema": "junimogate-workspace-manifest:v1",
                  "cacheKey": "{{first.WorkspaceKey}}",
                  "recipe": "unrewritten:v1",
                  "status": "not-applied",
                  "unexpected": true
                }
                """);

            var second = Prepare(preparer, root, candidate);
            TestHarness.Equal(WorkspacePreparationStatus.Built, second.Status);
            TestHarness.True(HasWorkspaceCode(second, WorkspaceErrorCodes.CacheCorrupt));
            TestHarness.Equal(1, Directory.EnumerateDirectories(Path.Combine(root, "quarantine")).Count());
            using var rebuilt = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(second.WorkspacePath!, "rewrite-manifest.json")));
            TestHarness.False(rebuilt.RootElement.TryGetProperty("unexpected", out _));
            TestHarness.Equal("not-applied", rebuilt.RootElement.GetProperty("status").GetString());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }),
    ("M4 quarantines a corrupt cache and rebuilds it", () =>
    {
        var directory = CreateTestDirectory();
        try
        {
            var candidate = CreateValidWorkspaceCandidate(directory, "corrupt", "1.0", [1, 2, 3]);
            var root = Path.Combine(directory, "workspace-root");
            var preparer = new GameWorkspacePreparer(new FixedCandidateRevalidator(candidate));
            var first = Prepare(preparer, root, candidate);
            File.WriteAllBytes(Path.Combine(first.WorkspacePath!, "Content", "Data", "game.xnb"), [9, 9, 9]);
            var second = Prepare(preparer, root, candidate);
            TestHarness.Equal(WorkspacePreparationStatus.Built, second.Status);
            TestHarness.True(HasWorkspaceCode(second, WorkspaceErrorCodes.CacheCorrupt));
            TestHarness.Equal(Hash([1, 2, 3]), Hash(File.ReadAllBytes(Path.Combine(second.WorkspacePath!, "Content", "Data", "game.xnb"))));
            TestHarness.Equal(1, Directory.EnumerateDirectories(Path.Combine(root, "quarantine")).Count());
            var state = ReadState(root);
            TestHarness.Equal(second.WorkspaceKey, state.Active);
            TestHarness.True(state.Previous is null);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }),
    ("M5 Gate 1 validates the active workspace into an immutable execution plan", () =>
    {
        var directory = CreateTestDirectory();
        try
        {
            var candidate = CreateValidWorkspaceCandidate(directory, "gate1-success", "1.0", [1, 2, 3]);
            var root = Path.Combine(directory, "workspace-root");
            var prepared = Prepare(new GameWorkspacePreparer(new FixedCandidateRevalidator(candidate)), root, candidate);
            TestHarness.Equal(WorkspacePreparationStatus.Built, prepared.Status);
            var revalidator = new SequenceCandidateRevalidator(candidate);
            var result = ValidateExecution(new WorkspaceExecutionValidator(revalidator), root, candidate);

            TestHarness.Equal(WorkspaceExecutionValidationStatus.Validated, result.Status);
            TestHarness.True(result.Plan is not null);
            TestHarness.Equal(prepared.WorkspaceKey, result.Plan!.WorkspaceKey);
            TestHarness.Equal(prepared.WorkspacePath, result.Plan.WorkspacePath);
            TestHarness.Equal(candidate.Installation.PackageName, result.Plan.PackageName);
            TestHarness.Equal(candidate.Installation.VersionName, result.Plan.VersionName);
            TestHarness.Equal(candidate.Installation.LongVersionCode, result.Plan.LongVersionCode);
            TestHarness.Equal(candidate.Installation.SelectedAbi, result.Plan.SelectedAbi);
            TestHarness.True(Sha256Digest.TryParse(result.Plan.IdentityDigest, out _));
            TestHarness.True(result.Plan.ValidatedAtUtc <= DateTimeOffset.UtcNow);
            TestHarness.Equal(3, result.Plan.Payloads.Count);
            TestHarness.Equal(1, revalidator.CallCount);
            TestHarness.False(JsonSerializer.Serialize(result.Plan).Contains(prepared.WorkspacePath!, StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }),
    ("M5 Gate 1 rejects an unknown certificate before workspace or live revalidation access", () =>
    {
        var directory = CreateTestDirectory();
        try
        {
            var candidate = CreateValidWorkspaceCandidate(
                directory,
                "gate1-cert",
                "1.0",
                [1],
                new SigningIdentity([digestA]));
            var root = Path.Combine(directory, "workspace-root");
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "workspace-state.json"), "not-json");
            var revalidator = new SequenceCandidateRevalidator(candidate);
            var result = ValidateExecution(new WorkspaceExecutionValidator(revalidator), root, candidate);

            TestHarness.Equal(WorkspaceExecutionValidationStatus.Rejected, result.Status);
            TestHarness.True(HasTrustCode(result, WorkspaceExecutionTrustErrorCodes.CertificateBlocked));
            TestHarness.Equal(0, revalidator.CallCount);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }),
    ("M5 Gate 1 rejects missing and corrupt active workspace state", () =>
    {
        var directory = CreateTestDirectory();
        try
        {
            var candidate = CreateValidWorkspaceCandidate(directory, "gate1-state", "1.0", [1]);
            var missingRoot = Path.Combine(directory, "missing-root");
            Directory.CreateDirectory(missingRoot);
            var missing = ValidateExecution(
                new WorkspaceExecutionValidator(new SequenceCandidateRevalidator(candidate)),
                missingRoot,
                candidate);
            TestHarness.True(HasTrustCode(missing, WorkspaceExecutionTrustErrorCodes.StateMissing));

            var corruptRoot = Path.Combine(directory, "corrupt-root");
            Directory.CreateDirectory(corruptRoot);
            File.WriteAllText(Path.Combine(corruptRoot, "workspace-state.json"), "{\"format\":");
            var corrupt = ValidateExecution(
                new WorkspaceExecutionValidator(new SequenceCandidateRevalidator(candidate)),
                corruptRoot,
                candidate);
            TestHarness.True(HasTrustCode(corrupt, WorkspaceExecutionTrustErrorCodes.StateInvalid));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }),
    ("M5 Gate 1 never accepts a traversal-shaped active key", () =>
    {
        var directory = CreateTestDirectory();
        try
        {
            var candidate = CreateValidWorkspaceCandidate(directory, "gate1-key", "1.0", [1]);
            var root = Path.Combine(directory, "workspace-root");
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "workspace-state.json"), """
                {
                  "format": "junimogate-workspace-state",
                  "schema": "v1",
                  "activeKey": "../outside",
                  "previousKey": null
                }
                """);
            var result = ValidateExecution(
                new WorkspaceExecutionValidator(new SequenceCandidateRevalidator(candidate)),
                root,
                candidate);
            TestHarness.True(HasTrustCode(result, WorkspaceExecutionTrustErrorCodes.ActiveKeyInvalid));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }),
    ("M5 Gate 1 rejects bad manifest JSON schema recipe and status", () =>
    {
        var cases = new (string Suffix, string FileName, string OldText, string NewText, string Code)[]
        {
            ("json", "source-manifest.json", "{", "not-json{", WorkspaceExecutionTrustErrorCodes.ManifestInvalid),
            ("schema", "extraction-manifest.json", WorkspacePreparationOptions.DefaultManifestSchema, "junimogate-workspace-manifest:v2", WorkspaceExecutionTrustErrorCodes.ManifestSchemaMismatch),
            ("recipe", "rewrite-manifest.json", WorkspaceExecutionTrustDefaults.Gate0RewriteRecipe, "unexpected-recipe:v1", WorkspaceExecutionTrustErrorCodes.RewriteRecipeMismatch),
            ("status", "rewrite-manifest.json", WorkspaceExecutionTrustDefaults.Gate0RewriteStatus, "applied", WorkspaceExecutionTrustErrorCodes.RewriteStatusMismatch),
        };

        foreach (var testCase in cases)
        {
            var directory = CreateTestDirectory();
            try
            {
                var candidate = CreateValidWorkspaceCandidate(directory, $"gate1-{testCase.Suffix}", "1.0", [1]);
                var root = Path.Combine(directory, "workspace-root");
                var prepared = Prepare(new GameWorkspacePreparer(new FixedCandidateRevalidator(candidate)), root, candidate);
                var path = Path.Combine(prepared.WorkspacePath!, testCase.FileName);
                var text = File.ReadAllText(path);
                TestHarness.True(text.Contains(testCase.OldText, StringComparison.Ordinal), testCase.Suffix);
                File.WriteAllText(path, text.Replace(testCase.OldText, testCase.NewText, StringComparison.Ordinal));

                var result = ValidateExecution(
                    new WorkspaceExecutionValidator(new SequenceCandidateRevalidator(candidate)),
                    root,
                    candidate);
                TestHarness.True(HasTrustCode(result, testCase.Code), testCase.Suffix);
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }),
    ("M5 Gate 1 enforces the exact payload file set excluding manifests", () =>
    {
        var directory = CreateTestDirectory();
        try
        {
            var candidate = CreateValidWorkspaceCandidate(directory, "gate1-set", "1.0", [1]);
            var root = Path.Combine(directory, "workspace-root");
            var prepared = Prepare(new GameWorkspacePreparer(new FixedCandidateRevalidator(candidate)), root, candidate);
            File.WriteAllBytes(Path.Combine(prepared.WorkspacePath!, "assemblies", "unexpected.dll"), "MZ-extra"u8.ToArray());

            var result = ValidateExecution(
                new WorkspaceExecutionValidator(new SequenceCandidateRevalidator(candidate)),
                root,
                candidate);
            TestHarness.True(HasTrustCode(result, WorkspaceExecutionTrustErrorCodes.FileSetMismatch));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }),
    ("M5 Gate 1 re-hashes every payload and rejects a same-size mutation", () =>
    {
        var directory = CreateTestDirectory();
        try
        {
            var candidate = CreateValidWorkspaceCandidate(directory, "gate1-hash", "1.0", [1, 2, 3]);
            var root = Path.Combine(directory, "workspace-root");
            var prepared = Prepare(new GameWorkspacePreparer(new FixedCandidateRevalidator(candidate)), root, candidate);
            File.WriteAllBytes(Path.Combine(prepared.WorkspacePath!, "Content", "Data", "game.xnb"), [3, 2, 1]);

            var result = ValidateExecution(
                new WorkspaceExecutionValidator(new SequenceCandidateRevalidator(candidate)),
                root,
                candidate);
            TestHarness.True(HasTrustCode(result, WorkspaceExecutionTrustErrorCodes.PayloadHashMismatch));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }),
    ("M5 Gate 1 rejects a live package identity race after file validation", () =>
    {
        var directory = CreateTestDirectory();
        try
        {
            var candidate = CreateValidWorkspaceCandidate(directory, "gate1-race", "1.0", [1]);
            var changed = CreateValidWorkspaceCandidate(directory, "gate1-race-changed", "2.0", [2]);
            var root = Path.Combine(directory, "workspace-root");
            Prepare(new GameWorkspacePreparer(new FixedCandidateRevalidator(candidate)), root, candidate);
            var revalidator = new SequenceCandidateRevalidator(changed);

            var result = ValidateExecution(new WorkspaceExecutionValidator(revalidator), root, candidate);
            TestHarness.True(HasTrustCode(result, WorkspaceExecutionTrustErrorCodes.LiveRevalidationFailed));
            TestHarness.Equal(1, revalidator.CallCount);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }),
    ("M5 Gate 1 cancellation is stable and never produces an execution plan", () =>
    {
        var directory = CreateTestDirectory();
        try
        {
            var candidate = CreateValidWorkspaceCandidate(directory, "gate1-cancel", "1.0", [1]);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            var result = ValidateExecution(
                new WorkspaceExecutionValidator(new SequenceCandidateRevalidator(candidate)),
                Path.Combine(directory, "workspace-root"),
                candidate,
                cancellation.Token);
            TestHarness.Equal(WorkspaceExecutionValidationStatus.Cancelled, result.Status);
            TestHarness.True(result.Plan is null);
            TestHarness.True(HasTrustCode(result, WorkspaceExecutionTrustErrorCodes.Cancelled));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }),
    ("M5 native probe inventories bounded ARM64 ELF metadata without extraction", () =>
    {
        var directory = CreateTestDirectory();
        try
        {
            var gameElf = BuildNativeElf(flags: 0x11223344, payloadSize: 192);
            var candidate = CreateWorkspaceCandidate(
                directory,
                "native-success",
                "1.0",
                [("assets/Content/Data/game.xnb", new byte[] { 1, 2, 3 }, null)],
                includeStardew: true,
                includeMonoGame: true,
                assemblyContentEntries:
                [
                    ("lib/arm64-v8a/libgame.so", gameElf, null),
                    ("lib/x86_64/libignored.so", "not-an-arm64-elf"u8.ToArray(), null),
                ]);
            var root = Path.Combine(directory, "workspace-root");
            Prepare(new GameWorkspacePreparer(new FixedCandidateRevalidator(candidate)), root, candidate);
            var trust = ValidateExecution(new WorkspaceExecutionValidator(new SequenceCandidateRevalidator(candidate)), root, candidate);
            TestHarness.True(trust.Plan is not null);

            var result = ProbeNative(trust.Plan!, candidate);
            TestHarness.Equal(NativeEntryInventoryStatus.Succeeded, result.Status);
            TestHarness.Equal("arm64-v8a", result.SelectedAbi);
            TestHarness.Equal(3, result.Entries.Count);
            TestHarness.Equal(2, result.Entries.Count(entry =>
                entry.EntryPath == "lib/arm64-v8a/libassemblies.arm64-v8a.blob.so"));
            var game = result.Entries.Single(entry => entry.EntryPath == "lib/arm64-v8a/libgame.so");
            TestHarness.Equal((long)gameElf.Length, game.Size);
            TestHarness.Equal(Hash(gameElf), game.Sha256);
            TestHarness.Equal(2, game.Elf.ElfClass);
            TestHarness.Equal(1, game.Elf.DataEncoding);
            TestHarness.Equal(3, game.Elf.ObjectType);
            TestHarness.Equal(183, game.Elf.Machine);
            TestHarness.Equal(0x11223344U, game.Elf.Flags);
            TestHarness.True(result.Entries.SequenceEqual(result.Entries
                .OrderBy(static entry => entry.EntryPath, StringComparer.Ordinal)
                .ThenBy(static entry => entry.Sha256, StringComparer.Ordinal)
                .ThenBy(static entry => entry.Size)
                .ThenBy(static entry => entry.SourceLabel, StringComparer.Ordinal)));
            TestHarness.False(Directory.EnumerateFiles(root, "*.so", SearchOption.AllDirectories).Any());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }),
    ("M5 native probe blocks an unknown certificate before APK file access", () =>
    {
        var directory = CreateTestDirectory();
        try
        {
            var candidate = CreateValidWorkspaceCandidate(directory, "native-cert", "1.0", [1]);
            var root = Path.Combine(directory, "workspace-root");
            Prepare(new GameWorkspacePreparer(new FixedCandidateRevalidator(candidate)), root, candidate);
            var trust = ValidateExecution(new WorkspaceExecutionValidator(new SequenceCandidateRevalidator(candidate)), root, candidate);
            TestHarness.True(trust.Plan is not null);
            var untrusted = ReplaceSigning(candidate, new SigningIdentity([digestA]));
            foreach (var source in candidate.Installation.ApkSources)
            {
                File.Delete(source.SourcePath);
            }

            var result = ProbeNative(trust.Plan!, untrusted);
            TestHarness.Equal(NativeEntryInventoryStatus.Failed, result.Status);
            TestHarness.True(HasNativeCode(result, NativeEntryInventoryErrorCodes.CertificateBlocked));
            TestHarness.False(HasNativeCode(result, NativeEntryInventoryErrorCodes.InvalidArchive));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }),
    ("M5 native probe binds the execution plan to the complete live identity", () =>
    {
        var directory = CreateTestDirectory();
        try
        {
            var candidate = CreateValidWorkspaceCandidate(directory, "native-plan", "1.0", [1]);
            var root = Path.Combine(directory, "workspace-root");
            Prepare(new GameWorkspacePreparer(new FixedCandidateRevalidator(candidate)), root, candidate);
            var trust = ValidateExecution(new WorkspaceExecutionValidator(new SequenceCandidateRevalidator(candidate)), root, candidate);
            TestHarness.True(trust.Plan is not null);
            var changedVersionName = ReplaceVersionName(candidate, "1.0-tampered");

            var result = ProbeNative(trust.Plan!, changedVersionName);
            TestHarness.Equal(NativeEntryInventoryStatus.Failed, result.Status);
            TestHarness.True(HasNativeCode(result, NativeEntryInventoryErrorCodes.TrustPlanMismatch));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }),
    ("M5 native probe re-hashes each APK and rejects a same-size mutation", () =>
    {
        var directory = CreateTestDirectory();
        try
        {
            var candidate = CreateValidWorkspaceCandidate(directory, "native-source", "1.0", [1]);
            var root = Path.Combine(directory, "workspace-root");
            Prepare(new GameWorkspacePreparer(new FixedCandidateRevalidator(candidate)), root, candidate);
            var trust = ValidateExecution(new WorkspaceExecutionValidator(new SequenceCandidateRevalidator(candidate)), root, candidate);
            TestHarness.True(trust.Plan is not null);
            var sourcePath = candidate.Installation.ApkSources[0].SourcePath;
            var bytes = File.ReadAllBytes(sourcePath);
            bytes[^1] ^= 0x01;
            File.WriteAllBytes(sourcePath, bytes);

            var result = ProbeNative(trust.Plan!, candidate);
            TestHarness.Equal(NativeEntryInventoryStatus.Failed, result.Status);
            TestHarness.True(HasNativeCode(result, NativeEntryInventoryErrorCodes.SourceIdentityMismatch));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }),
    ("M5 native probe rejects unsafe duplicate and malformed selected-ABI entries", () =>
    {
        var cases = new (string Suffix, (string Name, byte[] Data, int? Attributes)[] Entries, string Code)[]
        {
            ("unsafe", new (string, byte[], int?)[]
            {
                ("lib/arm64-v8a/nested/libbad.so", BuildNativeElf(), null),
            }, NativeEntryInventoryErrorCodes.UnsafeEntry),
            ("case", new (string, byte[], int?)[]
            {
                ("LIB/arm64-v8a/libbad.so", BuildNativeElf(), null),
            }, NativeEntryInventoryErrorCodes.UnsafeEntry),
            ("duplicate", new (string, byte[], int?)[]
            {
                ("lib/arm64-v8a/libduplicate.so", BuildNativeElf(flags: 1), null),
                ("lib/arm64-v8a/libduplicate.so", BuildNativeElf(flags: 2), null),
            }, NativeEntryInventoryErrorCodes.DuplicateEntry),
            ("invalid", new (string, byte[], int?)[]
            {
                ("lib/arm64-v8a/libbad.so", "not-elf"u8.ToArray(), null),
            }, NativeEntryInventoryErrorCodes.InvalidElf),
            ("symlink", new (string, byte[], int?)[]
            {
                ("lib/arm64-v8a/liblink.so", BuildNativeElf(), unchecked((int)0xA1FF0000U)),
            }, NativeEntryInventoryErrorCodes.UnsafeEntry),
        };

        foreach (var testCase in cases)
        {
            var directory = CreateTestDirectory();
            try
            {
                var candidate = CreateWorkspaceCandidate(
                    directory,
                    $"native-{testCase.Suffix}",
                    "1.0",
                    [("assets/Content/Data/game.xnb", new byte[] { 1 }, null)],
                    includeStardew: true,
                    includeMonoGame: true,
                    assemblyContentEntries: testCase.Entries);
                var root = Path.Combine(directory, "workspace-root");
                var prepared = Prepare(new GameWorkspacePreparer(new FixedCandidateRevalidator(candidate)), root, candidate);
                TestHarness.True(prepared.Status == WorkspacePreparationStatus.Built, testCase.Suffix);
                var trust = ValidateExecution(new WorkspaceExecutionValidator(new SequenceCandidateRevalidator(candidate)), root, candidate);
                TestHarness.True(trust.Plan is not null, testCase.Suffix);

                var result = ProbeNative(trust.Plan!, candidate);
                TestHarness.True(result.Status == NativeEntryInventoryStatus.Failed, testCase.Suffix);
                TestHarness.True(HasNativeCode(result, testCase.Code), testCase.Suffix);
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }),
    ("M5 native probe enforces entry bounds and stable cancellation", () =>
    {
        var directory = CreateTestDirectory();
        try
        {
            var candidate = CreateValidWorkspaceCandidate(directory, "native-bounds", "1.0", [1]);
            var root = Path.Combine(directory, "workspace-root");
            Prepare(new GameWorkspacePreparer(new FixedCandidateRevalidator(candidate)), root, candidate);
            var trust = ValidateExecution(new WorkspaceExecutionValidator(new SequenceCandidateRevalidator(candidate)), root, candidate);
            TestHarness.True(trust.Plan is not null);

            var bounded = ProbeNative(
                trust.Plan!,
                candidate,
                new NativeEntryInventoryLimits { MaximumSelectedEntries = 1 });
            TestHarness.Equal(NativeEntryInventoryStatus.Failed, bounded.Status);
            TestHarness.True(HasNativeCode(bounded, NativeEntryInventoryErrorCodes.LimitsExceeded));

            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            var cancelled = ProbeNative(trust.Plan!, candidate, cancellationToken: cancellation.Token);
            TestHarness.Equal(NativeEntryInventoryStatus.Cancelled, cancelled.Status);
            TestHarness.True(HasNativeCode(cancelled, NativeEntryInventoryErrorCodes.Cancelled));
            TestHarness.Equal(0, cancelled.Entries.Count);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }));

static string WriteLegacyApk(
    string directory,
    string fileName,
    byte[] stardewImage,
    byte[] monoGameImage,
    bool includeContent,
    uint monoGameStoreIndex = 0)
{
    var entries = new List<(string Name, byte[] Data, int? Attributes)>
    {
        (LegacyAssemblyStoreApkPath.BaseStorePath, BuildLegacyStore(0, [stardewImage]), null),
        (LegacyAssemblyStoreApkPath.GetAbiStorePath("arm64-v8a"), BuildLegacyStore(1, [BuildXalz(monoGameImage, 1)]), null),
        (LegacyAssemblyStoreApkPath.ManifestPath, Encoding.UTF8.GetBytes(
            "Hash 32 Hash 64 Blob ID Blob idx Name\n" +
            "0x00000001 0x0000000000000001 000 0000 StardewValley\n" +
            $"0x00000002 0x0000000000000002 001 {monoGameStoreIndex:D4} MonoGame.Framework\n"), null),
    };
    if (includeContent)
        entries.Add(("assets/Content/Data/game.xnb", [1], null));

    return WriteBinaryApk(directory, fileName, entries);
}

static byte[] BuildLegacyStore(uint storeId, IReadOnlyList<byte[]> images)
{
    var metadataLength = checked(20 + (images.Count * 24));
    var length = checked(metadataLength + images.Sum(static image => image.Length));
    var store = new byte[length];
    BinaryPrimitives.WriteUInt32LittleEndian(store, AssemblyStoreV2.Magic);
    BinaryPrimitives.WriteUInt32LittleEndian(store.AsSpan(4), 1);
    BinaryPrimitives.WriteUInt32LittleEndian(store.AsSpan(8), checked((uint)images.Count));
    BinaryPrimitives.WriteUInt32LittleEndian(store.AsSpan(12), 0);
    BinaryPrimitives.WriteUInt32LittleEndian(store.AsSpan(16), storeId);

    var dataOffset = metadataLength;
    for (var index = 0; index < images.Count; index++)
    {
        var descriptor = store.AsSpan(20 + (index * 24), 24);
        BinaryPrimitives.WriteUInt32LittleEndian(descriptor, checked((uint)dataOffset));
        BinaryPrimitives.WriteUInt32LittleEndian(descriptor[4..], checked((uint)images[index].Length));
        images[index].CopyTo(store.AsSpan(dataOffset));
        dataOffset = checked(dataOffset + images[index].Length);
    }

    return store;
}

static MemoryStream BuildElfStore(
    byte[] imageData,
    bool includePayloadSection = true,
    string assemblyName = "Synthetic.dll",
    uint? dataOffsetOverride = null,
    uint? dataSizeOverride = null,
    uint? descriptorIndexOverride = null)
{
    var nameBytes = Encoding.UTF8.GetBytes(assemblyName);
    var dataOffset = checked((uint)(20 + 12 + 28 + 4 + nameBytes.Length));
    var payloadLength = checked((int)dataOffset + imageData.Length);
    var payload = new byte[payloadLength];
    BinaryPrimitives.WriteUInt32LittleEndian(payload, AssemblyStoreV2.Magic);
    BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), AssemblyStoreV2.Arm64Version);
    BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(8), 1);
    BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(12), 1);
    BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(16), 12);

    BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(20), 0x1122334455667788);
    BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(28), descriptorIndexOverride ?? 0);

    var descriptor = payload.AsSpan(32, 28);
    BinaryPrimitives.WriteUInt32LittleEndian(descriptor, 0);
    BinaryPrimitives.WriteUInt32LittleEndian(descriptor[4..], dataOffsetOverride ?? dataOffset);
    BinaryPrimitives.WriteUInt32LittleEndian(descriptor[8..], dataSizeOverride ?? checked((uint)imageData.Length));
    BinaryPrimitives.WriteUInt32LittleEndian(descriptor[12..], 0);
    BinaryPrimitives.WriteUInt32LittleEndian(descriptor[16..], 0);
    BinaryPrimitives.WriteUInt32LittleEndian(descriptor[20..], 0);
    BinaryPrimitives.WriteUInt32LittleEndian(descriptor[24..], 0);

    BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(60), checked((uint)nameBytes.Length));
    nameBytes.CopyTo(payload.AsSpan(64));
    imageData.CopyTo(payload.AsSpan((int)dataOffset));

    var sectionNames = includePayloadSection
        ? "\0payload\0.shstrtab\0"u8.ToArray()
        : "\0missing\0.shstrtab\0"u8.ToArray();
    const int payloadOffset = 0x100;
    var stringTableOffset = Align(payloadOffset + payload.Length, 8);
    var sectionHeaderOffset = Align(stringTableOffset + sectionNames.Length, 8);
    var fileLength = sectionHeaderOffset + (3 * 64);
    var elf = new byte[fileLength];

    elf[0] = 0x7F;
    elf[1] = (byte)'E';
    elf[2] = (byte)'L';
    elf[3] = (byte)'F';
    elf[4] = 2;
    elf[5] = 1;
    elf[6] = 1;
    BinaryPrimitives.WriteUInt16LittleEndian(elf.AsSpan(16), 3);
    BinaryPrimitives.WriteUInt16LittleEndian(elf.AsSpan(18), 183);
    BinaryPrimitives.WriteUInt32LittleEndian(elf.AsSpan(20), 1);
    BinaryPrimitives.WriteUInt64LittleEndian(elf.AsSpan(40), checked((ulong)sectionHeaderOffset));
    BinaryPrimitives.WriteUInt16LittleEndian(elf.AsSpan(52), 64);
    BinaryPrimitives.WriteUInt16LittleEndian(elf.AsSpan(58), 64);
    BinaryPrimitives.WriteUInt16LittleEndian(elf.AsSpan(60), 3);
    BinaryPrimitives.WriteUInt16LittleEndian(elf.AsSpan(62), 2);

    payload.CopyTo(elf.AsSpan(payloadOffset));
    sectionNames.CopyTo(elf.AsSpan(stringTableOffset));

    var payloadSection = elf.AsSpan(sectionHeaderOffset + 64, 64);
    BinaryPrimitives.WriteUInt32LittleEndian(payloadSection, 1);
    BinaryPrimitives.WriteUInt32LittleEndian(payloadSection[4..], 1);
    BinaryPrimitives.WriteUInt64LittleEndian(payloadSection[24..], payloadOffset);
    BinaryPrimitives.WriteUInt64LittleEndian(payloadSection[32..], checked((ulong)payload.Length));

    var stringSection = elf.AsSpan(sectionHeaderOffset + 128, 64);
    BinaryPrimitives.WriteUInt32LittleEndian(stringSection, includePayloadSection ? 9U : 9U);
    BinaryPrimitives.WriteUInt32LittleEndian(stringSection[4..], 3);
    BinaryPrimitives.WriteUInt64LittleEndian(stringSection[24..], checked((ulong)stringTableOffset));
    BinaryPrimitives.WriteUInt64LittleEndian(stringSection[32..], checked((ulong)sectionNames.Length));

    return new MemoryStream(elf, writable: true);
}

static byte[] BuildXalz(byte[] image, uint descriptorIndex)
{
    var compressed = new byte[LZ4Codec.MaximumOutputSize(image.Length)];
    var compressedLength = LZ4Codec.Encode(image, 0, image.Length, compressed, 0, compressed.Length);
    if (compressedLength <= 0)
    {
        throw new InvalidOperationException("Synthetic LZ4 compression failed.");
    }

    var xalz = new byte[12 + compressedLength];
    BinaryPrimitives.WriteUInt32LittleEndian(xalz, 0x5A4C4158);
    BinaryPrimitives.WriteUInt32LittleEndian(xalz.AsSpan(4), descriptorIndex);
    BinaryPrimitives.WriteUInt32LittleEndian(xalz.AsSpan(8), checked((uint)image.Length));
    compressed.AsSpan(0, compressedLength).CopyTo(xalz.AsSpan(12));
    return xalz;
}

static byte[] BuildNativeElf(
    ushort objectType = 3,
    ushort machine = 183,
    uint elfVersion = 1,
    uint flags = 0,
    int payloadSize = 64)
{
    if (payloadSize < 64)
    {
        throw new ArgumentOutOfRangeException(nameof(payloadSize));
    }

    var elf = new byte[payloadSize];
    elf[0] = 0x7F;
    elf[1] = (byte)'E';
    elf[2] = (byte)'L';
    elf[3] = (byte)'F';
    elf[4] = 2;
    elf[5] = 1;
    elf[6] = 1;
    BinaryPrimitives.WriteUInt16LittleEndian(elf.AsSpan(16), objectType);
    BinaryPrimitives.WriteUInt16LittleEndian(elf.AsSpan(18), machine);
    BinaryPrimitives.WriteUInt32LittleEndian(elf.AsSpan(20), elfVersion);
    BinaryPrimitives.WriteUInt32LittleEndian(elf.AsSpan(48), flags);
    for (var index = 64; index < elf.Length; index++)
    {
        elf[index] = checked((byte)(index % 251));
    }

    return elf;
}

static NativeEntryInventoryResult ProbeNative(
    ValidatedExecutionPlan plan,
    GameInstallationCandidate candidate,
    NativeEntryInventoryLimits? limits = null,
    CancellationToken cancellationToken = default) =>
    new NativeEntryInventoryProbe()
        .ProbeAsync(plan, candidate, limits, cancellationToken)
        .AsTask().GetAwaiter().GetResult();

static bool HasNativeCode(NativeEntryInventoryResult result, string code) =>
    result.Diagnostics.Any(diagnostic => diagnostic.Code.Equals(code, StringComparison.Ordinal));

static int Align(int value, int alignment) => checked((value + alignment - 1) & ~(alignment - 1));

static string CreateTestDirectory()
{
    var directory = Path.Combine(Path.GetTempPath(), $"junimogate-discovery-test-{Guid.NewGuid():N}");
    Directory.CreateDirectory(directory);
    return directory;
}

static string WriteApk(string directory, string fileName, IEnumerable<string> entries)
{
    var path = Path.Combine(directory, fileName);
    using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
    using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false);
    foreach (var entryName in entries)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.NoCompression);
        using var target = entry.Open();
        target.WriteByte(0x42);
    }

    return path;
}

static PackageInstallationSnapshot Snapshot(
    string packageName,
    SigningIdentity signingIdentity,
    params PackageApkSourceSnapshot[] sources) =>
    new(packageName, "1.0", 1, signingIdentity, sources);

static PackageDiscoveryReport AnalyzeSingle(
    string packageName,
    PackageInstallationSnapshot first,
    PackageInstallationSnapshot second) =>
    new GameInstallationDiscoveryCoordinator(new QueueSnapshotProvider((packageName, [first, second])))
        .AnalyzeAsync([packageName])
        .AsTask().GetAwaiter().GetResult()
        .Packages[0];

static bool HasCode(PackageDiscoveryReport report, string code) =>
    report.Diagnostics.Any(diagnostic => diagnostic.Code.Equals(code, StringComparison.Ordinal));

static SigningIdentity TrustedSigning() =>
    new([Sha256Digest.Parse(KnownGameCertificate.PlayCertificateSha256)]);

static GameInstallationCandidate CreateValidWorkspaceCandidate(
    string directory,
    string suffix,
    string versionName,
    byte[] content,
    SigningIdentity? signing = null) =>
    CreateWorkspaceCandidate(
        directory,
        suffix,
        versionName,
        [("assets/Content/Data/game.xnb", content, null)],
        includeStardew: true,
        includeMonoGame: true,
        signing: signing);

static GameInstallationCandidate CreateWorkspaceCandidate(
    string directory,
    string suffix,
    string versionName,
    (string Name, byte[] Data, int? Attributes)[] contentEntries,
    bool includeStardew,
    bool includeMonoGame,
    (string Name, byte[] Data, int? Attributes)[]? assemblyContentEntries = null,
    SigningIdentity? signing = null,
    CompressionLevel contentCompressionLevel = CompressionLevel.NoCompression)
{
    var fixtures = new List<ApkFixture>();
    var basePath = WriteBinaryApk(directory, $"{suffix}-base.apk", contentEntries, contentCompressionLevel);
    fixtures.Add(new ApkFixture(
        basePath,
        "base",
        null,
        contentEntries.Length == 0 ? [] : [ApkSourceRoleNames.GameContent]));

    var splitNumber = 0;
    if (includeStardew)
    {
        splitNumber++;
        using var store = BuildElfStore("MZ-stardew"u8.ToArray(), assemblyName: "StardewValley.dll");
        var entries = new List<(string Name, byte[] Data, int? Attributes)>
        {
            ("lib/arm64-v8a/libassemblies.arm64-v8a.blob.so", store.ToArray(), null),
        };
        if (assemblyContentEntries is not null)
        {
            entries.AddRange(assemblyContentEntries);
        }

        var path = WriteBinaryApk(directory, $"{suffix}-stardew.apk", entries);
        fixtures.Add(new ApkFixture(
            path,
            $"split-{splitNumber}",
            $"{suffix}.stardew",
            assemblyContentEntries is { Length: > 0 }
                ? [ApkSourceRoleNames.GameContent, ApkSourceRoleNames.ModernAssemblyBlob]
                : [ApkSourceRoleNames.ModernAssemblyBlob]));
    }

    if (includeMonoGame)
    {
        splitNumber++;
        using var store = BuildElfStore("MZ-monogame"u8.ToArray(), assemblyName: "MonoGame.Framework.dll");
        var path = WriteBinaryApk(directory, $"{suffix}-monogame.apk", [
            ("lib/arm64-v8a/libassemblies.arm64-v8a.blob.so", store.ToArray(), (int?)null),
        ]);
        fixtures.Add(new ApkFixture(
            path,
            $"split-{splitNumber}",
            $"{suffix}.monogame",
            [ApkSourceRoleNames.ModernAssemblyBlob]));
    }

    return MakeCandidate(versionName, signing ?? TrustedSigning(), fixtures.ToArray());
}

static GameInstallationCandidate MakeCandidate(
    string versionName,
    SigningIdentity signing,
    params ApkFixture[] fixtures)
{
    var sources = fixtures.Select(fixture =>
    {
        var bytes = File.ReadAllBytes(fixture.Path);
        return new ApkSourceIdentity(
            fixture.Path,
            Sha256Digest.Parse(Hash(bytes)),
            bytes.LongLength,
            fixture.Label,
            fixture.SplitName);
    }).ToArray();
    var inventories = fixtures.Select(fixture => new ApkSourceInventory(
        fixture.Label,
        fixture.Roles,
        fixture.Roles.Contains(ApkSourceRoleNames.ModernAssemblyBlob, StringComparer.Ordinal) ? ["arm64-v8a"] : [],
        fixture.Roles.Contains(ApkSourceRoleNames.ModernAssemblyBlob, StringComparer.Ordinal) ? ["arm64-v8a"] : [])).ToArray();
    var installation = new GameInstallationIdentity(
        KnownGameCertificate.PlayPackageName,
        versionName,
        versionName == "2.0" ? 2 : 1,
        signing,
        "arm64-v8a",
        sources);
    return new GameInstallationCandidate(installation, inventories);
}

static GameInstallationCandidate ReplaceSigning(
    GameInstallationCandidate candidate,
    SigningIdentity signing)
{
    var original = candidate.Installation;
    return new GameInstallationCandidate(
        new GameInstallationIdentity(
            original.PackageName,
            original.VersionName,
            original.LongVersionCode,
            signing,
            original.SelectedAbi,
            original.ApkSources),
        candidate.SourceInventories);
}

static GameInstallationCandidate ReplaceVersionName(
    GameInstallationCandidate candidate,
    string versionName)
{
    var original = candidate.Installation;
    return new GameInstallationCandidate(
        new GameInstallationIdentity(
            original.PackageName,
            versionName,
            original.LongVersionCode,
            original.SigningIdentity,
            original.SelectedAbi,
            original.ApkSources),
        candidate.SourceInventories);
}

static GameInstallationCandidate ReplaceSourceDigest(
    GameInstallationCandidate candidate,
    string label,
    Sha256Digest digest)
{
    var original = candidate.Installation;
    var sources = original.ApkSources.Select(source => source.Label == label
        ? new ApkSourceIdentity(source.SourcePath, digest, source.Size, source.Label, source.SplitName)
        : source).ToArray();
    return new GameInstallationCandidate(
        new GameInstallationIdentity(
            original.PackageName,
            original.VersionName,
            original.LongVersionCode,
            original.SigningIdentity,
            original.SelectedAbi,
            sources),
        candidate.SourceInventories);
}

static string WriteBinaryApk(
    string directory,
    string fileName,
    IEnumerable<(string Name, byte[] Data, int? Attributes)> entries,
    CompressionLevel compressionLevel = CompressionLevel.NoCompression)
{
    var path = Path.Combine(directory, fileName);
    using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
    using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false);
    foreach (var item in entries)
    {
        var entry = archive.CreateEntry(item.Name, compressionLevel);
        if (item.Attributes is { } attributes)
        {
            entry.ExternalAttributes = attributes;
        }

        using var output = entry.Open();
        output.Write(item.Data);
    }

    return path;
}

static WorkspacePreparationResult Prepare(
    GameWorkspacePreparer preparer,
    string root,
    GameInstallationCandidate candidate,
    WorkspacePreparationOptions? options = null,
    CancellationToken cancellationToken = default) =>
    preparer.PrepareAsync(new WorkspacePreparationRequest(root, candidate, options), cancellationToken)
        .AsTask().GetAwaiter().GetResult();

static bool HasWorkspaceCode(WorkspacePreparationResult result, string code) =>
    result.Diagnostics.Any(diagnostic => diagnostic.Code.Equals(code, StringComparison.Ordinal));

static WorkspaceExecutionValidationResult ValidateExecution(
    WorkspaceExecutionValidator validator,
    string root,
    GameInstallationCandidate candidate,
    CancellationToken cancellationToken = default) =>
    validator.ValidateAsync(
            candidate,
            root,
            WorkspacePreparationOptions.DefaultExtractorSchema,
            WorkspacePreparationOptions.DefaultManifestSchema,
            WorkspaceExecutionTrustDefaults.Gate0RewriteRecipe,
            WorkspaceExecutionTrustDefaults.Gate0RewriteStatus,
            cancellationToken)
        .AsTask().GetAwaiter().GetResult();

static bool HasTrustCode(WorkspaceExecutionValidationResult result, string code) =>
    result.Diagnostics.Any(diagnostic => diagnostic.Code.Equals(code, StringComparison.Ordinal));

static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

static (string? Active, string? Previous) ReadState(string root)
{
    using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "workspace-state.json")));
    var rootElement = document.RootElement;
    var active = rootElement.GetProperty("activeKey").GetString();
    var previousElement = rootElement.GetProperty("previousKey");
    return (active, previousElement.ValueKind == JsonValueKind.Null ? null : previousElement.GetString());
}

sealed record ApkFixture(
    string Path,
    string Label,
    string? SplitName,
    string[] Roles);

sealed class FixedCandidateRevalidator : IWorkspaceCandidateRevalidator
{
    private readonly GameInstallationCandidate? candidate;

    public FixedCandidateRevalidator(GameInstallationCandidate? candidate)
    {
        this.candidate = candidate;
    }

    public ValueTask<GameInstallationCandidate?> RevalidateAsync(
        string packageName,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(candidate);
    }
}

sealed class SequenceCandidateRevalidator : IWorkspaceCandidateRevalidator
{
    private readonly Queue<GameInstallationCandidate?> candidates;

    public SequenceCandidateRevalidator(params GameInstallationCandidate?[] candidates)
    {
        this.candidates = new Queue<GameInstallationCandidate?>(candidates);
    }

    public int CallCount { get; private set; }

    public ValueTask<GameInstallationCandidate?> RevalidateAsync(
        string packageName,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CallCount++;
        return ValueTask.FromResult(candidates.Count == 0 ? null : candidates.Dequeue());
    }
}

sealed class CallbackProgress : IProgress<WorkspaceProgressEvent>
{
    private readonly Action<WorkspaceProgressEvent> callback;

    public CallbackProgress(Action<WorkspaceProgressEvent> callback)
    {
        this.callback = callback;
    }

    public void Report(WorkspaceProgressEvent value) => callback(value);
}

sealed class RecordingProgress : IProgress<WorkspaceProgressEvent>
{
    private readonly List<WorkspaceProgressStage> distinctStages = [];
    private readonly HashSet<WorkspaceProgressStage> seen = [];

    public IReadOnlyList<WorkspaceProgressStage> DistinctStages => distinctStages;

    public void Report(WorkspaceProgressEvent value)
    {
        if (seen.Add(value.Stage))
        {
            distinctStages.Add(value.Stage);
        }
    }
}

sealed class QueueSnapshotProvider : IPackageInstallationSnapshotProvider
{
    private readonly Dictionary<string, Queue<PackageInstallationSnapshot?>> snapshots;

    public QueueSnapshotProvider(params (string PackageName, PackageInstallationSnapshot?[] Snapshots)[] packages)
    {
        snapshots = packages.ToDictionary(
            static package => package.PackageName,
            static package => new Queue<PackageInstallationSnapshot?>(package.Snapshots),
            StringComparer.Ordinal);
    }

    public ValueTask<PackageInstallationSnapshot?> GetSnapshotAsync(
        string packageName,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!snapshots.TryGetValue(packageName, out var packageSnapshots) || packageSnapshots.Count == 0)
        {
            return ValueTask.FromResult<PackageInstallationSnapshot?>(null);
        }

        return ValueTask.FromResult(packageSnapshots.Dequeue());
    }
}
