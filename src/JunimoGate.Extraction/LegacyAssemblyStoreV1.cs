// Format semantics adapted from dotnet/android's MIT-licensed assembly-store-reader-mk2,
// commit 1361e50584b56e690e2b8b5f6db6a04a1d2b7b38. See THIRD-PARTY-NOTICES.md.
using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using K4os.Compression.LZ4;

namespace JunimoGate.Extraction;

public static class LegacyAssemblyStoreApkPath
{
    public const string BaseStorePath = "assemblies/assemblies.blob";
    public const string ManifestPath = "assemblies/assemblies.manifest";

    public static string GetAbiStorePath(string abi)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(abi);
        if (abi is "." or ".." || abi.IndexOfAny(['/', '\\', '\0']) >= 0)
            throw new ArgumentException("The AssemblyStore ABI is not a safe path segment.", nameof(abi));

        return $"assemblies/assemblies.{abi.Replace('-', '_')}.blob";
    }

    public static bool IsSelectedStorePath(string? path, string abi)
    {
        if (string.IsNullOrEmpty(path) || path.IndexOfAny(['\\', '\0']) >= 0)
            return false;

        return path.Equals(BaseStorePath, StringComparison.OrdinalIgnoreCase) ||
               path.Equals(GetAbiStorePath(abi), StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class LegacyAssemblyStoreItem
{
    internal LegacyAssemblyStoreItem(
        LegacyAssemblyStoreSet owner,
        LegacyAssemblyStoreBlob blob,
        uint descriptorIndex,
        string name,
        string abi,
        string sourceEntry,
        LegacyAssemblyDescriptor descriptor)
    {
        Owner = owner;
        Blob = blob;
        DescriptorIndex = descriptorIndex;
        Name = name;
        Abi = abi;
        SourceEntry = sourceEntry;
        Descriptor = descriptor;
    }

    public uint DescriptorIndex { get; }
    public string Name { get; }
    public string Abi { get; }
    public string SourceEntry { get; }
    public uint DataSize => Descriptor.DataSize;

    internal LegacyAssemblyStoreSet Owner { get; }
    internal LegacyAssemblyStoreBlob Blob { get; }
    internal LegacyAssemblyDescriptor Descriptor { get; }
}

/// <summary>Reads the pre-.NET 9 APK AssemblyStore v1 set for one selected ABI.</summary>
public sealed class LegacyAssemblyStoreSet : IDisposable, IAsyncDisposable
{
    private const int MaximumManifestBytes = 16 * 1024 * 1024;
    private const int MaximumManifestEntries = 100_000;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private readonly LegacyAssemblyStoreBlob[] blobs;
    private bool disposed;

    private LegacyAssemblyStoreSet(LegacyAssemblyStoreBlob[] blobs)
    {
        this.blobs = blobs;
        Items = [];
    }

    public IReadOnlyList<LegacyAssemblyStoreItem> Items { get; private set; }

    public static bool HasCandidate(ZipArchive archive)
    {
        ArgumentNullException.ThrowIfNull(archive);
        return archive.Entries.Any(static entry =>
            entry.FullName.Equals(LegacyAssemblyStoreApkPath.BaseStorePath, StringComparison.OrdinalIgnoreCase));
    }

    public static LegacyAssemblyStoreSet Open(ZipArchive archive, string abi)
    {
        ArgumentNullException.ThrowIfNull(archive);
        ArgumentException.ThrowIfNullOrWhiteSpace(abi);

        var baseEntry = GetUniqueEntry(archive, LegacyAssemblyStoreApkPath.BaseStorePath);
        var abiEntry = GetUniqueEntry(archive, LegacyAssemblyStoreApkPath.GetAbiStorePath(abi));
        var manifestEntry = GetUniqueEntry(archive, LegacyAssemblyStoreApkPath.ManifestPath);

        LegacyAssemblyStoreBlob? baseBlob = null;
        LegacyAssemblyStoreBlob? abiBlob = null;
        try
        {
            baseBlob = LegacyAssemblyStoreBlob.Open(baseEntry, abi: string.Empty);
            abiBlob = LegacyAssemblyStoreBlob.Open(abiEntry, abi);
            if (baseBlob.StoreId != 0 || abiBlob.StoreId == 0 || baseBlob.StoreId == abiBlob.StoreId)
                throw new AssemblyStoreFormatException("Legacy AssemblyStore blobs have invalid or ambiguous store IDs.");

            var result = new LegacyAssemblyStoreSet([baseBlob, abiBlob]);
            result.Items = result.MapManifest(ReadManifest(manifestEntry), abi);
            return result;
        }
        catch
        {
            baseBlob?.Dispose();
            abiBlob?.Dispose();
            throw;
        }
    }

    public async ValueTask CopyAssemblyImageToAsync(
        LegacyAssemblyStoreItem item,
        Stream destination,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(destination);
        if (!ReferenceEquals(item.Owner, this))
            throw new ArgumentException("The item does not belong to this legacy AssemblyStore set.", nameof(item));

        await item.Blob.CopyAssemblyImageToAsync(item, destination, cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;
        foreach (var blob in blobs)
            blob.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
            return;

        disposed = true;
        foreach (var blob in blobs)
            await blob.DisposeAsync().ConfigureAwait(false);
    }

    private IReadOnlyList<LegacyAssemblyStoreItem> MapManifest(
        IReadOnlyList<LegacyManifestEntry> manifest,
        string abi)
    {
        var byId = blobs.ToDictionary(static blob => blob.StoreId);
        var mapped = blobs.ToDictionary(static blob => blob.StoreId, static blob => new bool[blob.Descriptors.Count]);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var items = new List<LegacyAssemblyStoreItem>(manifest.Count);
        foreach (var entry in manifest)
        {
            if (!byId.TryGetValue(entry.StoreId, out var blob) || entry.StoreIndex >= blob.Descriptors.Count)
                throw new AssemblyStoreFormatException("The legacy AssemblyStore manifest references an unavailable store entry.");

            if (mapped[entry.StoreId][entry.StoreIndex])
                throw new AssemblyStoreFormatException("The legacy AssemblyStore manifest maps one store entry more than once.");
            mapped[entry.StoreId][entry.StoreIndex] = true;

            var name = entry.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                ? entry.Name
                : entry.Name + ".dll";
            try
            {
                AssemblyExtractionTransaction.ValidateAssemblyBaseName(name);
            }
            catch (ArgumentException exception)
            {
                throw new AssemblyStoreFormatException($"The legacy AssemblyStore manifest contains an unsafe assembly name: {exception.Message}");
            }

            if (!names.Add(name))
                throw new AssemblyStoreFormatException("The legacy AssemblyStore manifest contains duplicate assembly names.");

            items.Add(new LegacyAssemblyStoreItem(
                this,
                blob,
                entry.StoreIndex,
                name,
                abi,
                blob.SourceEntry,
                blob.Descriptors[(int)entry.StoreIndex]));
        }

        if (mapped.Values.Any(static entries => entries.Any(static value => !value)))
            throw new AssemblyStoreFormatException("The legacy AssemblyStore manifest does not name every selected store entry.");

        return items.OrderBy(static item => item.Name, StringComparer.Ordinal).ToArray();
    }

    private static ZipArchiveEntry GetUniqueEntry(ZipArchive archive, string expectedPath)
    {
        var matches = archive.Entries.Where(entry =>
            entry.FullName.Equals(expectedPath, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length != 1 || !matches[0].FullName.Equals(expectedPath, StringComparison.Ordinal))
            throw new AssemblyStoreFormatException($"Legacy AssemblyStore entry '{expectedPath}' is missing, duplicated, or non-canonical.");

        return matches[0];
    }

    private static IReadOnlyList<LegacyManifestEntry> ReadManifest(ZipArchiveEntry entry)
    {
        if (entry.Length <= 0 || entry.Length > MaximumManifestBytes)
            throw new AssemblyStoreFormatException("The legacy AssemblyStore manifest exceeds its size bounds.");

        var bytes = GC.AllocateUninitializedArray<byte>(checked((int)entry.Length));
        using (var stream = entry.Open())
            stream.ReadExactly(bytes);

        string text;
        try
        {
            text = StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            throw new AssemblyStoreFormatException("The legacy AssemblyStore manifest is not valid UTF-8.");
        }

        if (text.IndexOf('\0') >= 0)
            throw new AssemblyStoreFormatException("The legacy AssemblyStore manifest contains a NUL character.");

        var lines = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length is < 2 or > MaximumManifestEntries + 1)
            throw new AssemblyStoreFormatException("The legacy AssemblyStore manifest entry count is outside supported bounds.");

        var result = new List<LegacyManifestEntry>(lines.Length - 1);
        for (var index = 1; index < lines.Length; index++)
        {
            var fields = lines[index].Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length != 5 ||
                !TryParseHexUInt32(fields[0], out _) ||
                !TryParseHexUInt64(fields[1], out _) ||
                !uint.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out var storeId) ||
                !uint.TryParse(fields[3], NumberStyles.None, CultureInfo.InvariantCulture, out var storeIndex) ||
                string.IsNullOrWhiteSpace(fields[4]) || fields[4].Length > 512)
            {
                throw new AssemblyStoreFormatException("The legacy AssemblyStore manifest contains a malformed entry.");
            }

            result.Add(new LegacyManifestEntry(storeId, storeIndex, fields[4]));
        }

        return result;
    }

    private static bool TryParseHexUInt32(string value, out uint result) =>
        uint.TryParse(RemoveHexPrefix(value), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out result);

    private static bool TryParseHexUInt64(string value, out ulong result) =>
        ulong.TryParse(RemoveHexPrefix(value), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out result);

    private static string RemoveHexPrefix(string value) =>
        value.StartsWith("0x", StringComparison.Ordinal) ? value[2..] : value;

    private sealed record LegacyManifestEntry(uint StoreId, uint StoreIndex, string Name);
}

internal sealed class LegacyAssemblyStoreBlob : IDisposable, IAsyncDisposable
{
    private const uint Version = 1;
    private const long MaximumStoreSize = uint.MaxValue;
    private const int MaximumEntryCount = 100_000;
    private const int MaximumIndexEntryCount = 1_000_000;
    private const uint XalzMagic = 0x5A4C4158;

    private readonly Stream stream;
    private readonly SemaphoreSlim ioGate = new(1, 1);
    private bool disposed;

    private LegacyAssemblyStoreBlob(
        Stream stream,
        string sourceEntry,
        string abi,
        uint storeId,
        IReadOnlyList<LegacyAssemblyDescriptor> descriptors)
    {
        this.stream = stream;
        SourceEntry = sourceEntry;
        Abi = abi;
        StoreId = storeId;
        Descriptors = descriptors;
    }

    public string SourceEntry { get; }
    public string Abi { get; }
    public uint StoreId { get; }
    public IReadOnlyList<LegacyAssemblyDescriptor> Descriptors { get; }

    public static LegacyAssemblyStoreBlob Open(ZipArchiveEntry entry, string abi)
    {
        var input = entry.Open();
        Stream? seekable = null;
        try
        {
            seekable = SpoolToTemporaryFile(input);
            input.Dispose();
            var parsed = Parse(seekable, entry.FullName);
            return new LegacyAssemblyStoreBlob(seekable, entry.FullName, abi, parsed.StoreId, parsed.Descriptors);
        }
        catch
        {
            input.Dispose();
            seekable?.Dispose();
            throw;
        }
    }

    public async ValueTask CopyAssemblyImageToAsync(
        LegacyAssemblyStoreItem item,
        Stream destination,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!ReferenceEquals(item.Blob, this))
            throw new ArgumentException("The item does not belong to this legacy AssemblyStore blob.", nameof(item));
        if (!destination.CanWrite)
            throw new ArgumentException("Destination stream must be writable.", nameof(destination));

        await ioGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            stream.Position = item.Descriptor.DataOffset;
            var header = new byte[12];
            var headerLength = (int)Math.Min(item.Descriptor.DataSize, (uint)header.Length);
            await ReadExactlyAsync(stream, header.AsMemory(0, headerLength), cancellationToken).ConfigureAwait(false);

            if (headerLength >= 2 && header[0] == (byte)'M' && header[1] == (byte)'Z')
            {
                await destination.WriteAsync(header.AsMemory(0, headerLength), cancellationToken).ConfigureAwait(false);
                await CopyExactlyAsync(stream, destination, item.Descriptor.DataSize - (uint)headerLength, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (headerLength < 12 || BinaryPrimitives.ReadUInt32LittleEndian(header) != XalzMagic)
                throw new AssemblyStoreFormatException($"Legacy assembly '{item.Name}' is neither an MZ image nor a complete XALZ image.");

            var uncompressedLength = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8));
            if (uncompressedLength > AssemblyStoreV2.MaximumAssemblyImageSize)
                throw new AssemblyStoreFormatException($"Legacy assembly '{item.Name}' exceeds the assembly image size limit.");

            var compressedLength = checked((int)(item.Descriptor.DataSize - 12));
            var maximumCompressedLength = LZ4Codec.MaximumOutputSize(checked((int)uncompressedLength));
            if (compressedLength > maximumCompressedLength)
                throw new AssemblyStoreFormatException($"Legacy XALZ payload for '{item.Name}' exceeds its declared output bound.");

            var compressed = GC.AllocateUninitializedArray<byte>(compressedLength);
            await ReadExactlyAsync(stream, compressed, cancellationToken).ConfigureAwait(false);
            var uncompressed = GC.AllocateUninitializedArray<byte>(checked((int)uncompressedLength));
            var decodedLength = LZ4Codec.Decode(compressed, uncompressed);
            if (decodedLength != uncompressed.Length || uncompressed.Length < 2 ||
                uncompressed[0] != (byte)'M' || uncompressed[1] != (byte)'Z')
            {
                throw new AssemblyStoreFormatException($"Legacy XALZ payload for '{item.Name}' did not decode to one managed image.");
            }

            await destination.WriteAsync(uncompressed, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ioGate.Release();
        }
    }

    public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;
        ioGate.Dispose();
        stream.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
            return;

        disposed = true;
        ioGate.Dispose();
        await stream.DisposeAsync().ConfigureAwait(false);
    }

    private static ParsedLegacyStore Parse(Stream stream, string sourceName)
    {
        if (stream.Length < 20 || stream.Length > MaximumStoreSize)
            throw new AssemblyStoreFormatException($"Legacy AssemblyStore '{sourceName}' exceeds its size bounds.");

        Span<byte> header = stackalloc byte[20];
        stream.ReadExactly(header);
        if (BinaryPrimitives.ReadUInt32LittleEndian(header) != AssemblyStoreV2.Magic ||
            BinaryPrimitives.ReadUInt32LittleEndian(header[4..]) != Version)
        {
            throw new AssemblyStoreFormatException($"Legacy AssemblyStore '{sourceName}' has an unsupported header.");
        }

        var localCount = BinaryPrimitives.ReadUInt32LittleEndian(header[8..]);
        var globalCount = BinaryPrimitives.ReadUInt32LittleEndian(header[12..]);
        var storeId = BinaryPrimitives.ReadUInt32LittleEndian(header[16..]);
        if (localCount is 0 or > MaximumEntryCount || globalCount > MaximumIndexEntryCount)
            throw new AssemblyStoreFormatException($"Legacy AssemblyStore '{sourceName}' has unsupported entry counts.");

        var descriptorBytes = checked((long)localCount * 24);
        var indexBytes = storeId == 0 ? checked((long)globalCount * 40) : 0;
        var payloadFloor = checked(20 + descriptorBytes + indexBytes);
        if (payloadFloor > stream.Length)
            throw new AssemblyStoreFormatException($"Legacy AssemblyStore '{sourceName}' metadata exceeds the blob length.");

        var descriptors = new LegacyAssemblyDescriptor[localCount];
        Span<byte> descriptorBytesBuffer = stackalloc byte[24];
        for (var index = 0; index < descriptors.Length; index++)
        {
            stream.ReadExactly(descriptorBytesBuffer);
            var descriptor = new LegacyAssemblyDescriptor(
                BinaryPrimitives.ReadUInt32LittleEndian(descriptorBytesBuffer),
                BinaryPrimitives.ReadUInt32LittleEndian(descriptorBytesBuffer[4..]),
                BinaryPrimitives.ReadUInt32LittleEndian(descriptorBytesBuffer[8..]),
                BinaryPrimitives.ReadUInt32LittleEndian(descriptorBytesBuffer[12..]),
                BinaryPrimitives.ReadUInt32LittleEndian(descriptorBytesBuffer[16..]),
                BinaryPrimitives.ReadUInt32LittleEndian(descriptorBytesBuffer[20..]));
            ValidateRange(descriptor.DataOffset, descriptor.DataSize, payloadFloor, stream.Length, sourceName, "assembly");
            ValidateOptionalRange(descriptor.DebugDataOffset, descriptor.DebugDataSize, payloadFloor, stream.Length, sourceName, "debug data");
            ValidateOptionalRange(descriptor.ConfigDataOffset, descriptor.ConfigDataSize, payloadFloor, stream.Length, sourceName, "config data");
            descriptors[index] = descriptor;
        }

        return new ParsedLegacyStore(storeId, descriptors);
    }

    private static void ValidateOptionalRange(
        uint offset,
        uint size,
        long payloadFloor,
        long streamLength,
        string sourceName,
        string description)
    {
        if (offset == 0 && size == 0)
            return;
        if (offset == 0 || size == 0)
            throw new AssemblyStoreFormatException($"Legacy AssemblyStore '{sourceName}' has an incomplete {description} range.");

        ValidateRange(offset, size, payloadFloor, streamLength, sourceName, description);
    }

    private static void ValidateRange(
        uint offset,
        uint size,
        long payloadFloor,
        long streamLength,
        string sourceName,
        string description)
    {
        if (size == 0 || size > AssemblyStoreV2.MaximumAssemblyImageSize || offset < payloadFloor ||
            checked((long)offset + size) > streamLength)
        {
            throw new AssemblyStoreFormatException($"Legacy AssemblyStore '{sourceName}' has an invalid {description} range.");
        }
    }

    private static Stream SpoolToTemporaryFile(Stream input)
    {
        var path = Path.Combine(Path.GetTempPath(), $"junimogate-legacy-store-{Guid.NewGuid():N}.tmp");
        FileStream? temporary = null;
        try
        {
            temporary = new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.RandomAccess | FileOptions.DeleteOnClose);
            var buffer = new byte[64 * 1024];
            long total = 0;
            while (true)
            {
                var read = input.Read(buffer);
                if (read == 0)
                    break;

                total = checked(total + read);
                if (total > MaximumStoreSize)
                    throw new AssemblyStoreFormatException("Legacy AssemblyStore stream exceeds its size limit.");

                temporary.Write(buffer, 0, read);
            }

            temporary.Position = 0;
            return temporary;
        }
        catch
        {
            temporary?.Dispose();
            try
            {
                File.Delete(path);
            }
            catch
            {
                // DeleteOnClose handles the normal path.
            }

            throw;
        }
    }

    private static async ValueTask ReadExactlyAsync(Stream source, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        while (!buffer.IsEmpty)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                throw new EndOfStreamException("Unexpected end of legacy AssemblyStore entry data.");
            buffer = buffer[read..];
        }
    }

    private static async ValueTask CopyExactlyAsync(
        Stream source,
        Stream destination,
        uint byteCount,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        long remaining = byteCount;
        while (remaining > 0)
        {
            var read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken).ConfigureAwait(false);
            if (read == 0)
                throw new EndOfStreamException("Unexpected end of legacy AssemblyStore entry data.");
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            remaining -= read;
        }
    }

    private sealed record ParsedLegacyStore(uint StoreId, IReadOnlyList<LegacyAssemblyDescriptor> Descriptors);
}

internal readonly record struct LegacyAssemblyDescriptor(
    uint DataOffset,
    uint DataSize,
    uint DebugDataOffset,
    uint DebugDataSize,
    uint ConfigDataOffset,
    uint ConfigDataSize);
