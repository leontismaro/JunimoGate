using System.Buffers.Binary;

const string FieldAccessSymbol = "mono_method_can_access_field";
const string MethodAccessSymbol = "mono_method_can_access_method";
ReadOnlySpan<byte> allowAccess = [
    0x20, 0x00, 0x80, 0x52, // mov w0, #1
    0xc0, 0x03, 0x5f, 0xd6, // ret
];

if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: JunimoGate.RuntimePack <stock-libmonosgen-2.0.so> <patched-libmonosgen-2.0.so>");
    return 2;
}

string sourcePath = Path.GetFullPath(args[0]);
string destinationPath = Path.GetFullPath(args[1]);
if (!File.Exists(sourcePath))
    throw new FileNotFoundException("The stock Android Mono runtime was not found.", sourcePath);
if (StringComparer.Ordinal.Equals(sourcePath, destinationPath))
    throw new InvalidOperationException("The patched runtime must not overwrite the SDK runtime pack.");

byte[] image = File.ReadAllBytes(sourcePath);
Elf64Arm64Image elf = new(image);
foreach (string symbolName in new[] { FieldAccessSymbol, MethodAccessSymbol })
{
    ElfSymbol symbol = elf.FindFunction(symbolName);
    if (symbol.Size < (ulong)allowAccess.Length)
        throw new InvalidDataException($"ELF symbol '{symbolName}' is too small to patch safely.");

    int fileOffset = elf.MapVirtualAddressToFileOffset(symbol.VirtualAddress, allowAccess.Length);
    allowAccess.CopyTo(image.AsSpan(fileOffset, allowAccess.Length));
    Console.WriteLine($"Patched {symbolName} at ELF file offset 0x{fileOffset:x}.");
}

string? destinationDirectory = Path.GetDirectoryName(destinationPath);
if (string.IsNullOrEmpty(destinationDirectory))
    throw new InvalidOperationException("The destination must have a parent directory.");
Directory.CreateDirectory(destinationDirectory);

string temporaryPath = Path.Combine(destinationDirectory, $".{Path.GetFileName(destinationPath)}.{Guid.NewGuid():N}.tmp");
try
{
    File.WriteAllBytes(temporaryPath, image);
    File.Move(temporaryPath, destinationPath, overwrite: true);
}
finally
{
    if (File.Exists(temporaryPath))
        File.Delete(temporaryPath);
}

Console.WriteLine($"Wrote application-local Mono runtime: {destinationPath}");
return 0;

internal readonly record struct ElfSection(uint Type, ulong Offset, ulong Size, uint Link, ulong EntrySize);
internal readonly record struct ElfProgramHeader(uint Type, ulong Offset, ulong VirtualAddress, ulong FileSize);
internal readonly record struct ElfSymbol(string Name, ulong VirtualAddress, ulong Size);

internal sealed class Elf64Arm64Image
{
    private const int ElfHeaderSize = 64;
    private const uint LoadSegment = 1;
    private const uint DynamicSymbols = 11;
    private const byte FunctionSymbol = 2;
    private readonly byte[] image;
    private readonly ElfSection[] sections;
    private readonly ElfProgramHeader[] programHeaders;

    public Elf64Arm64Image(byte[] image)
    {
        this.image = image;
        if (image.Length < ElfHeaderSize
            || !image.AsSpan(0, 4).SequenceEqual("\u007fELF"u8)
            || image[4] != 2
            || image[5] != 1
            || image[6] != 1
            || ReadUInt16(18) != 183
            || ReadUInt32(20) != 1)
        {
            throw new InvalidDataException("Mono runtime input must be ELF64 little-endian AArch64.");
        }

        ulong programHeaderOffset = ReadUInt64(32);
        ulong sectionHeaderOffset = ReadUInt64(40);
        ushort programHeaderEntrySize = ReadUInt16(54);
        ushort programHeaderCount = ReadUInt16(56);
        ushort sectionHeaderEntrySize = ReadUInt16(58);
        ushort sectionHeaderCount = ReadUInt16(60);

        if (programHeaderEntrySize < 56 || programHeaderCount == 0)
            throw new InvalidDataException("ELF program header table is missing or invalid.");
        if (sectionHeaderEntrySize < 64 || sectionHeaderCount == 0)
            throw new InvalidDataException("ELF section header table is missing or invalid.");

        ValidateTable(programHeaderOffset, programHeaderEntrySize, programHeaderCount, "program header");
        ValidateTable(sectionHeaderOffset, sectionHeaderEntrySize, sectionHeaderCount, "section header");

        programHeaders = new ElfProgramHeader[programHeaderCount];
        for (int index = 0; index < programHeaders.Length; index++)
        {
            int offset = CheckedOffset(programHeaderOffset + (ulong)index * programHeaderEntrySize, 56, "program header");
            programHeaders[index] = new ElfProgramHeader(
                ReadUInt32(offset),
                ReadUInt64(offset + 8),
                ReadUInt64(offset + 16),
                ReadUInt64(offset + 32));
        }

        sections = new ElfSection[sectionHeaderCount];
        for (int index = 0; index < sections.Length; index++)
        {
            int offset = CheckedOffset(sectionHeaderOffset + (ulong)index * sectionHeaderEntrySize, 64, "section header");
            sections[index] = new ElfSection(
                ReadUInt32(offset + 4),
                ReadUInt64(offset + 24),
                ReadUInt64(offset + 32),
                ReadUInt32(offset + 40),
                ReadUInt64(offset + 56));
        }
    }

