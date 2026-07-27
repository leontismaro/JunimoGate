using JunimoGate.Core;
using JunimoGate.Tests;

var digest1 = Sha256Digest.Parse(new string('1', 64));
var digest2 = Sha256Digest.Parse(new string('2', 64));
var digest3 = Sha256Digest.Parse(new string('3', 64));

return TestHarness.Run(
    ("Sha256Digest accepts canonical lowercase hex", () =>
    {
        var text = new string('a', Sha256Digest.HexLength);
        var digest = Sha256Digest.Parse(text);
        TestHarness.Equal(text, digest.Value);
        TestHarness.True(Sha256Digest.TryParse(text, out _));
    }),
    ("Sha256Digest rejects wrong length", () =>
    {
        TestHarness.False(Sha256Digest.TryParse(new string('a', 63), out _));
        TestHarness.Throws<FormatException>(() => Sha256Digest.Parse(string.Empty));
    }),
    ("Sha256Digest rejects uppercase, non-hex, and default values", () =>
    {
        TestHarness.False(Sha256Digest.TryParse(new string('A', 64), out _));
        TestHarness.False(Sha256Digest.TryParse(new string('g', 64), out _));
        TestHarness.False(default(Sha256Digest).IsValid);
    }),
    ("Signing identity canonicalizes and deduplicates current signer set", () =>
    {
        var signing = new SigningIdentity([digest3, digest1, digest3, digest2]);
        TestHarness.True(signing.CurrentSignerDigests.SequenceEqual([digest1, digest2, digest3]));
        TestHarness.Equal(0, signing.RotationHistory.Count);
    }),
    ("Signing identity preserves valid single-signer rotation order", () =>
    {
        var signing = new SigningIdentity([digest3], [digest1, digest2, digest3]);
        TestHarness.True(signing.RotationHistory.SequenceEqual([digest1, digest2, digest3]));
        TestHarness.Throws<ArgumentException>(() => new SigningIdentity([digest3], [digest1, digest1, digest3]));
        TestHarness.Throws<ArgumentException>(() => new SigningIdentity([digest3], [digest1, digest2]));
    }),
    ("Signing identity rejects history for multiple current signers", () =>
    {
        TestHarness.Throws<ArgumentException>(() => new SigningIdentity([digest1, digest2], [digest1]));
        TestHarness.Throws<ArgumentException>(() => new SigningIdentity([default(Sha256Digest)]));
        TestHarness.Throws<ArgumentException>(() => new SigningIdentity([]));
    }),
    ("Known game certificate directly matches the tested Play identity", () =>
    {
        var knownDigest = Sha256Digest.Parse(KnownGameCertificate.PlayCertificateSha256);
        var verification = KnownGameCertificate.Verify(
            KnownGameCertificate.PlayPackageName,
            new SigningIdentity([knownDigest], [knownDigest]));
        TestHarness.Equal(GameCertificateStatus.KnownTested, verification.Status);
        TestHarness.True(verification.AllowsCodeExecution);
        TestHarness.Equal(knownDigest, verification.MatchedKnownCertificate!.Value);
    }),
    ("Known game certificate accepts only Android-verified single-signer rotation", () =>
    {
        var knownDigest = Sha256Digest.Parse(KnownGameCertificate.PlayCertificateSha256);
        var rotatedDigest = Sha256Digest.Parse(new string('a', 64));
        var verification = KnownGameCertificate.Verify(
            KnownGameCertificate.PlayPackageName,
            new SigningIdentity([rotatedDigest], [knownDigest, rotatedDigest]));
        TestHarness.Equal(GameCertificateStatus.KnownTestedAfterRotation, verification.Status);
        TestHarness.True(verification.AllowsCodeExecution);
        TestHarness.Equal(knownDigest, verification.MatchedKnownCertificate!.Value);
    }),
    ("Known game certificate rejects unrelated and multi-signer identities", () =>
    {
        var knownDigest = Sha256Digest.Parse(KnownGameCertificate.PlayCertificateSha256);
        var unrelatedDigest = Sha256Digest.Parse(new string('b', 64));
        var unrelated = KnownGameCertificate.Verify(
            KnownGameCertificate.PlayPackageName,
            new SigningIdentity([unrelatedDigest]));
        TestHarness.Equal(GameCertificateStatus.Unrecognized, unrelated.Status);
        TestHarness.False(unrelated.AllowsCodeExecution);
        TestHarness.True(unrelated.MatchedKnownCertificate is null);

        var multiple = KnownGameCertificate.Verify(
            KnownGameCertificate.PlayPackageName,
            new SigningIdentity([knownDigest, unrelatedDigest]));
        TestHarness.Equal(GameCertificateStatus.Unrecognized, multiple.Status);
        TestHarness.False(multiple.AllowsCodeExecution);

        var unconfigured = KnownGameCertificate.Verify(
            "com.chucklefish.stardewvalleysamsung",
            new SigningIdentity([knownDigest]));
        TestHarness.Equal(GameCertificateStatus.NotConfigured, unconfigured.Status);
        TestHarness.False(unconfigured.AllowsCodeExecution);
    }),
    ("Game installation creates a stable enriched source manifest", () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "junimogate-core-tests");
        var split = new ApkSourceIdentity(Path.Combine(root, "feature.apk"), digest2, 22, "split-2", "feature.z");
        var baseSource = new ApkSourceIdentity(Path.Combine(root, "base.apk"), digest1, 11, "base");
        var firstSplit = new ApkSourceIdentity(Path.Combine(root, "config.apk"), digest3, 33, "split-1", "config.arm64");
        var signing = new SigningIdentity([digest3], [digest1, digest3]);
        var installation = new GameInstallationIdentity(
            "com.chucklefish.stardewvalley",
            "1.6.0",
            10600,
            signing,
            "arm64-v8a",
            [split, baseSource, firstSplit]);

        TestHarness.Equal(10600L, installation.LongVersionCode);
        TestHarness.Equal("arm64-v8a", installation.SelectedAbi);
        TestHarness.True(ReferenceEquals(signing, installation.SigningIdentity));
        TestHarness.True(installation.ApkSources.Select(static source => source.Label).SequenceEqual(["base", "split-1", "split-2"]));
        TestHarness.Equal(11L, installation.ApkSources[0].Size);
        TestHarness.Equal("config.arm64", installation.ApkSources[1].SplitName);
    }),
    ("Candidate inventories canonicalize values and align to source labels", () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "junimogate-core-tests");
        var baseSource = new ApkSourceIdentity(Path.Combine(root, "base.apk"), digest1, 11, "base");
        var splitSource = new ApkSourceIdentity(Path.Combine(root, "split.apk"), digest2, 22, "split-1", "config.arm64");
        var installation = new GameInstallationIdentity(
            "com.chucklefish.stardewvalley",
            "1.6.0",
            10600,
            new SigningIdentity([digest1]),
            "arm64-v8a",
            [splitSource, baseSource]);
        var splitInventory = new ApkSourceInventory(
            "split-1",
            [ApkSourceRoleNames.ModernAssemblyBlob],
            ["ARM64-V8A", "arm64-v8a"],
            ["ARM64-V8A"]);
        var baseInventory = new ApkSourceInventory(
            "base",
            [ApkSourceRoleNames.GameContent],
            [],
            []);
        var candidate = new GameInstallationCandidate(installation, [splitInventory, baseInventory]);

        TestHarness.True(candidate.SourceInventories.Select(static inventory => inventory.SourceLabel).SequenceEqual(["base", "split-1"]));
        TestHarness.True(candidate.SourceInventories[1].NativeAbis.SequenceEqual(["arm64-v8a"]));
        TestHarness.Throws<ArgumentException>(() => new GameInstallationCandidate(installation, [baseInventory]));
        TestHarness.Throws<ArgumentException>(() => new ApkSourceInventory("base", [""], [], []));
    }),
    ("Package update marker is stable and changes with package source metadata", () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "junimogate-core-tests");
        var signing = new SigningIdentity([digest1]);
        var first = new PackageInstallationSnapshot(
            "com.chucklefish.stardewvalley",
            "1.6.15.3",
            245,
            signing,
            [
                new PackageApkSourceSnapshot(Path.Combine(root, "base.apk"), true, null, 100, 1_000),
                new PackageApkSourceSnapshot(Path.Combine(root, "split.apk"), false, "config.arm64_v8a", 200, 2_000),
            ],
            lastUpdateTimeUtcMilliseconds: 3_000);
        var reordered = new PackageInstallationSnapshot(
            first.PackageName,
            first.VersionName,
            first.LongVersionCode,
            signing,
            first.ApkSources.Reverse(),
            first.LastUpdateTimeUtcMilliseconds);
        var updated = new PackageInstallationSnapshot(
            first.PackageName,
            first.VersionName,
            first.LongVersionCode,
            signing,
            [
                first.ApkSources[0],
                first.ApkSources[1] with { LastModifiedTimeUtcMilliseconds = 2_001 },
            ],
            first.LastUpdateTimeUtcMilliseconds);

        var marker = PackageUpdateMarker.Create(first);
        TestHarness.Equal(64, marker.Length);
        TestHarness.Equal(marker, PackageUpdateMarker.Create(reordered));
        TestHarness.False(marker.Equals(PackageUpdateMarker.Create(updated), StringComparison.Ordinal));
        TestHarness.Throws<InvalidDataException>(() => PackageUpdateMarker.Create(new PackageInstallationSnapshot(
            first.PackageName,
            first.VersionName,
            first.LongVersionCode,
            signing,
            [new PackageApkSourceSnapshot(Path.Combine(root, "base.apk"), true, null)])));
    }),
    ("APK source and installation reject ambiguous identities", () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "junimogate-core-tests");
        var baseSource = new ApkSourceIdentity(Path.Combine(root, "base.apk"), digest1, 11, "base");
        var signing = new SigningIdentity([digest1]);
        TestHarness.Throws<ArgumentException>(() => new ApkSourceIdentity(Path.Combine(root, "invalid.apk"), default, 0, "base"));
        TestHarness.Throws<ArgumentOutOfRangeException>(() => new ApkSourceIdentity(Path.Combine(root, "invalid.apk"), digest1, -1, "base"));
        TestHarness.Throws<ArgumentException>(() => new ApkSourceIdentity(Path.Combine(root, "invalid.apk"), digest1, 0, "split-0", "bad"));
        TestHarness.Throws<ArgumentException>(() => new ApkSourceIdentity(Path.Combine(root, "invalid.apk"), digest1, 0, "base", "not-base"));
        TestHarness.Throws<ArgumentException>(() => new GameInstallationIdentity(
            "invalid",
            "1.6.0",
            1,
            signing,
            "arm64-v8a",
            [baseSource]));
        TestHarness.Throws<ArgumentException>(() => new GameInstallationIdentity(
            "com.chucklefish.stardewvalley",
            "1.6.0",
            1,
            signing,
            "arm64-v8a",
            [new ApkSourceIdentity(Path.Combine(root, "split.apk"), digest2, 1, "split-1", "feature")]));
    }),
    ("Prepared runtime inventory resolves valid files once", () =>
    {
        using var fixture = new RuntimeInventoryFixture();
        var assembly = fixture.Write("assemblies/Game.dll", [1, 2, 3]);
        var content = fixture.Write("Content/Maps/Farm.xnb", [4, 5, 6, 7]);

        var assemblies = PreparedRuntimeFileInventoryBuilder.BuildAndValidate(
            fixture.Root,
            [new PreparedRuntimeFileSpec("Game", "assemblies/Game.dll", assembly)],
            StringComparer.OrdinalIgnoreCase,
            "managed assembly");
        var contentFiles = PreparedRuntimeFileInventoryBuilder.BuildAndValidate(
            fixture.Root,
            [new PreparedRuntimeFileSpec("Content/Maps/Farm.xnb", "Content/Maps/Farm.xnb", content)],
            StringComparer.Ordinal,
            "Content",
            "Content/");

        TestHarness.Equal(Path.Combine(fixture.Root, "assemblies", "Game.dll"), assemblies["game"]);
        TestHarness.Equal(Path.Combine(fixture.Root, "Content", "Maps", "Farm.xnb"), contentFiles["Content/Maps/Farm.xnb"]);
    }),
    ("Prepared runtime inventory rejects missing and changed files", () =>
    {
        using var fixture = new RuntimeInventoryFixture();
        fixture.Write("assemblies/Game.dll", [1, 2, 3]);
        TestHarness.Throws<FileNotFoundException>(() => PreparedRuntimeFileInventoryBuilder.BuildAndValidate(
            fixture.Root,
            [new PreparedRuntimeFileSpec("Missing", "assemblies/Missing.dll", 1)],
            StringComparer.OrdinalIgnoreCase,
            "managed assembly"));
        TestHarness.Throws<InvalidDataException>(() => PreparedRuntimeFileInventoryBuilder.BuildAndValidate(
            fixture.Root,
            [new PreparedRuntimeFileSpec("Game", "assemblies/Game.dll", 4)],
            StringComparer.OrdinalIgnoreCase,
            "managed assembly"));
    }),
    ("Prepared runtime inventory rejects duplicate identities and Content paths", () =>
    {
        using var fixture = new RuntimeInventoryFixture();
        var first = fixture.Write("assemblies/First.dll", [1]);
        var second = fixture.Write("assemblies/Second.dll", [2]);
        TestHarness.Throws<InvalidDataException>(() => PreparedRuntimeFileInventoryBuilder.BuildAndValidate(
            fixture.Root,
            [
                new PreparedRuntimeFileSpec("Game", "assemblies/First.dll", first),
                new PreparedRuntimeFileSpec("game", "assemblies/Second.dll", second),
            ],
            StringComparer.OrdinalIgnoreCase,
            "managed assembly"));

        var content = fixture.Write("Content/Test.xnb", [3]);
        TestHarness.Throws<InvalidDataException>(() => PreparedRuntimeFileInventoryBuilder.BuildAndValidate(
            fixture.Root,
            [
                new PreparedRuntimeFileSpec("Content/Test.xnb", "Content/Test.xnb", content),
                new PreparedRuntimeFileSpec("Content/Test.xnb", "Content/Test.xnb", content),
            ],
            StringComparer.Ordinal,
            "Content",
            "Content/"));
    }),
    ("Prepared runtime inventory rejects escaping and noncanonical paths", () =>
    {
        using var fixture = new RuntimeInventoryFixture();
        foreach (var path in new[] { "../escape.dll", "/absolute.dll", "assemblies\\Game.dll", "Content//Test.xnb" })
        {
            TestHarness.Throws<InvalidDataException>(() => PreparedRuntimeFileInventoryBuilder.BuildAndValidate(
                fixture.Root,
                [new PreparedRuntimeFileSpec("Game", path, 1)],
                StringComparer.OrdinalIgnoreCase,
                "managed assembly"));
        }
    }));

sealed class RuntimeInventoryFixture : IDisposable
{
    public RuntimeInventoryFixture()
    {
        Root = Path.Combine(Path.GetTempPath(), $"junimogate-runtime-inventory-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public long Write(string relativePath, byte[] bytes)
    {
        var path = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        return bytes.LongLength;
    }

    public void Dispose() => Directory.Delete(Root, recursive: true);
}
