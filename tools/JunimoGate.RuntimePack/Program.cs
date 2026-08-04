using System.Buffers.Binary;

const string FieldAccessSymbol = "mono_method_can_access_field";
const string MethodAccessSymbol = "mono_method_can_access_method";
const string GuidFormatterSymbol = "mono_guid_to_string_minimal";
const string ReflectionTypeSymbol = "mono_class_from_mono_type_internal";
ReadOnlySpan<byte> allowAccess = [
    0x20, 0x00, 0x80, 0x52, // mov w0, #1
    0xc0, 0x03, 0x5f, 0xd6, // ret
];

if (args is ["--self-test"])
{
    RunSelfTests();
    return 0;
}

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

PatchNullGuidFormatting(image, elf, GuidFormatterSymbol);
PatchInvalidReflectionTypeFallback(image, elf, ReflectionTypeSymbol);

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

static void PatchNullGuidFormatting(byte[] image, Elf64Arm64Image elf, string symbolName)
{
    // Android requests a Mono thread dump when the UI thread crosses the ANR timeout.
    // Reflection.Emit assemblies have no metadata GUID heap, but Mono's stack-frame
    // formatter passes that null pointer to this function. Keep the formatter's normal
    // output unchanged and substitute a zero GUID only for that diagnostic-only case.
    ElfSymbol symbol = elf.FindFunction(symbolName);
    const int FunctionSize = 0x88;
    if (symbol.Size != FunctionSize)
    {
        throw new InvalidDataException(
            $"ELF symbol '{symbolName}' has unsupported size 0x{symbol.Size:x}; expected 0x{FunctionSize:x}. " +
            "Review the Mono runtime update before changing the patch recipe.");
    }

    int fileOffset = elf.MapVirtualAddressToFileOffset(symbol.VirtualAddress, FunctionSize);
    Span<byte> function = image.AsSpan(fileOffset, FunctionSize);
    uint[] original = ReadInstructions(function);
    ValidateGuidFormatterRecipe(original, symbolName);

    long printfTarget = DecodeBranchTarget(original[30], checked((long)symbol.VirtualAddress + 30 * 4));
    uint[] patched =
    [
        0xd101c3ff, // sub sp, sp, #0x70
        0xa9067bfd, // stp x29, x30, [sp, #0x60]
        0x910183fd, // add x29, sp, #0x60
        0xb5000060, // cbnz x0, +0xc
        0xa9057fff, // stp xzr, xzr, [sp, #0x50]
        0x910143e0, // add x0, sp, #0x50
        original[3],
        original[4],
        original[5],
        original[6],
        original[7],
        original[8],
        original[9],
        original[10],
        original[11],
        original[12],
        original[13],
        original[14],
        original[15],
        original[16],
        original[17],
        original[18],
        original[19], // original ADRP for the GUID format string; same 4K code page
        original[20], // original ADD for the GUID format string
        0xa90027e8, // stp x8, x9, [sp]
        0xa9012fea, // stp x10, x11, [sp, #0x10]
        0xa90237ec, // stp x12, x13, [sp, #0x20]
        0xa9033fee, // stp x14, x15, [sp, #0x30]
        0xf90023f0, // str x16, [sp, #0x40]
        EncodeBranchLink(checked((long)symbol.VirtualAddress + 29 * 4), printfTarget),
        0xa9467bfd, // ldp x29, x30, [sp, #0x60]
        0x9101c3ff, // add sp, sp, #0x70
        0xd65f03c0, // ret
        0xd503201f, // nop
    ];

    WriteInstructions(function, patched);
    if (!ReadInstructions(function).SequenceEqual(patched))
        throw new InvalidDataException($"ELF symbol '{symbolName}' did not retain the expected patched body.");

    Console.WriteLine($"Patched {symbolName} null-GUID handling at ELF file offset 0x{fileOffset:x}.");
}

