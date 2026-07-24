using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
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
    string extractor = "extract-v1",
    string recipe = "rewrite-v1",
    string smapi = "smapi-build-1") =>
    WorkspaceCacheKey.Create(packageName, versionCode, abi, sources ?? [digestA, digestB], extractor, recipe, smapi);

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
    ("APK inventory combines roles without split assumptions", () =>
    {
        var inventory = ApkEntryInventory.Classify([
            "assets/Content/Data/ObjectInformation.xnb",
            "lib/arm64-v8a/libassemblies.arm64-v8a.blob.so",
        ]);
        TestHarness.True(inventory.Contains(ApkContentRole.GameContent));
        TestHarness.True(inventory.Contains(ApkContentRole.ModernAssemblyBlob));
        TestHarness.False(inventory.Contains(ApkContentRole.LegacyAssemblyBlob));
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
    ("Workspace cache key changes for every identity field", () =>
    {
        var baseline = Key();
        var variants = new[]
        {
            Key(packageName: "com.chucklefish.stardewvalleysamsung"),
            Key(versionCode: 124),
            Key(abi: "x86_64"),
            Key(sources: [digestA, digestC]),
            Key(extractor: "extract-v2"),
            Key(recipe: "rewrite-v2"),
            Key(smapi: "smapi-build-2"),
        };

        foreach (var variant in variants)
        {
            TestHarness.False(baseline.Equals(variant), "An identity-field change must invalidate the cache key.");
        }
    }));

static MemoryStream BuildElfStore(
    byte[] imageData,
    bool includePayloadSection = true,
    uint? dataOffsetOverride = null,
    uint? dataSizeOverride = null,
    uint? descriptorIndexOverride = null)
{
    const string assemblyName = "Synthetic.dll";
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

static int Align(int value, int alignment) => checked((value + alignment - 1) & ~(alignment - 1));
