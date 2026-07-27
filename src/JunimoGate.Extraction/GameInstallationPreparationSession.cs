using System.IO.Compression;
using System.Security.Cryptography;
using JunimoGate.Core;

namespace JunimoGate.Extraction;

/// <summary>
/// Owns the APK handles and verified source identities for one product Deep Prepare transaction.
/// Each APK is opened and fully hashed exactly once; extraction and native inspection borrow the
/// same ZIP archives until the transaction is disposed.
/// </summary>
public sealed class GameInstallationPreparationSession : IAsyncDisposable
{
    private readonly PreparedApkSource[] sources;
    private bool disposed;

    private GameInstallationPreparationSession(
        PackageInstallationSnapshot initialSnapshot,
        GameInstallationCandidate candidate,
        PreparedApkSource[] sources,
        long apkBytesHashed)
    {
        InitialSnapshot = initialSnapshot;
        Candidate = candidate;
        this.sources = sources;
        ApkBytesHashed = apkBytesHashed;
    }

    public PackageInstallationSnapshot InitialSnapshot { get; }
    public GameInstallationCandidate Candidate { get; }
    public int ApkSourceCount => sources.Length;
    public long ApkBytesHashed { get; }
    internal IReadOnlyList<PreparedApkSource> Sources
    {
        get
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return sources;
        }
    }

    public static async ValueTask<GameInstallationPreparationSession> OpenAsync(
        PackageInstallationSnapshot snapshot,
        string expectedPackageName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedPackageName);
        ValidateSnapshot(snapshot, expectedPackageName);

        var opened = new List<PreparedApkSource>(snapshot.ApkSources.Count);
        long totalBytes = 0;
        try
        {
            foreach (var labeled in CreateStableSourceLabels(snapshot))
            {
                cancellationToken.ThrowIfCancellationRequested();
                FileStream stream;
                try
                {
                    stream = new FileStream(
                        labeled.Source.SourcePath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read,
                        128 * 1024,
                        FileOptions.Asynchronous | FileOptions.SequentialScan);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                {
                    throw new GameInstallationPreparationException(
                        GameDiscoveryErrorCodes.ApkSourceUnreadable,
                        $"APK source '{labeled.Label}' could not be opened.");
                }

                try
                {
                    if (labeled.Source.Size >= 0 && stream.Length != labeled.Source.Size)
                    {
                        throw new GameInstallationPreparationException(
                            GameDiscoveryErrorCodes.PackageChangedDuringScan,
                            $"APK source '{labeled.Label}' changed before Deep Prepare.");
                    }

                    var size = stream.Length;
                    var digest = Sha256Digest.Parse(Convert.ToHexStringLower(
                        await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)));
                    totalBytes = checked(totalBytes + size);
                    stream.Position = 0;

                    ZipArchive archive;
                    try
                    {
                        archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
                    }
                    catch (InvalidDataException)
                    {
                        throw new GameInstallationPreparationException(
                            GameDiscoveryErrorCodes.ApkSourceInvalidZip,
                            $"APK source '{labeled.Label}' is not a valid ZIP archive.");
                    }

                    var entryNames = new List<string>(archive.Entries.Count);
                    foreach (var entry in archive.Entries)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        entryNames.Add(entry.FullName);
                    }

                    var inventory = ApkEntryInventory.Classify(entryNames);
                    var sourceIdentity = new ApkSourceIdentity(
                        labeled.Source.SourcePath,
                        digest,
                        size,
                        labeled.Label,
                        labeled.Source.SplitName);
                    opened.Add(new PreparedApkSource(sourceIdentity, inventory, stream, archive));
                }
                catch
                {
                    await stream.DisposeAsync().ConfigureAwait(false);
                    throw;
                }
            }

            ValidateRequiredRoles(opened);
            var installationIdentity = new GameInstallationIdentity(
                snapshot.PackageName!,
                snapshot.VersionName!,
                snapshot.LongVersionCode!.Value,
                snapshot.SigningIdentity!,
                GameInstallationDiscoveryCoordinator.SupportedAbi,
                opened.Select(static source => source.Identity));
            var candidate = new GameInstallationCandidate(
                installationIdentity,
                opened.Select(static source => MapInventory(source.Identity.Label, source.Inventory)));
            return new GameInstallationPreparationSession(snapshot, candidate, opened.ToArray(), totalBytes);
        }
        catch
        {
            foreach (var source in opened)
                source.Dispose();
            throw;
        }
    }

    public ValueTask DisposeAsync()
    {
        if (disposed)
            return ValueTask.CompletedTask;
        disposed = true;
        foreach (var source in sources)
            source.Dispose();
        return ValueTask.CompletedTask;
    }

    private static void ValidateSnapshot(PackageInstallationSnapshot snapshot, string expectedPackageName)
    {
        if (!expectedPackageName.Equals(snapshot.PackageName, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(snapshot.VersionName) || snapshot.LongVersionCode is null or < 0)
        {
            throw new GameInstallationPreparationException(
                GameDiscoveryErrorCodes.MetadataInvalid,
                "The installed package metadata is invalid.");
        }

        if (snapshot.SigningIdentity is null)
        {
            throw new GameInstallationPreparationException(
                GameDiscoveryErrorCodes.SigningInfoMissing,
                "The installed package signing identity is unavailable.");
        }

        var baseSources = snapshot.ApkSources.Where(static source => source.IsBase).ToArray();
        var splits = snapshot.ApkSources.Where(static source => !source.IsBase).ToArray();
        if (baseSources.Length != 1 || baseSources[0].SplitName is not null ||
            snapshot.ApkSources.Count == 0 ||
            snapshot.ApkSources.Any(static source => string.IsNullOrWhiteSpace(source.SourcePath) || !Path.IsPathFullyQualified(source.SourcePath)) ||
            snapshot.ApkSources.Select(static source => source.SourcePath).Distinct(StringComparer.Ordinal).Count() != snapshot.ApkSources.Count ||
            splits.Any(static source => string.IsNullOrWhiteSpace(source.SplitName)) ||
            splits.Select(static source => source.SplitName).Distinct(StringComparer.Ordinal).Count() != splits.Length)
        {
            throw new GameInstallationPreparationException(
                GameDiscoveryErrorCodes.SplitIdentityMismatch,
                "The installed base and split APK identities are inconsistent.");
        }
    }

    private static IReadOnlyList<LabeledSource> CreateStableSourceLabels(PackageInstallationSnapshot snapshot)
    {
        var result = new List<LabeledSource>(snapshot.ApkSources.Count)
        {
            new(snapshot.ApkSources.Single(static source => source.IsBase), "base"),
        };
        var index = 1;
        foreach (var split in snapshot.ApkSources.Where(static source => !source.IsBase)
                     .OrderBy(static source => source.SplitName, StringComparer.Ordinal))
        {
            result.Add(new LabeledSource(split, $"split-{index++}"));
        }

        return result;
    }

    private static void ValidateRequiredRoles(IReadOnlyList<PreparedApkSource> sources)
    {
        if (!sources.Any(static source => source.Inventory.Contains(ApkContentRole.GameContent)))
        {
            throw new GameInstallationPreparationException(
                GameDiscoveryErrorCodes.ContentSourceMissing,
                "No APK source contains the required game Content.");
        }

        var nativeAbis = sources.SelectMany(static source => source.Inventory.NativeAbis)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var storeAbis = sources.SelectMany(static source => source.Inventory.ModernAssemblyStoreAbis)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var hasLegacy = sources.Any(static source => source.Inventory.Contains(ApkContentRole.LegacyAssemblyBlob));
        var hasModern = sources.Any(static source => source.Inventory.Contains(ApkContentRole.ModernAssemblyBlob));
        if (!hasLegacy && !hasModern)
        {
            throw new GameInstallationPreparationException(
                GameDiscoveryErrorCodes.AssemblySourceMissing,
                "No APK source contains a supported managed assembly store.");
        }

        if (!storeAbis.Contains(GameInstallationDiscoveryCoordinator.SupportedAbi) &&
            !(hasLegacy && nativeAbis.Contains(GameInstallationDiscoveryCoordinator.SupportedAbi)))
        {
            throw new GameInstallationPreparationException(
                hasModern && nativeAbis.Contains(GameInstallationDiscoveryCoordinator.SupportedAbi)
                    ? GameDiscoveryErrorCodes.AbiConflict
                    : GameDiscoveryErrorCodes.AbiUnsupported,
                "The installed APK sources do not provide a supported ARM64 assembly identity.");
        }
    }

    private static ApkSourceInventory MapInventory(string label, ApkEntryInventory inventory)
    {
        var roles = new List<string>(3);
        if (inventory.Contains(ApkContentRole.GameContent))
            roles.Add(ApkSourceRoleNames.GameContent);
        if (inventory.Contains(ApkContentRole.LegacyAssemblyBlob))
            roles.Add(ApkSourceRoleNames.LegacyAssemblyBlob);
        if (inventory.Contains(ApkContentRole.ModernAssemblyBlob))
            roles.Add(ApkSourceRoleNames.ModernAssemblyBlob);
        return new ApkSourceInventory(label, roles, inventory.NativeAbis, inventory.ModernAssemblyStoreAbis);
    }

    private sealed record LabeledSource(PackageApkSourceSnapshot Source, string Label);
}

public sealed class GameInstallationPreparationException : IOException
{
    public GameInstallationPreparationException(string code, string message)
        : base(message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        Code = code;
    }

    public string Code { get; }
}

internal sealed class PreparedApkSource : IDisposable
{
    public PreparedApkSource(
        ApkSourceIdentity identity,
        ApkEntryInventory inventory,
        FileStream stream,
        ZipArchive archive)
    {
        Identity = identity;
        Inventory = inventory;
        Stream = stream;
        Archive = archive;
    }

    public ApkSourceIdentity Identity { get; }
    public ApkEntryInventory Inventory { get; }
    public FileStream Stream { get; }
    public ZipArchive Archive { get; }

    public void Dispose()
    {
        Archive.Dispose();
        Stream.Dispose();
    }
}