static void PatchInvalidReflectionTypeFallback(byte[] image, Elf64Arm64Image elf, string symbolName)
{
    // Some Reflection.Emit methods expose a zero-initialized MonoType while
    // RuntimeMethodInfo.GetParameters() inspects their generated signature. Mono's
    // default switch arm aborts the process for that value. Preserve every normal
    // type case and return the fallback class already held by this function only for
    // the otherwise-fatal default arm.
    const int FunctionSize = 0x24c;
    ElfSymbol symbol = elf.FindFunction(symbolName);
    if (symbol.Size != FunctionSize)
    {
        throw new InvalidDataException(
            $"ELF symbol '{symbolName}' has unsupported size 0x{symbol.Size:x}; expected 0x{FunctionSize:x}. " +
            "Review the Mono runtime update before changing the patch recipe.");
    }

    int fileOffset = elf.MapVirtualAddressToFileOffset(symbol.VirtualAddress, FunctionSize);
    Span<byte> function = image.AsSpan(fileOffset, FunctionSize);
    PatchInvalidReflectionTypeFallbackFunction(function, symbolName);

    Console.WriteLine($"Patched {symbolName} invalid Reflection.Emit type fallback at ELF file offset 0x{fileOffset + 0x23c:x}.");
}

static void PatchInvalidReflectionTypeFallbackFunction(Span<byte> function, string symbolName)
{
    const int FunctionSize = 0x24c;
    const int PatchOffset = 0x23c;
    if (function.Length != FunctionSize)
    {
        throw new InvalidDataException(
            $"ELF symbol '{symbolName}' has unsupported size 0x{function.Length:x}; expected 0x{FunctionSize:x}. " +
            "Review the Mono runtime update before changing the patch recipe.");
    }

    uint[] original = ReadInstructions(function);
    int patchIndex = PatchOffset / 4;
    uint[] expectedTail =
    [
        0xaa1f03e0, // mov x0, xzr
        0x52800201, // mov w1, #0x10
        0,          // bl g_strdup_printf
        0,          // adrp x0, assertion-message page
        0x911cd800, // add x0, x0, assertion-message offset
        0x52812281, // mov w1, #0x914
        0,          // bl monoeg_g_log
    ];
    int tailStart = patchIndex - 3;
    for (int index = 0; index < expectedTail.Length; index++)
    {
        uint instruction = original[tailStart + index];
        bool matches = index switch
        {
            2 or 6 => (instruction & 0xfc000000) == 0x94000000,
            3 => (instruction & 0x9f00001f) == 0x90000000,
            _ => instruction == expectedTail[index],
        };
        if (!matches)
        {
            throw new InvalidDataException(
                $"ELF symbol '{symbolName}' no longer matches the supported Mono recipe at instruction " +
                $"{tailStart + index} (0x{instruction:x8}). Review the runtime update before patching it.");
        }
    }

    uint[] fallback =
    [
        0xf100011f, // cmp x8, #0
        0x9a880120, // csel x0, x9, x8, eq
        0xa8c17bfd, // ldp x29, x30, [sp], #0x10
        0xd65f03c0, // ret
    ];
    WriteInstructions(function.Slice(PatchOffset, fallback.Length * 4), fallback);
    if (!ReadInstructions(function.Slice(PatchOffset, fallback.Length * 4)).SequenceEqual(fallback))
        throw new InvalidDataException($"ELF symbol '{symbolName}' did not retain the expected fallback body.");
}

static void RunSelfTests()
{
    const int FunctionSize = 0x24c;
    const int PatchOffset = 0x23c;
    const int PatchIndex = PatchOffset / 4;
    const int TailStart = PatchIndex - 3;
    uint[] expectedTail =
    [
        0xaa1f03e0,
        0x52800201,
        0x94000000,
        0x90000000,
        0x911cd800,
        0x52812281,
        0x94000000,
    ];

    byte[] supportedFunction = new byte[FunctionSize];
    WriteInstructions(supportedFunction.AsSpan(TailStart * 4, expectedTail.Length * 4), expectedTail);
    PatchInvalidReflectionTypeFallbackFunction(supportedFunction, ReflectionTypeSymbol);

    uint[] expectedFallback = [0xf100011f, 0x9a880120, 0xa8c17bfd, 0xd65f03c0];
    if (!ReadInstructions(supportedFunction.AsSpan(PatchOffset, expectedFallback.Length * 4)).SequenceEqual(expectedFallback))
        throw new InvalidDataException("Reflection.Emit fallback self-test did not produce the expected ARM64 instructions.");

    byte[] changedRecipe = new byte[FunctionSize];
    WriteInstructions(changedRecipe.AsSpan(TailStart * 4, expectedTail.Length * 4), expectedTail);
    BinaryPrimitives.WriteUInt32LittleEndian(changedRecipe.AsSpan(TailStart * 4, 4), 0xd503201f);
    ExpectInvalidData(() => PatchInvalidReflectionTypeFallbackFunction(changedRecipe, ReflectionTypeSymbol));
    ExpectInvalidData(() => PatchInvalidReflectionTypeFallbackFunction(new byte[FunctionSize - 4], ReflectionTypeSymbol));

    Console.WriteLine("RuntimePack self-test passed: supported Reflection.Emit recipe patches exactly; changed recipes fail closed.");
}

