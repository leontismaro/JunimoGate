// Format semantics adapted from dotnet/android's MIT-licensed assembly-store-reader-mk2,
// commit 1361e50584b56e690e2b8b5f6db6a04a1d2b7b38. See THIRD-PARTY-NOTICES.md.
using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using K4os.Compression.LZ4;

namespace JunimoGate.Extraction;

public sealed class AssemblyStoreFormatException : IOException
{
    public AssemblyStoreFormatException(string message)
        : base(message)
    {
    }
}

public sealed record AssemblyStoreItem(
    uint DescriptorIndex,
    string Name,
    string Abi,
    uint DataOffset,
    uint DataSize,
    uint DebugDataOffset,
    uint DebugDataSize,
    uint ConfigDataOffset,
    uint ConfigDataSize,
    IReadOnlyList<ulong> NameHashes);

/// <summary>An AssemblyStore candidate owned by an open APK archive.</summary>
/// <remarks>The parent <see cref="ZipArchive"/> must remain open until <see cref="Open"/> returns.</remarks>
public sealed record AssemblyStoreApkEntry(ZipArchiveEntry Entry, string Abi)
{
    public AssemblyStoreV2 Open()
    {
        var stream = Entry.Open();
        AssemblyStoreV2? store = null;
        try
        {
            store = AssemblyStoreV2.Open(stream, leaveOpen: false, sourceName: Entry.FullName);
            if (!store.Abi.Equals(Abi, StringComparison.OrdinalIgnoreCase))
            {
                throw new AssemblyStoreFormatException(
                    $"Assembly store ABI '{store.Abi}' does not match APK path ABI '{Abi}' in '{Entry.FullName}'.");
            }

            return store;
        }
        catch
        {
            store?.Dispose();
            if (store is null)
            {
                stream.Dispose();
            }

            throw;
        }
    }
}