    public ElfSymbol FindFunction(string expectedName)
    {
        List<ElfSymbol> matches = [];
        foreach (ElfSection symbolTable in sections.Where(section => section.Type == DynamicSymbols))
        {
            if (symbolTable.Link >= (uint)sections.Length)
                throw new InvalidDataException("ELF dynamic symbol string-table link is invalid.");
            if (symbolTable.EntrySize < 24 || symbolTable.Size % symbolTable.EntrySize != 0)
                throw new InvalidDataException("ELF dynamic symbol table shape is invalid.");

            ElfSection strings = sections[(int)symbolTable.Link];
            ReadOnlySpan<byte> stringData = Slice(strings.Offset, strings.Size, "dynamic string table");
            ulong symbolCount = symbolTable.Size / symbolTable.EntrySize;
            for (ulong index = 0; index < symbolCount; index++)
            {
                ulong entryAddress = symbolTable.Offset + index * symbolTable.EntrySize;
                int entryOffset = CheckedOffset(entryAddress, 24, "dynamic symbol");
                if ((image[entryOffset + 4] & 0x0f) != FunctionSymbol)
                    continue;

                uint nameOffset = ReadUInt32(entryOffset);
                string name = ReadNullTerminatedAscii(stringData, nameOffset);
                if (!StringComparer.Ordinal.Equals(name, expectedName))
                    continue;

                ushort sectionIndex = ReadUInt16(entryOffset + 6);
                ulong virtualAddress = ReadUInt64(entryOffset + 8);
                ulong size = ReadUInt64(entryOffset + 16);
                if (sectionIndex == 0 || virtualAddress == 0)
                    throw new InvalidDataException($"ELF symbol '{expectedName}' is undefined.");
                matches.Add(new ElfSymbol(name, virtualAddress, size));
            }
        }

        if (matches.Count != 1)
            throw new InvalidDataException($"Expected exactly one defined ELF function named '{expectedName}', found {matches.Count}.");
        return matches[0];
    }

    public int MapVirtualAddressToFileOffset(ulong virtualAddress, int requiredLength)
    {
        foreach (ElfProgramHeader header in programHeaders.Where(header => header.Type == LoadSegment))
        {
            if (virtualAddress < header.VirtualAddress)
                continue;
            ulong delta = virtualAddress - header.VirtualAddress;
            if (delta > header.FileSize || (ulong)requiredLength > header.FileSize - delta)
                continue;
            return CheckedOffset(header.Offset + delta, requiredLength, "load segment");
        }

        throw new InvalidDataException($"ELF virtual address 0x{virtualAddress:x} is not backed by a PT_LOAD file range.");
    }

    private void ValidateTable(ulong offset, ushort entrySize, ushort count, string label)
    {
        ulong size = (ulong)entrySize * count;
        _ = Slice(offset, size, label);
    }

    private ReadOnlySpan<byte> Slice(ulong offset, ulong size, string label)
    {
        if (offset > (ulong)image.Length || size > (ulong)image.Length - offset || size > int.MaxValue)
            throw new InvalidDataException($"ELF {label} exceeds file bounds.");
        return image.AsSpan((int)offset, (int)size);
    }

    private int CheckedOffset(ulong offset, int size, string label)
    {
        _ = Slice(offset, (ulong)size, label);
        return checked((int)offset);
    }

    private static string ReadNullTerminatedAscii(ReadOnlySpan<byte> strings, uint offset)
    {
        if (offset >= strings.Length)
            throw new InvalidDataException("ELF dynamic symbol name exceeds string-table bounds.");
        ReadOnlySpan<byte> remainder = strings[(int)offset..];
        int length = remainder.IndexOf((byte)0);
        if (length < 0)
            throw new InvalidDataException("ELF dynamic symbol name is not null-terminated.");
        foreach (byte value in remainder[..length])
        {
            if (value > 0x7f)
                throw new InvalidDataException("ELF dynamic symbol name is not ASCII.");
        }
        return System.Text.Encoding.ASCII.GetString(remainder[..length]);
    }

    private ushort ReadUInt16(int offset) => BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(offset, 2));
    private uint ReadUInt32(int offset) => BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(offset, 4));
    private ulong ReadUInt64(int offset) => BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan(offset, 8));
}