static void ExpectInvalidData(Action action)
{
    try
    {
        action();
    }
    catch (InvalidDataException)
    {
        return;
    }

    throw new InvalidDataException("RuntimePack self-test expected an InvalidDataException.");
}

static void ValidateGuidFormatterRecipe(IReadOnlyList<uint> instructions, string symbolName)
{
    uint[] expected =
    [
        0xd10183ff, 0xa9057bfd, 0x910143fd,
        0x39400c01, 0x39400802, 0x39400403, 0x39400004,
        0x39401405, 0x39401006, 0x39401c07, 0x39401808,
        0x39402009, 0x3940240a, 0x3940280b, 0x39402c0c,
        0x3940300d, 0x3940340e, 0x3940380f, 0x39403c10,
        0, 0,
        0xb9003bef, 0xb90043f0, 0xb90033ee, 0xb9002bed,
        0xb90023ec, 0xb9001beb, 0xb90013ea, 0xb9000be9, 0xb90003e8,
        0, 0xa9457bfd, 0x910183ff, 0xd65f03c0,
    ];

    if (instructions.Count != expected.Length)
        throw new InvalidDataException($"ELF symbol '{symbolName}' has an unsupported instruction count.");

    for (int index = 0; index < expected.Length; index++)
    {
        uint instruction = instructions[index];
        bool matches = index switch
        {
            19 => (instruction & 0x9f00001f) == 0x90000000, // adrp x0, format-page
            20 => (instruction & 0xffc003ff) == 0x91000000, // add x0, x0, format-offset
            30 => (instruction & 0xfc000000) == 0x94000000, // bl g_strdup_printf
            _ => instruction == expected[index],
        };
        if (!matches)
        {
            throw new InvalidDataException(
                $"ELF symbol '{symbolName}' no longer matches the supported Mono recipe at instruction {index} " +
                $"(0x{instruction:x8}). Review the runtime update before patching it.");
        }
    }
}

static uint[] ReadInstructions(ReadOnlySpan<byte> bytes)
{
    if (bytes.Length % 4 != 0)
        throw new InvalidDataException("AArch64 function size is not instruction-aligned.");
    var instructions = new uint[bytes.Length / 4];
    for (int index = 0; index < instructions.Length; index++)
        instructions[index] = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(index * 4, 4));
    return instructions;
}

static void WriteInstructions(Span<byte> destination, IReadOnlyList<uint> instructions)
{
    if (destination.Length != instructions.Count * 4)
        throw new InvalidDataException("Patched AArch64 function size changed unexpectedly.");
    for (int index = 0; index < instructions.Count; index++)
        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(index * 4, 4), instructions[index]);
}

static long DecodeBranchTarget(uint instruction, long instructionAddress)
{
    long immediate = instruction & 0x03ff_ffff;
    if ((immediate & (1L << 25)) != 0)
        immediate -= 1L << 26;
    return checked(instructionAddress + immediate * 4);
}

static uint EncodeBranchLink(long instructionAddress, long targetAddress)
{
    long delta = checked(targetAddress - instructionAddress);
    if ((delta & 3) != 0 || delta < -(1L << 27) || delta >= (1L << 27))
        throw new InvalidDataException("AArch64 branch target is outside the encodable BL range.");
    return 0x9400_0000u | ((uint)(delta / 4) & 0x03ff_ffffu);
}

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