public static class AssemblyStoreApkPath
{
    public static bool TryParse(string? entryName, out string abi)
    {
        abi = string.Empty;
        if (string.IsNullOrEmpty(entryName) || entryName.IndexOfAny(['\\', '\0']) >= 0)
        {
            return false;
        }

        var segments = entryName.Split('/', StringSplitOptions.None);
        if (segments.Length != 3 ||
            !segments[0].Equals("lib", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(segments[1]) ||
            segments[1] is "." or ".." ||
            segments[1].IndexOfAny(['/', '\\', '\0']) >= 0)
        {
            return false;
        }

        var expectedFileName = $"libassemblies.{segments[1]}.blob.so";
        if (!segments[2].Equals(expectedFileName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        abi = segments[1];
        return true;
    }
}

public sealed class AssemblyStoreV2 : IDisposable, IAsyncDisposable
{
    public const uint Magic = 0x41424158; // XABA in little-endian storage
    public const uint Arm64Version = 0x80010002;
    public const uint ArmVersion = 0x00020002;
    public const uint X64Version = 0x80030002;
    public const uint X86Version = 0x00040002;
    public const long MaximumAssemblyImageSize = 512L * 1024 * 1024;

    private const long MaximumStoreSize = 8L * 1024 * 1024 * 1024;
    private const int MaximumEntryCount = 100_000;
    private const int MaximumIndexEntryCount = 1_000_000;
    private const int MaximumNameByteLength = 16 * 1024;
    private const int MaximumTotalNameBytes = 64 * 1024 * 1024;
    private const int MaximumElfStringTableBytes = 16 * 1024 * 1024;
    private const uint XalzMagic = 0x5A4C4158; // XALZ

    private readonly Stream stream;
    private readonly bool ownsStream;
    private readonly long streamOrigin;
    private readonly long streamLength;
    private readonly SemaphoreSlim ioGate = new(1, 1);
    private bool disposed;

    private AssemblyStoreV2(
        Stream stream,
        bool ownsStream,
        long streamOrigin,
        long streamLength,
        string sourceName,
        long payloadOffset,
        long payloadSize,
        uint rawVersion,
        string abi,
        IReadOnlyList<AssemblyStoreItem> items,
        uint indexEntryCount)
    {
        this.stream = stream;
        this.ownsStream = ownsStream;
        this.streamOrigin = streamOrigin;
        this.streamLength = streamLength;
        SourceName = sourceName;
        PayloadOffset = payloadOffset;
        PayloadSize = payloadSize;
        RawVersion = rawVersion;
        Abi = abi;
        Items = items;
        IndexEntryCount = indexEntryCount;
    }

    public string SourceName { get; }

    public long PayloadOffset { get; }

    public long PayloadSize { get; }

    public uint RawVersion { get; }

    public string Abi { get; }

    public bool Is64Bit => (RawVersion & 0x80000000) != 0;

    public uint IndexEntryCount { get; }

    public IReadOnlyList<AssemblyStoreItem> Items { get; }

    public static IReadOnlyList<AssemblyStoreApkEntry> FindInApk(ZipArchive archive)
    {
        ArgumentNullException.ThrowIfNull(archive);

        var entries = new List<AssemblyStoreApkEntry>();
        foreach (var entry in archive.Entries)
        {
            if (AssemblyStoreApkPath.TryParse(entry.FullName, out var abi))
            {
                entries.Add(new AssemblyStoreApkEntry(entry, abi));
            }
        }

        return entries.AsReadOnly();
    }

    public static AssemblyStoreV2 Open(
        Stream input,
        bool leaveOpen = false,
        string? sourceName = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!input.CanRead)
        {
            throw new ArgumentException("Assembly store stream must be readable.", nameof(input));
        }

        Stream seekable = input;
        var ownsSeekable = !leaveOpen;
        if (!input.CanSeek)
        {
            seekable = SpoolToTemporaryFile(input);
            ownsSeekable = true;
            if (!leaveOpen)
            {
                input.Dispose();
            }
        }

        try
        {
            var origin = seekable.Position;
            var length = checked(seekable.Length - origin);
            if (length < 4)
            {
                throw new AssemblyStoreFormatException("Assembly store is shorter than its magic header.");
            }

            if (length > MaximumStoreSize)
            {
                throw new AssemblyStoreFormatException(
                    $"Assembly store length {length} exceeds the {MaximumStoreSize}-byte safety limit.");
            }

            var parsed = Parse(seekable, origin, length, sourceName ?? "<stream>");
            return new AssemblyStoreV2(
                seekable,
                ownsSeekable,
                origin,
                length,
                sourceName ?? "<stream>",
                parsed.PayloadOffset,
                parsed.PayloadSize,
                parsed.RawVersion,
                parsed.Abi,
                parsed.Items,
                parsed.IndexEntryCount);
        }
        catch
        {
            if (ownsSeekable)
            {
                seekable.Dispose();
            }

            throw;
        }
    }

    public void CopyAssemblyImageTo(AssemblyStoreItem item, Stream destination)
    {
        CopyAssemblyImageToAsync(item, destination).AsTask().GetAwaiter().GetResult();
    }

    public async ValueTask CopyAssemblyImageToAsync(
        AssemblyStoreItem item,
        Stream destination,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite)
        {
            throw new ArgumentException("Destination stream must be writable.", nameof(destination));
        }

        if (item.DescriptorIndex >= Items.Count || !ReferenceEquals(item, Items[(int)item.DescriptorIndex]))
        {
            throw new ArgumentException("The item does not belong to this assembly store.", nameof(item));
        }

        await ioGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var absoluteDataOffset = checked(PayloadOffset + item.DataOffset);
            stream.Position = checked(streamOrigin + absoluteDataOffset);

            var firstHeader = new byte[12];
            var headerLength = (int)Math.Min(item.DataSize, (uint)firstHeader.Length);
            await ReadExactlyAsync(stream, firstHeader.AsMemory(0, headerLength), cancellationToken).ConfigureAwait(false);

            if (headerLength >= 2 && firstHeader[0] == (byte)'M' && firstHeader[1] == (byte)'Z')
            {
                await destination.WriteAsync(firstHeader.AsMemory(0, headerLength), cancellationToken).ConfigureAwait(false);
                await CopyExactlyAsync(stream, destination, item.DataSize - (uint)headerLength, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (headerLength < 12 || BinaryPrimitives.ReadUInt32LittleEndian(firstHeader) != XalzMagic)
            {
                throw new AssemblyStoreFormatException(
                    $"Assembly '{item.Name}' is neither an uncompressed MZ image nor a complete XALZ image.");
            }

            var descriptorIndex = BinaryPrimitives.ReadUInt32LittleEndian(firstHeader.AsSpan(4));
            if (descriptorIndex != item.DescriptorIndex)
            {
                throw new AssemblyStoreFormatException(
                    $"XALZ descriptor index {descriptorIndex} does not match store descriptor {item.DescriptorIndex} for '{item.Name}'.");
            }

            var uncompressedLength = BinaryPrimitives.ReadUInt32LittleEndian(firstHeader.AsSpan(8));
            if (uncompressedLength > MaximumAssemblyImageSize)
            {
                throw new AssemblyStoreFormatException(
                    $"XALZ image '{item.Name}' declares {uncompressedLength} bytes, exceeding the {MaximumAssemblyImageSize}-byte limit.");
            }

            var compressedLength = checked((int)(item.DataSize - 12));
            var maximumCompressedLength = LZ4Codec.MaximumOutputSize(checked((int)uncompressedLength));
            if (compressedLength > maximumCompressedLength)
            {
                throw new AssemblyStoreFormatException(
                    $"XALZ payload for '{item.Name}' is too large for its declared output length.");
            }

            var compressed = GC.AllocateUninitializedArray<byte>(compressedLength);
            await ReadExactlyAsync(stream, compressed, cancellationToken).ConfigureAwait(false);
            var uncompressed = GC.AllocateUninitializedArray<byte>(checked((int)uncompressedLength));
            var decodedLength = LZ4Codec.Decode(compressed, uncompressed);
            if (decodedLength != uncompressed.Length)
            {
                throw new AssemblyStoreFormatException(
                    $"XALZ decode length for '{item.Name}' was {decodedLength}; expected {uncompressed.Length}.");
            }

            if (uncompressed.Length < 2 || uncompressed[0] != (byte)'M' || uncompressed[1] != (byte)'Z')
            {
                throw new AssemblyStoreFormatException($"Decoded XALZ image '{item.Name}' is not an MZ assembly image.");
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
        {
            return;
        }

        disposed = true;
        ioGate.Dispose();
        if (ownsStream)
        {
            stream.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        ioGate.Dispose();
        if (ownsStream)
        {
            await stream.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static ParsedStore Parse(Stream stream, long origin, long length, string sourceName)
    {
        Span<byte> magicBuffer = stackalloc byte[4];
        ReadExactlyAt(stream, origin, 0, magicBuffer);
        var magic = BinaryPrimitives.ReadUInt32LittleEndian(magicBuffer);

        long payloadOffset;
        long payloadSize;
        ushort? elfMachine = null;
        if (magic == 0x464C457F)
        {
            (payloadOffset, payloadSize, elfMachine) = FindElf64Payload(stream, origin, length, sourceName);
        }
        else
        {
            payloadOffset = 0;
            payloadSize = length;
        }

        if (payloadSize < 20)
        {
            throw new AssemblyStoreFormatException($"Assembly store payload in '{sourceName}' is shorter than the 20-byte v2 header.");
        }

        Span<byte> header = stackalloc byte[20];
        ReadExactlyAt(stream, origin, payloadOffset, header);
        var storeMagic = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (storeMagic != Magic)
        {
            throw new AssemblyStoreFormatException(
                $"Assembly store payload in '{sourceName}' has bad XABA magic 0x{storeMagic:X8}.");
        }

        var version = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
        var (abi, is64Bit, expectedElfMachine) = version switch
        {
            Arm64Version => ("arm64-v8a", true, (ushort)183),
            ArmVersion => ("armeabi-v7a", false, (ushort)40),
            X64Version => ("x86_64", true, (ushort)62),
            X86Version => ("x86", false, (ushort)3),
            _ => throw new AssemblyStoreFormatException(
                $"Assembly store payload in '{sourceName}' has unsupported full version 0x{version:X8}."),
        };

        if (!is64Bit)
        {
            throw new AssemblyStoreFormatException(
                $"Assembly store ABI '{abi}' uses a 32-bit v2 layout; this reader supports only current ELF64 little-endian stores.");
        }

        if (elfMachine is not null && elfMachine != expectedElfMachine)
        {
            throw new AssemblyStoreFormatException(
                $"ELF machine {elfMachine} does not match AssemblyStore ABI '{abi}' (expected {expectedElfMachine}) in '{sourceName}'.");
        }

        var entryCount = BinaryPrimitives.ReadUInt32LittleEndian(header[8..]);
        var indexEntryCount = BinaryPrimitives.ReadUInt32LittleEndian(header[12..]);
        var indexByteSize = BinaryPrimitives.ReadUInt32LittleEndian(header[16..]);
        if (entryCount == 0 || entryCount > MaximumEntryCount)
        {
            throw new AssemblyStoreFormatException($"Assembly entry count {entryCount} is outside the supported range 1..{MaximumEntryCount}.");
        }

        if (indexEntryCount == 0 || indexEntryCount > MaximumIndexEntryCount)
        {
            throw new AssemblyStoreFormatException(
                $"Assembly index entry count {indexEntryCount} is outside the supported range 1..{MaximumIndexEntryCount}.");
        }

        var expectedIndexByteSize = checked((ulong)indexEntryCount * 12UL);
        if (indexByteSize != expectedIndexByteSize)
        {
            throw new AssemblyStoreFormatException(
                $"Assembly index byte size {indexByteSize} does not equal {indexEntryCount} 12-byte index entries ({expectedIndexByteSize} bytes)." );
        }

        var minimumMetadataSize = checked(20UL + expectedIndexByteSize + ((ulong)entryCount * 28UL) + ((ulong)entryCount * 4UL));
        if (minimumMetadataSize > (ulong)payloadSize)
        {
            throw new AssemblyStoreFormatException("AssemblyStore counts require metadata beyond the payload boundary.");
        }

        var cursor = 20L;
        var hashLists = new List<ulong>[checked((int)entryCount)];
        var seenDescriptors = new bool[checked((int)entryCount)];
        Span<byte> indexBytes = stackalloc byte[12];
        for (var i = 0U; i < indexEntryCount; i++)
        {
            ReadPayload(stream, origin, payloadOffset, payloadSize, cursor, indexBytes, "index entry");
            cursor += 12;
            var nameHash = BinaryPrimitives.ReadUInt64LittleEndian(indexBytes);
            var descriptorIndex = BinaryPrimitives.ReadUInt32LittleEndian(indexBytes[8..]);
            if (descriptorIndex >= entryCount)
            {
                throw new AssemblyStoreFormatException(
                    $"Index entry {i} references descriptor {descriptorIndex}, but only {entryCount} descriptors exist.");
            }

            (hashLists[descriptorIndex] ??= []).Add(nameHash);
            seenDescriptors[descriptorIndex] = true;
        }

        var descriptors = new Descriptor[checked((int)entryCount)];
        Span<byte> descriptorBytes = stackalloc byte[28];
        for (var i = 0; i < descriptors.Length; i++)
        {
            ReadPayload(stream, origin, payloadOffset, payloadSize, cursor, descriptorBytes, "descriptor");
            cursor += 28;
            var descriptor = new Descriptor(
                BinaryPrimitives.ReadUInt32LittleEndian(descriptorBytes),
                BinaryPrimitives.ReadUInt32LittleEndian(descriptorBytes[4..]),
                BinaryPrimitives.ReadUInt32LittleEndian(descriptorBytes[8..]),
                BinaryPrimitives.ReadUInt32LittleEndian(descriptorBytes[12..]),
                BinaryPrimitives.ReadUInt32LittleEndian(descriptorBytes[16..]),
                BinaryPrimitives.ReadUInt32LittleEndian(descriptorBytes[20..]),
                BinaryPrimitives.ReadUInt32LittleEndian(descriptorBytes[24..]));

            if (descriptor.DataSize == 0 || descriptor.DataSize > MaximumAssemblyImageSize)
            {
                throw new AssemblyStoreFormatException(
                    $"Descriptor {i} assembly data size {descriptor.DataSize} is outside the supported range 1..{MaximumAssemblyImageSize}.");
            }

            ValidatePayloadRange(descriptor.DataOffset, descriptor.DataSize, payloadSize, $"descriptor {i} assembly data");
            ValidatePayloadRange(descriptor.DebugOffset, descriptor.DebugSize, payloadSize, $"descriptor {i} debug data");
            ValidatePayloadRange(descriptor.ConfigOffset, descriptor.ConfigSize, payloadSize, $"descriptor {i} config data");
            descriptors[i] = descriptor;
        }

        var strictUtf8 = new UTF8Encoding(false, true);
        var names = new string[descriptors.Length];
        var totalNameBytes = 0;
        Span<byte> lengthBytes = stackalloc byte[4];
        for (var i = 0; i < names.Length; i++)
        {
            ReadPayload(stream, origin, payloadOffset, payloadSize, cursor, lengthBytes, "name length");
            cursor += 4;
            var nameLength = BinaryPrimitives.ReadUInt32LittleEndian(lengthBytes);
            if (nameLength == 0 || nameLength > MaximumNameByteLength)
            {
                throw new AssemblyStoreFormatException(
                    $"Descriptor {i} name length {nameLength} is outside the supported range 1..{MaximumNameByteLength}.");
            }

            totalNameBytes = checked(totalNameBytes + (int)nameLength);
            if (totalNameBytes > MaximumTotalNameBytes)
            {
                throw new AssemblyStoreFormatException(
                    $"Assembly names exceed the {MaximumTotalNameBytes}-byte cumulative safety limit.");
            }

            var nameBytes = new byte[checked((int)nameLength)];
            ReadPayload(stream, origin, payloadOffset, payloadSize, cursor, nameBytes, "assembly name");
            cursor += nameLength;
            try
            {
                names[i] = strictUtf8.GetString(nameBytes);
            }
            catch (DecoderFallbackException exception)
            {
                throw new AssemblyStoreFormatException($"Descriptor {i} contains an invalid UTF-8 assembly name: {exception.Message}");
            }

            if (names[i].IndexOf('\0') >= 0)
            {
                throw new AssemblyStoreFormatException($"Descriptor {i} assembly name contains a NUL character.");
            }
        }

        var metadataEnd = cursor;
        for (var i = 0; i < descriptors.Length; i++)
        {
            var descriptor = descriptors[i];
            ValidateRangeStartsAfterMetadata(descriptor.DataOffset, descriptor.DataSize, metadataEnd, $"descriptor {i} assembly data");
            ValidateRangeStartsAfterMetadata(descriptor.DebugOffset, descriptor.DebugSize, metadataEnd, $"descriptor {i} debug data");
            ValidateRangeStartsAfterMetadata(descriptor.ConfigOffset, descriptor.ConfigSize, metadataEnd, $"descriptor {i} config data");
        }

        var items = new List<AssemblyStoreItem>(descriptors.Length);
        for (var i = 0; i < descriptors.Length; i++)
        {
            if (!seenDescriptors[i])
            {
                throw new AssemblyStoreFormatException($"Descriptor {i} is not referenced by the AssemblyStore index.");
            }

            var descriptor = descriptors[i];
            items.Add(new AssemblyStoreItem(
                (uint)i,
                names[i],
                abi,
                descriptor.DataOffset,
                descriptor.DataSize,
                descriptor.DebugOffset,
                descriptor.DebugSize,
                descriptor.ConfigOffset,
                descriptor.ConfigSize,
                (hashLists[i] ?? []).AsReadOnly()));
        }

        return new ParsedStore(payloadOffset, payloadSize, version, abi, items.AsReadOnly(), indexEntryCount);
    }

    private static (long Offset, long Size, ushort Machine) FindElf64Payload(
        Stream stream,
        long origin,
        long length,
        string sourceName)
    {
        if (length < 64)
        {
            throw new AssemblyStoreFormatException($"ELF wrapper '{sourceName}' is shorter than the ELF64 header.");
        }

        Span<byte> header = stackalloc byte[64];
        ReadExactlyAt(stream, origin, 0, header);
        if (header[4] != 2)
        {
            throw new AssemblyStoreFormatException($"ELF wrapper '{sourceName}' has class {header[4]}; only ELF64 is supported.");
        }

        if (header[5] != 1)
        {
            throw new AssemblyStoreFormatException($"ELF wrapper '{sourceName}' is not little-endian.");
        }

        if (header[6] != 1)
        {
            throw new AssemblyStoreFormatException($"ELF wrapper '{sourceName}' has unsupported ELF identification version {header[6]}.");
        }

        var type = BinaryPrimitives.ReadUInt16LittleEndian(header[16..]);
        if (type != 3)
        {
            throw new AssemblyStoreFormatException($"ELF wrapper '{sourceName}' has type {type}; expected a shared object (ET_DYN)." );
        }

        var machine = BinaryPrimitives.ReadUInt16LittleEndian(header[18..]);
        var sectionHeaderOffset = BinaryPrimitives.ReadUInt64LittleEndian(header[40..]);
        var elfHeaderSize = BinaryPrimitives.ReadUInt16LittleEndian(header[52..]);
        var sectionHeaderEntrySize = BinaryPrimitives.ReadUInt16LittleEndian(header[58..]);
        var sectionHeaderCount = BinaryPrimitives.ReadUInt16LittleEndian(header[60..]);
        var stringTableIndex = BinaryPrimitives.ReadUInt16LittleEndian(header[62..]);

        if (elfHeaderSize < 64 || sectionHeaderEntrySize < 64)
        {
            throw new AssemblyStoreFormatException($"ELF wrapper '{sourceName}' has invalid ELF64 header sizes.");
        }

        if (sectionHeaderCount == 0 || stringTableIndex == 0xFFFF || stringTableIndex >= sectionHeaderCount)
        {
            throw new AssemblyStoreFormatException(
                $"ELF wrapper '{sourceName}' uses unsupported extended or invalid section table indexes.");
        }

        var sectionTableSize = checked((ulong)sectionHeaderEntrySize * sectionHeaderCount);
        ValidateFileRange(sectionHeaderOffset, sectionTableSize, length, "ELF section header table");

        Span<byte> sectionHeader = stackalloc byte[64];
        var stringHeaderOffset = checked(sectionHeaderOffset + ((ulong)stringTableIndex * sectionHeaderEntrySize));
        ReadExactlyAt(stream, origin, checked((long)stringHeaderOffset), sectionHeader);
        var stringTableOffset = BinaryPrimitives.ReadUInt64LittleEndian(sectionHeader[24..]);
        var stringTableSize = BinaryPrimitives.ReadUInt64LittleEndian(sectionHeader[32..]);
        if (stringTableSize == 0 || stringTableSize > MaximumElfStringTableBytes)
        {
            throw new AssemblyStoreFormatException(
                $"ELF section-name table size {stringTableSize} is outside the supported range 1..{MaximumElfStringTableBytes}.");
        }

        ValidateFileRange(stringTableOffset, stringTableSize, length, "ELF section-name table");
        var names = new byte[checked((int)stringTableSize)];
        ReadExactlyAt(stream, origin, checked((long)stringTableOffset), names);

        for (var i = 0; i < sectionHeaderCount; i++)
        {
            var currentOffset = checked(sectionHeaderOffset + ((ulong)i * sectionHeaderEntrySize));
            ReadExactlyAt(stream, origin, checked((long)currentOffset), sectionHeader);
            var nameOffset = BinaryPrimitives.ReadUInt32LittleEndian(sectionHeader);
            if (nameOffset >= names.Length)
            {
                throw new AssemblyStoreFormatException($"ELF section {i} name offset {nameOffset} is outside the section-name table.");
            }

            var nameEnd = Array.IndexOf(names, (byte)0, (int)nameOffset);
            if (nameEnd < 0)
            {
                throw new AssemblyStoreFormatException($"ELF section {i} name is not NUL-terminated.");
            }

            if (!names.AsSpan((int)nameOffset, nameEnd - (int)nameOffset).SequenceEqual("payload"u8))
            {
                continue;
            }

            var payloadOffset = BinaryPrimitives.ReadUInt64LittleEndian(sectionHeader[24..]);
            var payloadSize = BinaryPrimitives.ReadUInt64LittleEndian(sectionHeader[32..]);
            ValidateFileRange(payloadOffset, payloadSize, length, "ELF payload section");
            return (checked((long)payloadOffset), checked((long)payloadSize), machine);
        }

        throw new AssemblyStoreFormatException($"ELF wrapper '{sourceName}' does not contain a 'payload' section.");
    }

    private static void ValidateFileRange(ulong offset, ulong size, long fileLength, string description)
    {
        if (offset > (ulong)fileLength || size > (ulong)fileLength - offset)
        {
            throw new AssemblyStoreFormatException(
                $"{description} range [{offset}, {offset + size}) exceeds file length {fileLength}.");
        }
    }

    private static void ValidatePayloadRange(uint offset, uint size, long payloadSize, string description)
    {
        if ((ulong)offset > (ulong)payloadSize || (ulong)size > (ulong)payloadSize - offset)
        {
            throw new AssemblyStoreFormatException(
                $"{description} range [{offset}, {(ulong)offset + size}) exceeds payload length {payloadSize}.");
        }
    }

    private static void ValidateRangeStartsAfterMetadata(uint offset, uint size, long metadataEnd, string description)
    {
        if (size != 0 && offset < metadataEnd)
        {
            throw new AssemblyStoreFormatException(
                $"{description} starts at {offset}, before metadata ends at {metadataEnd}.");
        }
    }

    private static void ReadPayload(
        Stream stream,
        long origin,
        long payloadOffset,
        long payloadSize,
        long relativeOffset,
        Span<byte> destination,
        string description)
    {
        if (relativeOffset < 0 || relativeOffset > payloadSize || destination.Length > payloadSize - relativeOffset)
        {
            throw new AssemblyStoreFormatException(
                $"{description} at payload offset {relativeOffset} with size {destination.Length} exceeds payload length {payloadSize}.");
        }

        ReadExactlyAt(stream, origin, checked(payloadOffset + relativeOffset), destination);
    }

    private static void ReadExactlyAt(Stream stream, long origin, long relativeOffset, Span<byte> destination)
    {
        stream.Position = checked(origin + relativeOffset);
        stream.ReadExactly(destination);
    }

    private static Stream SpoolToTemporaryFile(Stream input)
    {
        var path = Path.Combine(Path.GetTempPath(), $"junimogate-store-{Guid.NewGuid():N}.tmp");
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
                {
                    break;
                }

                total = checked(total + read);
                if (total > MaximumStoreSize)
                {
                    throw new AssemblyStoreFormatException(
                        $"Assembly store stream exceeds the {MaximumStoreSize}-byte safety limit.");
                }

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
                // Best effort only; DeleteOnClose handles the normal path.
            }

            throw;
        }
    }

    private static async ValueTask ReadExactlyAsync(
        Stream source,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        while (!buffer.IsEmpty)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException("Unexpected end of AssemblyStore entry data.");
            }

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
            var wanted = (int)Math.Min(buffer.Length, remaining);
            var read = await source.ReadAsync(buffer.AsMemory(0, wanted), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException("Unexpected end of AssemblyStore entry data.");
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            remaining -= read;
        }
    }

    private sealed record ParsedStore(
        long PayloadOffset,
        long PayloadSize,
        uint RawVersion,
        string Abi,
        IReadOnlyList<AssemblyStoreItem> Items,
        uint IndexEntryCount);

    private readonly record struct Descriptor(
        uint MappingIndex,
        uint DataOffset,
        uint DataSize,
        uint DebugOffset,
        uint DebugSize,
        uint ConfigOffset,
        uint ConfigSize);
}
