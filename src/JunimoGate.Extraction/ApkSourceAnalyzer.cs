using System.IO.Compression;
using System.Security.Cryptography;
using JunimoGate.Core;

namespace JunimoGate.Extraction;

/// <summary>Result of scanning one APK source without opening commercial payload entries.</summary>
public sealed record ApkSourceScanResult
{
    private ApkSourceScanResult(
        string label,
        ApkSourceIdentity? source,
        ApkEntryInventory? inventory,
        DiagnosticRecord? diagnostic)
    {
        Label = label;
        Source = source;
        Inventory = inventory;
        Diagnostic = diagnostic;
    }

    /// <summary>Gets the logical source label used in diagnostics and manifests.</summary>
    public string Label { get; }

    /// <summary>Gets the verified source identity when scanning succeeded.</summary>
    public ApkSourceIdentity? Source { get; }

    /// <summary>Gets the ZIP entry inventory when scanning succeeded.</summary>
    public ApkEntryInventory? Inventory { get; }

    /// <summary>Gets the failure diagnostic when scanning did not succeed.</summary>
    public DiagnosticRecord? Diagnostic { get; }

    /// <summary>Gets whether both source identity and inventory were produced.</summary>
    public bool IsSuccess => Source is not null && Inventory is not null;

    internal static ApkSourceScanResult Success(
        string label,
        ApkSourceIdentity source,
        ApkEntryInventory inventory) =>
        new(label, source, inventory, null);

    internal static ApkSourceScanResult Failure(string label, DiagnosticRecord diagnostic) =>
        new(label, null, null, diagnostic);
}

/// <summary>Streams an APK SHA-256 digest and inventories ZIP entry names without reading payload contents.</summary>
public sealed class ApkSourceAnalyzer
{
    /// <summary>Scans one installed APK source and converts expected I/O failures into stable diagnostics.</summary>
    public async ValueTask<ApkSourceScanResult> AnalyzeAsync(
        PackageApkSourceSnapshot source,
        string label,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (string.IsNullOrWhiteSpace(label))
        {
            throw new ArgumentException("A logical APK source label is required.", nameof(label));
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return Failure(label, GameDiscoveryErrorCodes.Cancelled, "APK source scanning was cancelled.");
        }

        FileStream stream;
        try
        {
            stream = new FileStream(
                source.SourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch (FileNotFoundException)
        {
            return Failure(label, GameDiscoveryErrorCodes.ApkSourceMissing, "An APK source is missing.");
        }
        catch (DirectoryNotFoundException)
        {
            return Failure(label, GameDiscoveryErrorCodes.ApkSourceMissing, "An APK source is missing.");
        }
        catch (UnauthorizedAccessException)
        {
            return Failure(label, GameDiscoveryErrorCodes.ApkSourceUnreadable, "An APK source is not readable.");
        }
        catch (ArgumentException)
        {
            return Failure(label, GameDiscoveryErrorCodes.ApkSourceUnreadable, "An APK source path is invalid.");
        }
        catch (NotSupportedException)
        {
            return Failure(label, GameDiscoveryErrorCodes.ApkSourceUnreadable, "An APK source path is unsupported.");
        }
        catch (IOException)
        {
            return Failure(label, GameDiscoveryErrorCodes.ApkSourceUnreadable, "An APK source could not be opened.");
        }

        await using (stream.ConfigureAwait(false))
        {
            byte[] hash;
            long size;
            try
            {
                size = stream.Length;
                hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return Failure(label, GameDiscoveryErrorCodes.Cancelled, "APK source scanning was cancelled.");
            }
            catch (Exception exception) when (exception is IOException or CryptographicException)
            {
                return Failure(label, GameDiscoveryErrorCodes.ApkSourceHashFailed, "An APK source could not be hashed.");
            }

            ApkEntryInventory inventory;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                stream.Position = 0;
                using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
                var entryNames = new List<string>(archive.Entries.Count);
                foreach (var entry in archive.Entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    entryNames.Add(entry.FullName);
                }

                inventory = ApkEntryInventory.Classify(entryNames);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return Failure(label, GameDiscoveryErrorCodes.Cancelled, "APK source scanning was cancelled.");
            }
            catch (InvalidDataException)
            {
                return Failure(label, GameDiscoveryErrorCodes.ApkSourceInvalidZip, "An APK source is not a valid ZIP archive.");
            }
            catch (IOException)
            {
                return Failure(label, GameDiscoveryErrorCodes.ApkSourceInvalidZip, "An APK source ZIP inventory could not be read.");
            }

            var digest = Sha256Digest.Parse(Convert.ToHexStringLower(hash));
            var identity = new ApkSourceIdentity(source.SourcePath, digest, size, label, source.SplitName);
            return ApkSourceScanResult.Success(label, identity, inventory);
        }
    }

    private static ApkSourceScanResult Failure(string label, string code, string message) =>
        ApkSourceScanResult.Failure(
            label,
            new DiagnosticRecord(
                DateTimeOffset.UtcNow,
                StartupStage.Inventory,
                code == GameDiscoveryErrorCodes.Cancelled ? DiagnosticSeverity.Warning : DiagnosticSeverity.Error,
                code,
                message,
                $"Source label: {label}."));
}
