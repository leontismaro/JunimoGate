using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using JunimoGate.Core;

namespace JunimoGate.Extraction;

/// <summary>Builds and activates immutable, platform-neutral game workspaces without loading game assemblies.</summary>
public sealed class GameWorkspacePreparer
{
    private const string SourceManifestFileName = "source-manifest.json";
    private const string ExtractionManifestFileName = "extraction-manifest.json";
    private const string RewriteManifestFileName = "rewrite-manifest.json";
    private const string StateFileName = "workspace-state.json";
    private const string StateFormat = "junimogate-workspace-state";
    private const string StateSchema = "v1";
    private const string SourceManifestFormat = "junimogate-source-manifest";
    private const string ExtractionManifestFormat = "junimogate-extraction-manifest";
    private const string RewriteManifestFormat = "junimogate-rewrite-manifest";
    private const string RewriteStatusNotApplied = "not-applied";

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> RootLocks = new(StringComparer.Ordinal);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.General)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
    };

    private readonly IWorkspaceCandidateRevalidator revalidator;
    private readonly StrictContentExtractor contentExtractor;

    public GameWorkspacePreparer(
        IWorkspaceCandidateRevalidator revalidator,
        StrictContentExtractor? contentExtractor = null)
    {
        ArgumentNullException.ThrowIfNull(revalidator);
        this.revalidator = revalidator;
        this.contentExtractor = contentExtractor ?? new StrictContentExtractor();
    }

    public async ValueTask<WorkspacePreparationResult> PrepareAsync(
        WorkspacePreparationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var stopwatch = Stopwatch.StartNew();
        var diagnostics = new List<DiagnosticRecord>();
        string? stagingPath = null;
        string? keyText = null;
        string? workspacePath = null;
        WorkspaceExtractionStatistics? statistics = null;
        long peakTemporaryBytes = 0;
        long finalWorkspaceBytes = 0;
        RootLease? rootLease = null;

        try
        {
            Report(request, WorkspaceProgressStage.AcquiringLock, "Acquiring workspace lock.");
            rootLease = await AcquireRootLeaseAsync(request.WorkspaceRoot, cancellationToken).ConfigureAwait(false);

            Report(request, WorkspaceProgressStage.VerifyingCertificate, "Verifying installation certificate policy.");
            var installation = request.Candidate.Installation;
            var certificate = KnownGameCertificate.Verify(installation.PackageName, installation.SigningIdentity);
            if (!certificate.AllowsCodeExecution)
            {
                diagnostics.Add(Diagnostic(WorkspaceErrorCodes.CertificateBlocked, DiagnosticSeverity.Error,
                    "The installation certificate is not allowed to provide executable game code."));
                return Result(WorkspacePreparationStatus.Blocked, null, null, diagnostics);
            }

            var root = request.WorkspaceRoot;
            var workspacesRoot = Path.Combine(root, "workspaces");
            var stagingRoot = Path.Combine(root, "staging");
            var quarantineRoot = Path.Combine(root, "quarantine");
            Directory.CreateDirectory(workspacesRoot);
            Directory.CreateDirectory(stagingRoot);
            Directory.CreateDirectory(quarantineRoot);

            Report(request, WorkspaceProgressStage.CleaningStaging, "Cleaning stale workspace staging directories.");
            CleanupStaleStaging(stagingRoot);

            var key = WorkspaceCacheKey.Create(
                installation.PackageName,
                installation.LongVersionCode,
                installation.SelectedAbi,
                installation.SigningIdentity,
                installation.ApkSources.Select(static source => source.Digest),
                request.Options.ExtractorSchema,
                request.Options.RewriterRecipe,
                request.Options.SmapiBuildId);
            keyText = key.ToString();
            workspacePath = Path.Combine(workspacesRoot, keyText);

            var cacheHit = false;
            if (Directory.Exists(workspacePath))
            {
                Report(request, WorkspaceProgressStage.ValidatingCache, "Validating immutable workspace cache.");
                var cacheValidation = await ValidateWorkspaceAsync(
                    workspacePath,
                    keyText,
                    request,
                    cancellationToken).ConfigureAwait(false);
                if (cacheValidation.IsValid)
                {
                    cacheHit = true;
                    statistics = cacheValidation.Statistics;
                    finalWorkspaceBytes = cacheValidation.TotalBytes;
                }
                else
                {
                    var quarantinePath = Path.Combine(
                        quarantineRoot,
                        $"{keyText}-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}");
                    try
                    {
                        Directory.Move(workspacePath, quarantinePath);
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        throw new WorkspacePreparationException(
                            WorkspaceErrorCodes.CacheCorrupt,
                            "A corrupt workspace cache could not be quarantined.");
                    }

                    diagnostics.Add(Diagnostic(
                        WorkspaceErrorCodes.CacheCorrupt,
                        DiagnosticSeverity.Warning,
                        "A corrupt workspace cache was quarantined and will be rebuilt."));
                }
            }

            if (!cacheHit)
            {
                stagingPath = Path.Combine(stagingRoot, $"{keyText}-{Guid.NewGuid():N}");
                Directory.CreateDirectory(stagingPath);
                var built = await BuildWorkspaceAsync(
                    request,
                    keyText,
                    stagingPath,
                    cancellationToken).ConfigureAwait(false);
                statistics = built.Statistics;
                peakTemporaryBytes = built.TotalBytes;
                finalWorkspaceBytes = built.TotalBytes;

                Report(request, WorkspaceProgressStage.Committing, "Committing immutable workspace.");
                if (Directory.Exists(workspacePath))
                {
                    throw new WorkspacePreparationException(
                        WorkspaceErrorCodes.ActivationFailed,
                        "The immutable workspace destination unexpectedly already exists.");
                }

                Directory.Move(stagingPath, workspacePath);
                stagingPath = null;
            }

            Report(request, WorkspaceProgressStage.RevalidatingInstallation, "Revalidating installation identity.");
            var freshCandidate = await revalidator.RevalidateAsync(installation.PackageName, cancellationToken).ConfigureAwait(false);
            if (freshCandidate is null || !IdentityEquals(request.Candidate, freshCandidate) ||
                !KnownGameCertificate.Verify(
                    freshCandidate.Installation.PackageName,
                    freshCandidate.Installation.SigningIdentity).AllowsCodeExecution)
            {
                diagnostics.Add(Diagnostic(
                    WorkspaceErrorCodes.SourceIdentityMismatch,
                    DiagnosticSeverity.Error,
                    "The installed package identity changed before workspace activation."));
                return Result(WorkspacePreparationStatus.Failed, workspacePath, keyText, diagnostics, statistics);
            }

            cancellationToken.ThrowIfCancellationRequested();
            Report(request, WorkspaceProgressStage.Activating, "Activating workspace state.");
            await ActivateAsync(root, keyText, cancellationToken).ConfigureAwait(false);
            Report(request, WorkspaceProgressStage.Completed, cacheHit ? "Workspace cache hit." : "Workspace built.");
            stopwatch.Stop();
            return Result(
                cacheHit ? WorkspacePreparationStatus.CacheHit : WorkspacePreparationStatus.Built,
                workspacePath,
                keyText,
                diagnostics,
                statistics,
                new WorkspacePreparationMetrics(
                    Math.Max(1, stopwatch.ElapsedMilliseconds),
                    cacheHit ? 0 : peakTemporaryBytes,
                    finalWorkspaceBytes));
        }
        catch (OperationCanceledException)
        {
            diagnostics.Add(Diagnostic(WorkspaceErrorCodes.Cancelled, DiagnosticSeverity.Information,
                "Workspace preparation was cancelled."));
            return Result(WorkspacePreparationStatus.Cancelled, null, keyText, diagnostics, statistics);
        }
        catch (WorkspacePreparationException exception)
        {
            diagnostics.Add(Diagnostic(exception.Code, DiagnosticSeverity.Error, exception.Message));
            return Result(WorkspacePreparationStatus.Failed, workspacePath, keyText, diagnostics, statistics);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or CryptographicException)
        {
            diagnostics.Add(Diagnostic(
                WorkspaceErrorCodes.ManifestInvalid,
                DiagnosticSeverity.Error,
                "Workspace preparation failed while reading, writing, or validating data."));
            return Result(WorkspacePreparationStatus.Failed, workspacePath, keyText, diagnostics, statistics);
        }
        finally
        {
            if (stagingPath is not null)
            {
                TryDeleteDirectory(stagingPath);
            }

            if (rootLease is not null)
            {
                await rootLease.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async ValueTask<(WorkspaceExtractionStatistics Statistics, IReadOnlyList<WorkspaceExtractedFileManifest> Files, long TotalBytes)> BuildWorkspaceAsync(
        WorkspacePreparationRequest request,
        string keyText,
        string stagingPath,
        CancellationToken cancellationToken)
    {
        var installation = request.Candidate.Installation;
        Report(request, WorkspaceProgressStage.VerifyingSources, "Verifying APK source identities.");
        var openedSources = new List<OpenedSource>();
        try
        {
            for (var index = 0; index < installation.ApkSources.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var source = installation.ApkSources[index];
                var inventory = request.Candidate.SourceInventories[index];
                FileStream stream;
                try
                {
                    stream = new FileStream(
                        source.SourcePath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read,
                        128 * 1024,
                        FileOptions.Asynchronous | FileOptions.SequentialScan);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    throw new WorkspacePreparationException(
                        WorkspaceErrorCodes.SourceIdentityMismatch,
                        $"APK source '{source.Label}' could not be opened.");
                }

                try
                {
                    if (stream.Length != source.Size)
                    {
                        throw new WorkspacePreparationException(
                            WorkspaceErrorCodes.SourceHashMismatch,
                            $"APK source '{source.Label}' size does not match the discovered identity.");
                    }

                    var actualHash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
                    if (!actualHash.Equals(source.Digest.Value, StringComparison.Ordinal))
                    {
                        throw new WorkspacePreparationException(
                            WorkspaceErrorCodes.SourceHashMismatch,
                            $"APK source '{source.Label}' hash does not match the discovered identity.");
                    }

                    stream.Position = 0;
                    var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
                    openedSources.Add(new OpenedSource(source, inventory, stream, archive));
                }
                catch
                {
                    await stream.DisposeAsync().ConfigureAwait(false);
                    throw;
                }
            }

            var contentSources = openedSources
                .Where(static source => source.Inventory.Roles.Contains(ApkSourceRoleNames.GameContent, StringComparer.Ordinal))
                .Select(static source => new ContentApkSource(source.Identity.Label, source.Archive))
                .ToArray();
            var outputs = (await contentExtractor.ExtractAsync(
                contentSources,
                stagingPath,
                request.Options.Limits,
                request.Options.Progress,
                cancellationToken).ConfigureAwait(false)).ToList();

            Report(request, WorkspaceProgressStage.ExtractingAssemblies, "Extracting selected-ABI assemblies.");
            var modernSources = openedSources
                .Where(static source => source.Inventory.Roles.Contains(ApkSourceRoleNames.ModernAssemblyBlob, StringComparer.Ordinal))
                .ToArray();
            var legacyOnly = modernSources.Length == 0 && openedSources.Any(
                static source => source.Inventory.Roles.Contains(ApkSourceRoleNames.LegacyAssemblyBlob, StringComparer.Ordinal));
            if (legacyOnly)
            {
                throw new WorkspacePreparationException(
                    WorkspaceErrorCodes.UnsupportedAssemblyStore,
                    "Legacy-only assembly storage is not supported.");
            }

            var assembliesDirectory = Path.Combine(stagingPath, "assemblies");
            var selectedStoreCount = 0;
            await using (var transaction = new AssemblyExtractionTransaction(assembliesDirectory))
            {
                foreach (var source in modernSources)
                {
                    var stores = AssemblyStoreV2.FindInApk(source.Archive)
                        .Where(store => store.Abi.Equals(installation.SelectedAbi, StringComparison.OrdinalIgnoreCase))
                        .ToArray();
                    foreach (var storeEntry in stores)
                    {
                        selectedStoreCount++;
                        try
                        {
                            using var store = storeEntry.Open();
                            foreach (var item in store.Items.OrderBy(static item => item.Name, StringComparer.Ordinal))
                            {
                                ExtractedAssemblyFile extracted;
                                try
                                {
                                    extracted = await transaction.ExtractAsync(store, item, cancellationToken).ConfigureAwait(false);
                                }
                                catch (IOException exception) when (exception.Message.Contains("Duplicate", StringComparison.OrdinalIgnoreCase) || exception.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase))
                                {
                                    throw new WorkspacePreparationException(
                                        WorkspaceErrorCodes.DuplicateOutput,
                                        "Assembly stores produce a duplicate output.");
                                }

                                outputs.Add(new WorkspaceExtractedFileManifest(
                                    "assembly",
                                    $"assemblies/{extracted.Name}",
                                    extracted.Size,
                                    extracted.Sha256,
                                    source.Identity.Label,
                                    storeEntry.Entry.FullName));
                            }
                        }
                        catch (AssemblyStoreFormatException)
                        {
                            throw new WorkspacePreparationException(
                                WorkspaceErrorCodes.UnsupportedAssemblyStore,
                                "A selected-ABI assembly store has an unsupported or invalid format.");
                        }
                    }
                }
            }

            if (selectedStoreCount == 0)
            {
                throw new WorkspacePreparationException(
                    WorkspaceErrorCodes.UnsupportedAssemblyStore,
                    "No supported assembly store exists for the selected ABI.");
            }

            outputs = outputs.OrderBy(static output => output.RelativePath, StringComparer.Ordinal).ToList();
            RequireOutputs(outputs);
            var statistics = new WorkspaceExtractionStatistics(
                outputs.Count(static output => output.Kind == "content"),
                outputs.Where(static output => output.Kind == "content").Sum(static output => output.Size),
                outputs.Count(static output => output.Kind == "assembly"),
                outputs.Where(static output => output.Kind == "assembly").Sum(static output => output.Size));

            Report(request, WorkspaceProgressStage.WritingManifests, "Writing workspace manifests.");
            var sourceManifest = CreateSourceManifest(request, keyText);
            var extractionManifest = new WorkspaceExtractionManifest(
                ExtractionManifestFormat,
                request.Options.ManifestSchema,
                keyText,
                request.Options.ExtractorSchema,
                request.Options.RewriterRecipe,
                request.Options.SmapiBuildId,
                outputs,
                statistics);
            var rewriteManifest = new WorkspaceRewriteManifest(
                RewriteManifestFormat,
                request.Options.ManifestSchema,
                keyText,
                request.Options.RewriterRecipe,
                RewriteStatusNotApplied);
            await WriteJsonFileAsync(Path.Combine(stagingPath, SourceManifestFileName), sourceManifest, cancellationToken).ConfigureAwait(false);
            await WriteJsonFileAsync(Path.Combine(stagingPath, ExtractionManifestFileName), extractionManifest, cancellationToken).ConfigureAwait(false);
            await WriteJsonFileAsync(Path.Combine(stagingPath, RewriteManifestFileName), rewriteManifest, cancellationToken).ConfigureAwait(false);

            Report(request, WorkspaceProgressStage.ValidatingOutputs, "Validating completed workspace outputs.");
            var validation = await ValidateWorkspaceAsync(stagingPath, keyText, request, cancellationToken).ConfigureAwait(false);
            if (!validation.IsValid)
            {
                throw new WorkspacePreparationException(WorkspaceErrorCodes.ManifestInvalid, "Completed workspace validation failed.");
            }

            return (statistics, outputs, validation.TotalBytes);
        }
        finally
        {
            foreach (var source in openedSources)
            {
                source.Dispose();
            }
        }
    }

    private static WorkspaceSourceManifest CreateSourceManifest(WorkspacePreparationRequest request, string keyText)
    {
        var installation = request.Candidate.Installation;
        return new WorkspaceSourceManifest(
            SourceManifestFormat,
            request.Options.ManifestSchema,
            keyText,
            installation.PackageName,
            installation.VersionName,
            installation.LongVersionCode,
            installation.SelectedAbi,
            new WorkspaceSignerManifest(
                installation.SigningIdentity.CurrentSignerDigests.Select(static digest => digest.Value).ToArray(),
                installation.SigningIdentity.RotationHistory.Select(static digest => digest.Value).ToArray()),
            installation.ApkSources.Select(static source => new WorkspaceSourceManifestEntry(
                source.Label,
                source.SplitName,
                source.Digest.Value,
                source.Size)).ToArray());
    }

    private static async ValueTask<CacheValidation> ValidateWorkspaceAsync(
        string workspacePath,
        string keyText,
        WorkspacePreparationRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var sourceManifest = await ReadJsonFileAsync<WorkspaceSourceManifest>(
                Path.Combine(workspacePath, SourceManifestFileName), cancellationToken).ConfigureAwait(false);
            var extractionManifest = await ReadJsonFileAsync<WorkspaceExtractionManifest>(
                Path.Combine(workspacePath, ExtractionManifestFileName), cancellationToken).ConfigureAwait(false);
            var rewriteManifest = await ReadRewriteManifestAsync(
                Path.Combine(workspacePath, RewriteManifestFileName), cancellationToken).ConfigureAwait(false);
            if (sourceManifest is null || extractionManifest is null || rewriteManifest is null ||
                !SourceManifestMatches(sourceManifest, CreateSourceManifest(request, keyText)) ||
                extractionManifest.Format != ExtractionManifestFormat ||
                extractionManifest.Schema != request.Options.ManifestSchema ||
                extractionManifest.CacheKey != keyText ||
                extractionManifest.ExtractorSchema != request.Options.ExtractorSchema ||
                extractionManifest.RewriterRecipe != request.Options.RewriterRecipe ||
                extractionManifest.SmapiBuildId != request.Options.SmapiBuildId ||
                extractionManifest.Files is null || extractionManifest.Statistics is null ||
                rewriteManifest.Format != RewriteManifestFormat ||
                rewriteManifest.Schema != request.Options.ManifestSchema ||
                rewriteManifest.CacheKey != keyText ||
                rewriteManifest.Recipe != request.Options.RewriterRecipe ||
                rewriteManifest.Status != RewriteStatusNotApplied)
            {
                return CacheValidation.Invalid;
            }

            var expected = new HashSet<string>(StringComparer.Ordinal);
            foreach (var file in extractionManifest.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsValidManifestFile(file, request) || !expected.Add(file.RelativePath) ||
                    file.Size < 0 || !Sha256Digest.TryParse(file.Sha256, out _))
                {
                    return CacheValidation.Invalid;
                }

                var fullPath = Path.Combine(workspacePath, file.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                var info = new FileInfo(fullPath);
                if (!info.Exists || info.Length != file.Size)
                {
                    return CacheValidation.Invalid;
                }

                await using var stream = new FileStream(
                    fullPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    128 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
                if (!hash.Equals(file.Sha256, StringComparison.Ordinal))
                {
                    return CacheValidation.Invalid;
                }
            }

            var actual = Directory.EnumerateFiles(workspacePath, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(workspacePath, path).Replace(Path.DirectorySeparatorChar, '/'))
                .Where(path => path != SourceManifestFileName &&
                    path != ExtractionManifestFileName &&
                    path != RewriteManifestFileName)
                .ToHashSet(StringComparer.Ordinal);
            if (!actual.SetEquals(expected))
            {
                return CacheValidation.Invalid;
            }

            RequireOutputs(extractionManifest.Files);
            var computedStatistics = new WorkspaceExtractionStatistics(
                extractionManifest.Files.Count(static output => output.Kind == "content"),
                extractionManifest.Files.Where(static output => output.Kind == "content").Sum(static output => output.Size),
                extractionManifest.Files.Count(static output => output.Kind == "assembly"),
                extractionManifest.Files.Where(static output => output.Kind == "assembly").Sum(static output => output.Size));
            if (computedStatistics != extractionManifest.Statistics)
            {
                return CacheValidation.Invalid;
            }

            var totalBytes = Directory.EnumerateFiles(workspacePath, "*", SearchOption.AllDirectories)
                .Sum(static path => new FileInfo(path).Length);
            return new CacheValidation(true, extractionManifest.Statistics, totalBytes);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or WorkspacePreparationException)
        {
            return CacheValidation.Invalid;
        }
    }

    private static bool SourceManifestMatches(WorkspaceSourceManifest actual, WorkspaceSourceManifest expected) =>
        actual.Format == expected.Format &&
        actual.Schema == expected.Schema &&
        actual.CacheKey == expected.CacheKey &&
        actual.PackageName == expected.PackageName &&
        actual.VersionName == expected.VersionName &&
        actual.LongVersionCode == expected.LongVersionCode &&
        actual.Abi == expected.Abi &&
        actual.Signers is not null &&
        actual.Signers.Current.SequenceEqual(expected.Signers.Current, StringComparer.Ordinal) &&
        actual.Signers.History.SequenceEqual(expected.Signers.History, StringComparer.Ordinal) &&
        actual.Sources is not null &&
        actual.Sources.SequenceEqual(expected.Sources);

    private static void RequireOutputs(IReadOnlyList<WorkspaceExtractedFileManifest> outputs)
    {
        if (!outputs.Any(static output => output.Kind == "content") ||
            !outputs.Any(static output => output.Kind == "assembly" && Path.GetFileName(output.RelativePath).Equals("StardewValley.dll", StringComparison.OrdinalIgnoreCase)) ||
            !outputs.Any(static output => output.Kind == "assembly" && Path.GetFileName(output.RelativePath).Equals("MonoGame.Framework.dll", StringComparison.OrdinalIgnoreCase)))
        {
            throw new WorkspacePreparationException(
                WorkspaceErrorCodes.RequiredOutputMissing,
                "The workspace is missing required game assemblies or Content files.");
        }
    }

    private static bool IsValidManifestFile(
        WorkspaceExtractedFileManifest file,
        WorkspacePreparationRequest request)
    {
        if (file is null || !IsSafeWorkspaceRelativePath(file.RelativePath) ||
            string.IsNullOrWhiteSpace(file.SourceLabel) || string.IsNullOrWhiteSpace(file.SourceEntry) ||
            !IsSafeWorkspaceRelativePath(file.SourceEntry))
        {
            return false;
        }

        var inventory = request.Candidate.SourceInventories.FirstOrDefault(source => source.SourceLabel == file.SourceLabel);
        if (inventory is null)
        {
            return false;
        }

        if (file.Kind == "content")
        {
            return file.RelativePath.StartsWith("Content/", StringComparison.Ordinal) &&
                file.SourceEntry.StartsWith("assets/Content/", StringComparison.Ordinal) &&
                inventory.Roles.Contains(ApkSourceRoleNames.GameContent, StringComparer.Ordinal);
        }

        return file.Kind == "assembly" &&
            file.RelativePath.StartsWith("assemblies/", StringComparison.Ordinal) &&
            AssemblyStoreApkPath.TryParse(file.SourceEntry, out var abi) &&
            abi.Equals(request.Candidate.Installation.SelectedAbi, StringComparison.OrdinalIgnoreCase) &&
            inventory.Roles.Contains(ApkSourceRoleNames.ModernAssemblyBlob, StringComparer.Ordinal);
    }

    private static bool IsSafeWorkspaceRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.IndexOfAny(['\\', '\0', '<', '>', ':', '"', '|', '?', '*']) >= 0 ||
            path.StartsWith("/", StringComparison.Ordinal) || path.Any(char.IsControl))
        {
            return false;
        }

        var segments = path.Split('/', StringSplitOptions.None);
        return segments.Length >= 2 && segments.All(static segment =>
            segment.Length > 0 && segment is not "." and not ".." &&
            !segment.EndsWith(' ') && !segment.EndsWith('.'));
    }

    private static bool IdentityEquals(GameInstallationCandidate original, GameInstallationCandidate fresh)
    {
        var left = original.Installation;
        var right = fresh.Installation;
        return left.PackageName == right.PackageName &&
            left.VersionName == right.VersionName &&
            left.LongVersionCode == right.LongVersionCode &&
            left.SelectedAbi == right.SelectedAbi &&
            left.SigningIdentity.CurrentSignerDigests.SequenceEqual(right.SigningIdentity.CurrentSignerDigests) &&
            left.SigningIdentity.RotationHistory.SequenceEqual(right.SigningIdentity.RotationHistory) &&
            left.ApkSources.Select(static source => (source.Label, source.SplitName, source.Digest, source.Size))
                .SequenceEqual(right.ApkSources.Select(static source => (source.Label, source.SplitName, source.Digest, source.Size)));
    }

    private static async ValueTask ActivateAsync(string root, string keyText, CancellationToken cancellationToken)
    {
        var statePath = Path.Combine(root, StateFileName);
        WorkspaceState? current = null;
        if (File.Exists(statePath))
        {
            try
            {
                current = await ReadJsonFileAsync<WorkspaceState>(statePath, cancellationToken).ConfigureAwait(false);
                if (current is null || current.Format != StateFormat || current.Schema != StateSchema)
                {
                    throw new JsonException("Workspace state identity is invalid.");
                }
            }
            catch (Exception exception) when (exception is IOException or JsonException)
            {
                throw new WorkspacePreparationException(
                    WorkspaceErrorCodes.ActivationFailed,
                    "Existing workspace state is invalid and was not replaced.");
            }
        }

        if (current?.ActiveKey == keyText)
        {
            return;
        }

        var next = new WorkspaceState(StateFormat, StateSchema, keyText, current?.ActiveKey);
        var tempPath = Path.Combine(root, $".{StateFileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(next, JsonOptions);
            await using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(tempPath, statePath, overwrite: true);
        }
        catch (OperationCanceledException)
        {
            TryDeleteFile(tempPath);
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            TryDeleteFile(tempPath);
            throw new WorkspacePreparationException(
                WorkspaceErrorCodes.ActivationFailed,
                "Workspace state could not be atomically activated.");
        }
    }

    private static async ValueTask WriteJsonFileAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        await using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<T?> ReadJsonFileAsync<T>(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<WorkspaceRewriteManifest?> ReadRewriteManifestAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(bytes);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var expectedFields = new HashSet<string>(
            ["format", "schema", "cacheKey", "recipe", "status"],
            StringComparer.Ordinal);
        var actualFields = document.RootElement.EnumerateObject()
            .Select(static property => property.Name)
            .ToArray();
        if (actualFields.Length != expectedFields.Count ||
            actualFields.Distinct(StringComparer.Ordinal).Count() != expectedFields.Count ||
            !actualFields.ToHashSet(StringComparer.Ordinal).SetEquals(expectedFields))
        {
            return null;
        }

        return JsonSerializer.Deserialize<WorkspaceRewriteManifest>(bytes, JsonOptions);
    }

    private static async ValueTask<RootLease> AcquireRootLeaseAsync(string root, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(root);
        var canonicalRoot = Path.GetFullPath(root);
        var processLock = RootLocks.GetOrAdd(canonicalRoot, static _ => new SemaphoreSlim(1, 1));
        await processLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var lockPath = Path.Combine(canonicalRoot, ".workspace.lock");
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var fileLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                    return new RootLease(processLock, fileLock);
                }
                catch (IOException)
                {
                    await Task.Delay(50, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch
        {
            processLock.Release();
            throw;
        }
    }

    private static void CleanupStaleStaging(string stagingRoot)
    {
        foreach (var directory in Directory.EnumerateDirectories(stagingRoot))
        {
            var name = Path.GetFileName(directory);
            var dash = name.IndexOf('-');
            if (dash != 64 || name.Length != 97 ||
                !name.AsSpan(0, 64).ToString().All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f') ||
                !name.AsSpan(65).ToString().All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f'))
            {
                continue;
            }

            TryDeleteDirectory(directory);
        }
    }

    private static void Report(WorkspacePreparationRequest request, WorkspaceProgressStage stage, string message) =>
        request.Options.Progress?.Report(new WorkspaceProgressEvent(stage, message));

    private static DiagnosticRecord Diagnostic(string code, DiagnosticSeverity severity, string message) =>
        new(DateTimeOffset.UtcNow, StartupStage.Extraction, severity, code, message);

    private static WorkspacePreparationResult Result(
        WorkspacePreparationStatus status,
        string? workspacePath,
        string? key,
        IEnumerable<DiagnosticRecord> diagnostics,
        WorkspaceExtractionStatistics? statistics = null,
        WorkspacePreparationMetrics? metrics = null) =>
        new(status, workspacePath, key, diagnostics, statistics, metrics);

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Staging cleanup is best effort; the scoped naming rule prevents deleting unrelated paths.
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // Preserve the primary activation or cancellation result.
        }
    }

    private sealed record CacheValidation(bool IsValid, WorkspaceExtractionStatistics? Statistics, long TotalBytes)
    {
        public static CacheValidation Invalid { get; } = new(false, null, 0);
    }

    private sealed class OpenedSource : IDisposable
    {
        public OpenedSource(ApkSourceIdentity identity, ApkSourceInventory inventory, FileStream stream, ZipArchive archive)
        {
            Identity = identity;
            Inventory = inventory;
            Stream = stream;
            Archive = archive;
        }

        public ApkSourceIdentity Identity { get; }
        public ApkSourceInventory Inventory { get; }
        public FileStream Stream { get; }
        public ZipArchive Archive { get; }

        public void Dispose()
        {
            Archive.Dispose();
            Stream.Dispose();
        }
    }

    private sealed class RootLease : IAsyncDisposable
    {
        private readonly SemaphoreSlim processLock;
        private readonly FileStream fileLock;

        public RootLease(SemaphoreSlim processLock, FileStream fileLock)
        {
            this.processLock = processLock;
            this.fileLock = fileLock;
        }

        public async ValueTask DisposeAsync()
        {
            await fileLock.DisposeAsync().ConfigureAwait(false);
            processLock.Release();
        }
    }
}
