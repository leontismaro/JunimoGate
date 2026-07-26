using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using JunimoGate.Core;

namespace JunimoGate.Extraction;

/// <summary>Stable diagnostics produced by the metadata-only selected-ABI native inventory.</summary>
public static class NativeEntryInventoryErrorCodes
{
    public const string TrustPlanMismatch = "gamehost_probe_native_trust_plan_mismatch";
    public const string CertificateBlocked = "gamehost_probe_native_certificate_blocked";
    public const string SourceIdentityMismatch = "gamehost_probe_native_source_identity_mismatch";
    public const string InvalidArchive = "gamehost_probe_native_archive_invalid";
    public const string UnsafeEntry = "gamehost_probe_native_entry_unsafe";
    public const string DuplicateEntry = "gamehost_probe_native_entry_duplicate";
    public const string LimitsExceeded = "gamehost_probe_native_limits_exceeded";
    public const string InvalidElf = "gamehost_probe_native_elf_invalid";
    public const string NoEntries = "gamehost_probe_native_entries_missing";
    public const string Cancelled = "gamehost_probe_native_cancelled";
}

public enum NativeEntryInventoryStatus
{
    Succeeded,
    Failed,
    Cancelled,
}

/// <summary>Bounds metadata-only native entry scanning even for a trusted but malformed APK.</summary>
public sealed record NativeEntryInventoryLimits
{
    public int MaximumArchiveEntries { get; init; } = 200_000;
    public int MaximumSelectedEntries { get; init; } = 4_096;
    public long MaximumEntryBytes { get; init; } = 1024L * 1024 * 1024;
    public long MaximumTotalBytes { get; init; } = 4L * 1024 * 1024 * 1024;
    public double MaximumCompressionRatio { get; init; } = 500;
    public int MaximumEntryPathBytes { get; init; } = 1_024;

    internal void Validate()
    {
        if (MaximumArchiveEntries <= 0 || MaximumSelectedEntries <= 0 || MaximumEntryBytes <= 0 ||
            MaximumTotalBytes <= 0 || MaximumCompressionRatio <= 0 || MaximumEntryPathBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(NativeEntryInventoryLimits), "Native inventory limits must be positive.");
        }
    }
}

/// <summary>Bounded ELF header identity. No section, symbol, or native code bytes are retained.</summary>
public sealed record NativeElfIdentity(
    int ElfClass,
    int DataEncoding,
    int IdentVersion,
    int OsAbi,
    int AbiVersion,
    int ObjectType,
    int Machine,
    uint Flags);

/// <summary>Redacted identity for one selected-ABI APK native entry.</summary>
public sealed record NativeEntryEvidence(
    string SourceLabel,
    string EntryPath,
    long Size,
    long CompressedSize,
    string Sha256,
    NativeElfIdentity Elf);

public sealed class NativeEntryInventoryResult
{
    internal NativeEntryInventoryResult(
        NativeEntryInventoryStatus status,
        string selectedAbi,
        IEnumerable<NativeEntryEvidence> entries,
        IEnumerable<DiagnosticRecord> diagnostics)
    {
        Status = status;
        SelectedAbi = selectedAbi;
        Entries = Array.AsReadOnly(entries.ToArray());
        Diagnostics = Array.AsReadOnly(diagnostics.ToArray());
    }

    public NativeEntryInventoryStatus Status { get; }
    public string SelectedAbi { get; }
    public ReadOnlyCollection<NativeEntryEvidence> Entries { get; }
    public ReadOnlyCollection<DiagnosticRecord> Diagnostics { get; }
    public bool IsSuccess => Status == NativeEntryInventoryStatus.Succeeded;
}

/// <summary>
/// Re-hashes each installed APK and inventories selected-ABI ELF entries through the same open handle.
/// It never extracts or loads a native library.
/// </summary>
public sealed class NativeEntryInventoryProbe
{
    private const ushort ElfMachineAArch64 = 183;

