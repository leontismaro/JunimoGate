using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using JunimoGate.Core;

namespace JunimoGate.Extraction;

/// <summary>Builds and activates immutable, platform-neutral game workspaces without loading game assemblies.</summary>
public sealed class GameWorkspacePreparer
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> RootLocks = new(StringComparer.Ordinal);

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

    public ValueTask<WorkspacePreparationResult> PrepareAsync(
        WorkspacePreparationRequest request,
        CancellationToken cancellationToken = default) =>
        PrepareAsync(request, preparationSession: null, cancellationToken);

    public async ValueTask<WorkspacePreparationResult> PrepareAsync(
        WorkspacePreparationRequest request,
        GameInstallationPreparationSession? preparationSession,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (preparationSession is not null &&
            !WorkspaceManifestValidator.CandidateIdentityEquals(
                request.Candidate,
                preparationSession.Candidate,
                includeSourcePaths: true))
        {
            throw new ArgumentException("The preparation session does not match the workspace candidate.", nameof(preparationSession));
        }

        var stopwatch = Stopwatch.StartNew();
        var diagnostics = new List<DiagnosticRecord>();
        string? stagingPath = null;
        string? keyText = null;
        string? workspacePath = null;
        WorkspaceExtractionStatistics? statistics = null;
        long peakTemporaryBytes = 0;
        long finalWorkspaceBytes = 0;
        IReadOnlyList<WorkspaceExtractedFileManifest>? preparedFiles = null;
        GameInstallationCandidate? planCandidate = null;
        var workspacePayloadHashPassCount = 0;
        long workspacePayloadBytesHashed = 0;
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
                    preparedFiles = cacheValidation.Files;
                    workspacePayloadHashPassCount = 1;
                    workspacePayloadBytesHashed = cacheValidation.Files?.Sum(static file => file.Size) ?? 0;
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
                    preparationSession,
                    cancellationToken).ConfigureAwait(false);
                statistics = built.Statistics;
                preparedFiles = built.Files;
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

            if (request.Options.RevalidateInstallation)
            {
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

                planCandidate = freshCandidate;
            }
            else
            {
                planCandidate = request.Candidate;
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (request.Options.ActivateWorkspace)
            {
                Report(request, WorkspaceProgressStage.Activating, "Activating workspace state.");
                await ActivateAsync(root, keyText, cancellationToken).ConfigureAwait(false);
            }

            if (preparedFiles is null || planCandidate is null)
                throw new WorkspacePreparationException(WorkspaceErrorCodes.ManifestInvalid, "Workspace evidence is incomplete.");
            var executionPlan = CreateExecutionPlan(planCandidate, keyText, workspacePath, preparedFiles);
            Report(request, WorkspaceProgressStage.Completed, cacheHit ? "Workspace cache hit." : "Workspace built.");
            stopwatch.Stop();
            var metrics = new WorkspacePreparationMetrics(
                Math.Max(1, stopwatch.ElapsedMilliseconds),
                cacheHit ? 0 : peakTemporaryBytes,
                finalWorkspaceBytes)
            {
                ApkSourceOpenCount = preparationSession?.ApkSourceCount ?? 0,
                ApkFullHashCount = preparationSession?.ApkSourceCount ?? 0,
                ApkBytesHashed = preparationSession?.ApkBytesHashed ?? 0,
                WorkspacePayloadHashPassCount = workspacePayloadHashPassCount,
                WorkspacePayloadBytesHashed = workspacePayloadBytesHashed,
            };
            return Result(
                cacheHit ? WorkspacePreparationStatus.CacheHit : WorkspacePreparationStatus.Built,
                workspacePath,
                keyText,
                diagnostics,
                statistics,
                metrics,
                executionPlan);
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
        GameInstallationPreparationSession? preparationSession,
        CancellationToken cancellationToken)
    {
        var installation = request.Candidate.Installation;
        Report(request, WorkspaceProgressStage.VerifyingSources, "Verifying APK source identities.");
        var openedSources = new List<OpenedSource>();
        try
        {
            if (preparationSession is not null)
            {
                for (var index = 0; index < preparationSession.Sources.Count; index++)
                {
                    var source = preparationSession.Sources[index];
                    openedSources.Add(new OpenedSource(
                        source.Identity,
                        request.Candidate.SourceInventories[index],
                        source.Stream,
                        source.Archive,
                        ownsResources: false));
                }
            }
            else for (var index = 0; index < installation.ApkSources.Count; index++)
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
                    openedSources.Add(new OpenedSource(source, inventory, stream, archive, ownsResources: true));
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
            var selectedModernStores = modernSources.SelectMany(source =>
                AssemblyStoreV2.FindInApk(source.Archive)
                    .Where(store => store.Abi.Equals(installation.SelectedAbi, StringComparison.OrdinalIgnoreCase))
                    .Select(store => (Source: source, Store: store))).ToArray();
            var legacySources = selectedModernStores.Length == 0
                ? openedSources.Where(static source =>
                    source.Inventory.Roles.Contains(ApkSourceRoleNames.LegacyAssemblyBlob, StringComparer.Ordinal)).ToArray()
                : [];

            var assembliesDirectory = Path.Combine(stagingPath, "assemblies");
            var selectedStoreCount = 0;
            await using (var transaction = new AssemblyExtractionTransaction(assembliesDirectory))
            {
                foreach (var selected in selectedModernStores)
                {
                    selectedStoreCount++;
                    try
                    {
                        using var store = selected.Store.Open();
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
                                selected.Source.Identity.Label,
                                selected.Store.Entry.FullName));
                        }
                    }
                    catch (AssemblyStoreFormatException)
                    {
                        throw new WorkspacePreparationException(
                            WorkspaceErrorCodes.UnsupportedAssemblyStore,
                            "A selected-ABI assembly store has an unsupported or invalid format.");
                    }
                }

                foreach (var source in legacySources)
                {
                    try
                    {
                        using var store = LegacyAssemblyStoreSet.Open(source.Archive, installation.SelectedAbi);
                        selectedStoreCount++;
                        foreach (var item in store.Items)
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
                                    "Legacy assembly stores produce a duplicate output.");
                            }

                            outputs.Add(new WorkspaceExtractedFileManifest(
                                "assembly",
                                $"assemblies/{extracted.Name}",
                                extracted.Size,
                                extracted.Sha256,
                                source.Identity.Label,
                                item.SourceEntry));
                        }
                    }
                    catch (AssemblyStoreFormatException)
                    {
                        throw new WorkspacePreparationException(
                            WorkspaceErrorCodes.UnsupportedAssemblyStore,
                            "The selected-ABI legacy assembly store has an unsupported or invalid format.");
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
            WorkspaceManifestValidator.RequireOutputs(outputs);
            var statistics = new WorkspaceExtractionStatistics(
                outputs.Count(static output => output.Kind == "content"),
                outputs.Where(static output => output.Kind == "content").Sum(static output => output.Size),
                outputs.Count(static output => output.Kind == "assembly"),
                outputs.Where(static output => output.Kind == "assembly").Sum(static output => output.Size));

            Report(request, WorkspaceProgressStage.WritingManifests, "Writing workspace manifests.");
            var sourceManifest = CreateSourceManifest(request, keyText);
            var extractionManifest = new WorkspaceExtractionManifest(
                WorkspaceManifestConstants.ExtractionManifestFormat,
                request.Options.ManifestSchema,
                keyText,
                request.Options.ExtractorSchema,
                request.Options.RewriterRecipe,
                request.Options.SmapiBuildId,
                outputs,
                statistics);
            var rewriteManifest = new WorkspaceRewriteManifest(
                WorkspaceManifestConstants.RewriteManifestFormat,
                request.Options.ManifestSchema,
                keyText,
                request.Options.RewriterRecipe,
                WorkspaceManifestConstants.RewriteStatusNotApplied);
            await WriteJsonFileAsync(Path.Combine(stagingPath, WorkspaceManifestConstants.SourceManifestFileName), sourceManifest, cancellationToken).ConfigureAwait(false);
            await WriteJsonFileAsync(Path.Combine(stagingPath, WorkspaceManifestConstants.ExtractionManifestFileName), extractionManifest, cancellationToken).ConfigureAwait(false);
            await WriteJsonFileAsync(Path.Combine(stagingPath, WorkspaceManifestConstants.RewriteManifestFileName), rewriteManifest, cancellationToken).ConfigureAwait(false);

            Report(request, WorkspaceProgressStage.ValidatingOutputs, "Validating completed workspace outputs.");
            var validation = await ValidateWorkspaceAsync(
                stagingPath,
                keyText,
                request,
                cancellationToken,
                request.Options.ValidateWrittenPayloadHashes).ConfigureAwait(false);
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

    private static WorkspaceSourceManifest CreateSourceManifest(WorkspacePreparationRequest request, string keyText) =>
        WorkspaceManifestValidator.CreateSourceManifest(request.Candidate, keyText, request.Options.ManifestSchema);

    private static async ValueTask<CacheValidation> ValidateWorkspaceAsync(
        string workspacePath,
        string keyText,
        WorkspacePreparationRequest request,
        CancellationToken cancellationToken,
        bool validatePayloadHashes = true)
    {
        var validation = await WorkspaceManifestValidator.ValidateAsync(
            workspacePath,
            keyText,
            request.Candidate,
            new WorkspaceManifestValidationExpectations(
                request.Options.ManifestSchema,
                request.Options.ExtractorSchema,
                request.Options.RewriterRecipe,
                WorkspaceManifestConstants.RewriteStatusNotApplied,
                request.Options.SmapiBuildId),
            cancellationToken,
            validatePayloadHashes).ConfigureAwait(false);
        return validation.IsValid
            ? new CacheValidation(
                true,
                validation.ExtractionManifest!.Statistics,
                validation.ExtractionManifest.Files,
                validation.TotalBytes)
            : CacheValidation.Invalid;
    }

    private static bool IdentityEquals(GameInstallationCandidate original, GameInstallationCandidate fresh) =>
        WorkspaceManifestValidator.CandidateIdentityEquals(original, fresh, includeSourcePaths: false);

    private static ValidatedExecutionPlan CreateExecutionPlan(
        GameInstallationCandidate candidate,
        string workspaceKey,
        string workspacePath,
        IReadOnlyList<WorkspaceExtractedFileManifest> files)
    {
        var payloads = files
            .Select(static file => new ValidatedWorkspacePayload(file.Kind, file.RelativePath, file.Size, file.Sha256))
            .ToArray();
        var installation = candidate.Installation;
        return new ValidatedExecutionPlan(
            installation.PackageName,
            installation.VersionName,
            installation.LongVersionCode,
            installation.SelectedAbi,
            workspaceKey,
            workspacePath,
            WorkspaceExecutionValidator.CreateIdentityDigest(
                candidate,
                workspaceKey,
                WorkspacePreparationOptions.DefaultManifestSchema,
                WorkspacePreparationOptions.DefaultExtractorSchema,
                WorkspaceExecutionTrustDefaults.Gate0RewriteRecipe,
                WorkspaceExecutionTrustDefaults.Gate0RewriteStatus,
                payloads),
            DateTimeOffset.UtcNow,
            payloads);
    }

    private static async ValueTask ActivateAsync(string root, string keyText, CancellationToken cancellationToken)
    {
        var statePath = Path.Combine(root, WorkspaceManifestConstants.StateFileName);
        WorkspaceState? current = null;
        if (File.Exists(statePath))
        {
            try
            {
                current = await WorkspaceJson.ReadBoundedAsync<WorkspaceState>(statePath, 64 * 1024, cancellationToken).ConfigureAwait(false);
                if (current is null || current.Format != WorkspaceManifestConstants.StateFormat || current.Schema != WorkspaceManifestConstants.StateSchema)
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

        var next = new WorkspaceState(
            WorkspaceManifestConstants.StateFormat,
            WorkspaceManifestConstants.StateSchema,
            keyText,
            current?.ActiveKey);
        var tempPath = Path.Combine(root, $".{WorkspaceManifestConstants.StateFileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(next, WorkspaceJson.Options);
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
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, WorkspaceJson.Options);
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
        WorkspacePreparationMetrics? metrics = null,
        ValidatedExecutionPlan? executionPlan = null) =>
        new(status, workspacePath, key, diagnostics, statistics, metrics, executionPlan);

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

    private sealed record CacheValidation(
        bool IsValid,
        WorkspaceExtractionStatistics? Statistics,
        IReadOnlyList<WorkspaceExtractedFileManifest>? Files,
        long TotalBytes)
    {
        public static CacheValidation Invalid { get; } = new(false, null, null, 0);
    }

    private sealed class OpenedSource : IDisposable
    {
        private readonly bool ownsResources;

        public OpenedSource(
            ApkSourceIdentity identity,
            ApkSourceInventory inventory,
            FileStream stream,
            ZipArchive archive,
            bool ownsResources)
        {
            Identity = identity;
            Inventory = inventory;
            Stream = stream;
            Archive = archive;
            this.ownsResources = ownsResources;
        }

        public ApkSourceIdentity Identity { get; }
        public ApkSourceInventory Inventory { get; }
        public FileStream Stream { get; }
        public ZipArchive Archive { get; }

        public void Dispose()
        {
            if (ownsResources)
            {
                Archive.Dispose();
                Stream.Dispose();
            }
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