    public async ValueTask<NativeEntryInventoryResult> ProbeAsync(
        ValidatedExecutionPlan executionPlan,
        GameInstallationCandidate liveCandidate,
        NativeEntryInventoryLimits? limits = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(executionPlan);
        ArgumentNullException.ThrowIfNull(liveCandidate);
        limits ??= new NativeEntryInventoryLimits();
        limits.Validate();

        var installation = liveCandidate.Installation;
        var diagnostics = new List<DiagnosticRecord>();
        try
        {
            if (!KnownGameCertificate.Verify(installation.PackageName, installation.SigningIdentity).AllowsCodeExecution)
            {
                return Failure(
                    installation.SelectedAbi,
                    diagnostics,
                    NativeEntryInventoryErrorCodes.CertificateBlocked,
                    "The live installation certificate is not trusted for native metadata inspection.");
            }

            if (!WorkspaceExecutionValidator.MatchesGate0Identity(executionPlan, liveCandidate))
            {
                return Failure(
                    installation.SelectedAbi,
                    diagnostics,
                    NativeEntryInventoryErrorCodes.TrustPlanMismatch,
                    "The execution plan does not match the live package identity.");
            }

            if (!installation.SelectedAbi.Equals("arm64-v8a", StringComparison.Ordinal))
            {
                return Failure(
                    installation.SelectedAbi,
                    diagnostics,
                    NativeEntryInventoryErrorCodes.InvalidElf,
                    "The selected ABI does not have a supported ELF identity policy.");
            }

            var entries = new List<NativeEntryEvidence>();
            var sourcePaths = new HashSet<string>(StringComparer.Ordinal);
            var collisionPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            long totalBytes = 0;
            var archiveEntryCount = 0;

            foreach (var source in installation.ApkSources)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await using var stream = OpenSource(source.SourcePath);
                if (stream.Length != source.Size)
                {
                    return Failure(
                        installation.SelectedAbi,
                        diagnostics,
                        NativeEntryInventoryErrorCodes.SourceIdentityMismatch,
                        "An APK source size changed before native metadata inspection.");
                }

                var sourceHash = Convert.ToHexStringLower(
                    await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
                if (!sourceHash.Equals(source.Digest.Value, StringComparison.Ordinal))
                {
                    return Failure(
                        installation.SelectedAbi,
                        diagnostics,
                        NativeEntryInventoryErrorCodes.SourceIdentityMismatch,
                        "An APK source digest changed before native metadata inspection.");
                }

                stream.Position = 0;
                using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
                foreach (var entry in archive.Entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    archiveEntryCount = checked(archiveEntryCount + 1);
                    if (archiveEntryCount > limits.MaximumArchiveEntries)
                    {
                        return Failure(
                            installation.SelectedAbi,
                            diagnostics,
                            NativeEntryInventoryErrorCodes.LimitsExceeded,
                            "APK ZIP metadata exceeds the native inventory entry bound.");
                    }

                    if (!TryGetSelectedAbiEntry(entry.FullName, installation.SelectedAbi, out var canonicalPath))
                    {
                        continue;
                    }

                    if (!IsSafeSelectedAbiEntry(entry, canonicalPath, limits))
                    {
                        return Failure(
                            installation.SelectedAbi,
                            diagnostics,
                            NativeEntryInventoryErrorCodes.UnsafeEntry,
                            "A selected-ABI native ZIP entry has an unsafe path or file type.");
                    }

                    var sourcePathKey = $"{source.Label}\0{canonicalPath}";
                    if (!sourcePaths.Add(sourcePathKey) ||
                        (collisionPaths.TryGetValue(canonicalPath, out var priorPath) &&
                            !priorPath.Equals(canonicalPath, StringComparison.Ordinal)))
                    {
                        return Failure(
                            installation.SelectedAbi,
                            diagnostics,
                            NativeEntryInventoryErrorCodes.DuplicateEntry,
                            "Selected-ABI native ZIP entries collide by source path, Unicode, or case.");
                    }

                    collisionPaths.TryAdd(canonicalPath, canonicalPath);

                    if (entries.Count >= limits.MaximumSelectedEntries || entry.Length > limits.MaximumEntryBytes)
                    {
                        return Failure(
                            installation.SelectedAbi,
                            diagnostics,
                            NativeEntryInventoryErrorCodes.LimitsExceeded,
                            "Selected-ABI native entries exceed a configured count or size bound.");
                    }

                    var nextTotal = checked(totalBytes + entry.Length);
                    if (nextTotal > limits.MaximumTotalBytes || IsCompressionRatioExceeded(entry, limits.MaximumCompressionRatio))
                    {
                        return Failure(
                            installation.SelectedAbi,
                            diagnostics,
                            NativeEntryInventoryErrorCodes.LimitsExceeded,
                            "Selected-ABI native entries exceed total-size or compression-ratio bounds.");
                    }

                    var inspected = await InspectEntryAsync(entry, cancellationToken).ConfigureAwait(false);
                    if (inspected.BytesRead != entry.Length || !TryReadArm64Elf(inspected.Header, out var elf))
                    {
                        return Failure(
                            installation.SelectedAbi,
                            diagnostics,
                            NativeEntryInventoryErrorCodes.InvalidElf,
                            "A selected-ABI native entry is not a supported ARM64 ELF image.");
                    }

                    totalBytes = nextTotal;
                    entries.Add(new NativeEntryEvidence(
                        source.Label,
                        canonicalPath,
                        entry.Length,
                        entry.CompressedLength,
                        inspected.Sha256,
                        elf));
                }
            }

            if (entries.Count == 0)
            {
                return Failure(
                    installation.SelectedAbi,
                    diagnostics,
                    NativeEntryInventoryErrorCodes.NoEntries,
                    "No selected-ABI native ELF entries were found in the verified APK sources.");
            }

            var ordered = entries
                .OrderBy(static entry => entry.EntryPath, StringComparer.Ordinal)
                .ThenBy(static entry => entry.Sha256, StringComparer.Ordinal)
                .ThenBy(static entry => entry.Size)
                .ThenBy(static entry => entry.SourceLabel, StringComparer.Ordinal)
                .ToArray();
            diagnostics.Add(Diagnostic(
                "gamehost_probe_native_succeeded",
                DiagnosticSeverity.Information,
                "Selected-ABI native ELF metadata was inspected successfully."));
            return new NativeEntryInventoryResult(
                NativeEntryInventoryStatus.Succeeded,
                installation.SelectedAbi,
                ordered,
                diagnostics);
        }
        catch (OperationCanceledException)
        {
            diagnostics.Add(Diagnostic(
                NativeEntryInventoryErrorCodes.Cancelled,
                DiagnosticSeverity.Information,
                "Selected-ABI native metadata inspection was cancelled."));
            return new NativeEntryInventoryResult(
                NativeEntryInventoryStatus.Cancelled,
                installation.SelectedAbi,
                [],
                diagnostics);
        }
        catch (OverflowException)
        {
            return Failure(
                installation.SelectedAbi,
                diagnostics,
                NativeEntryInventoryErrorCodes.LimitsExceeded,
                "Selected-ABI native metadata exceeds numeric bounds.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or CryptographicException)
        {
            return Failure(
                installation.SelectedAbi,
                diagnostics,
                NativeEntryInventoryErrorCodes.InvalidArchive,
                "An APK source could not be safely read as bounded native ZIP metadata.");
        }
    }

    private static FileStream OpenSource(string path) =>
        new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

    private static bool TryGetSelectedAbiEntry(string path, string selectedAbi, out string canonicalPath)
    {
        canonicalPath = string.Empty;
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        var normalizedSeparators = path.Replace('\\', '/');
        var selectedPrefix = $"lib/{selectedAbi}/";
        var targetsSelectedAbi = normalizedSeparators.StartsWith(selectedPrefix, StringComparison.OrdinalIgnoreCase);
        if (!targetsSelectedAbi)
        {
            return false;
        }

        if (!normalizedSeparators.StartsWith(selectedPrefix, StringComparison.Ordinal) ||
            path.Contains('\\', StringComparison.Ordinal) || path.IndexOf('\0') >= 0 ||
            path.Any(char.IsControl) || !path.Equals(path.Normalize(NormalizationForm.FormC), StringComparison.Ordinal))
        {
            return true;
        }

        var segments = path.Split('/', StringSplitOptions.None);
        if (segments.Length != 3 ||
            !segments[0].Equals("lib", StringComparison.Ordinal) ||
            !segments[1].Equals(selectedAbi, StringComparison.Ordinal))
        {
            return true;
        }

        canonicalPath = path;
        return true;
    }

    private static bool IsSafeSelectedAbiEntry(
        ZipArchiveEntry entry,
        string canonicalPath,
        NativeEntryInventoryLimits limits)
    {
        if (string.IsNullOrEmpty(canonicalPath) ||
            Encoding.UTF8.GetByteCount(canonicalPath) > limits.MaximumEntryPathBytes ||
            entry.Length <= 0 || entry.CompressedLength < 0 ||
            IsSymlinkOrSpecialFile(entry.ExternalAttributes))
        {
            return false;
        }

        var fileName = canonicalPath[(canonicalPath.LastIndexOf('/') + 1)..];
        return fileName.EndsWith(".so", StringComparison.Ordinal) &&
            fileName.Length <= 255 &&
            fileName.All(static character =>
                character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '.' or '_' or '-' or '+');
    }

    private static bool IsSymlinkOrSpecialFile(int externalAttributes)
    {
        var unixMode = (externalAttributes >> 16) & 0xFFFF;
        if (unixMode == 0)
        {
            return false;
        }

        var fileType = unixMode & 0xF000;
        return fileType is not 0 and not 0x8000;
    }

    private static bool IsCompressionRatioExceeded(ZipArchiveEntry entry, double maximumRatio)
    {
        if (entry.Length == 0)
        {
            return false;
        }

        if (entry.CompressedLength == 0)
        {
            return true;
        }

        return entry.Length / (double)entry.CompressedLength > maximumRatio;
    }

    private static async ValueTask<InspectedEntry> InspectEntryAsync(
        ZipArchiveEntry entry,
        CancellationToken cancellationToken)
    {
        await using var input = entry.Open();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var header = new byte[64];
        var headerLength = 0;
        var buffer = new byte[128 * 1024];
        long total = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (headerLength < header.Length)
            {
                var copyLength = Math.Min(read, header.Length - headerLength);
                buffer.AsSpan(0, copyLength).CopyTo(header.AsSpan(headerLength));
                headerLength += copyLength;
            }

            hash.AppendData(buffer, 0, read);
            total = checked(total + read);
            if (total > entry.Length)
            {
                throw new InvalidDataException("A native ZIP entry expanded beyond its declared length.");
            }
        }

        return new InspectedEntry(
            total,
            header.AsSpan(0, headerLength).ToArray(),
            Convert.ToHexStringLower(hash.GetHashAndReset()));
    }

    private static bool TryReadArm64Elf(ReadOnlySpan<byte> header, out NativeElfIdentity identity)
    {
        identity = null!;
        if (header.Length < 64 ||
            header[0] != 0x7F || header[1] != (byte)'E' || header[2] != (byte)'L' || header[3] != (byte)'F' ||
            header[4] != 2 ||
            header[5] != 1 ||
            header[6] != 1)
        {
            return false;
        }

        var objectType = BinaryPrimitives.ReadUInt16LittleEndian(header[16..]);
        var machine = BinaryPrimitives.ReadUInt16LittleEndian(header[18..]);
        var elfVersion = BinaryPrimitives.ReadUInt32LittleEndian(header[20..]);
        if (machine != ElfMachineAArch64 || elfVersion != 1 || objectType is not 2 and not 3)
        {
            return false;
        }

        identity = new NativeElfIdentity(
            header[4],
            header[5],
            header[6],
            header[7],
            header[8],
            objectType,
            machine,
            BinaryPrimitives.ReadUInt32LittleEndian(header[48..]));
        return true;
    }

    private static NativeEntryInventoryResult Failure(
        string selectedAbi,
        ICollection<DiagnosticRecord> diagnostics,
        string code,
        string message)
    {
        diagnostics.Add(Diagnostic(code, DiagnosticSeverity.Error, message));
        return new NativeEntryInventoryResult(NativeEntryInventoryStatus.Failed, selectedAbi, [], diagnostics);
    }

    private static DiagnosticRecord Diagnostic(string code, DiagnosticSeverity severity, string message) =>
        new(DateTimeOffset.UtcNow, StartupStage.Inventory, severity, code, message);

    private sealed record InspectedEntry(long BytesRead, byte[] Header, string Sha256);
}
