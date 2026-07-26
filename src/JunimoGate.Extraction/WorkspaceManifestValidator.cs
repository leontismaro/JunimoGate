using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using JunimoGate.Core;

namespace JunimoGate.Extraction;

internal static class WorkspaceManifestConstants
{
    public const string SourceManifestFileName = "source-manifest.json";
    public const string ExtractionManifestFileName = "extraction-manifest.json";
    public const string RewriteManifestFileName = "rewrite-manifest.json";
    public const string StateFileName = "workspace-state.json";
    public const string StateFormat = "junimogate-workspace-state";
    public const string StateSchema = "v1";
    public const string SourceManifestFormat = "junimogate-source-manifest";
    public const string ExtractionManifestFormat = "junimogate-extraction-manifest";
    public const string RewriteManifestFormat = "junimogate-rewrite-manifest";
    public const string RewriteStatusNotApplied = "not-applied";
    public const string NoSmapiBuildId = "none";
}

internal static class WorkspaceJson
{
    private const int MaximumJsonDepth = 64;

    internal static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.General)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
    };

    internal static async ValueTask<T?> ReadBoundedAsync<T>(
        string path,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        if (maximumBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        }

        RejectReparsePoint(path);
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > maximumBytes)
        {
            throw new InvalidDataException("JSON input exceeds its bounded size limit.");
        }

        var length = checked((int)stream.Length);
        var bytes = new byte[length];
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        var documentOptions = new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = MaximumJsonDepth,
        };
        using var document = JsonDocument.Parse(bytes, documentOptions);
        RejectDuplicateProperties(document.RootElement);
        ValidateKnownContractShape<T>(document.RootElement);
        return JsonSerializer.Deserialize<T>(bytes, Options);
    }

    internal static void RejectReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("Symbolic links and reparse points are not valid workspace inputs.");
        }
    }

    private static void ValidateKnownContractShape<T>(JsonElement root)
    {
        if (typeof(T) == typeof(WorkspaceState))
        {
            RequireExactObject(root, ["format", "schema", "activeKey", "previousKey"]);
            return;
        }

        if (typeof(T) == typeof(WorkspaceSourceManifest))
        {
            RequireExactObject(root, ["format", "schema", "cacheKey", "packageName", "versionName", "longVersionCode", "abi", "signers", "sources"]);
            RequireExactObject(root.GetProperty("signers"), ["current", "history"]);
            foreach (var source in RequireArray(root.GetProperty("sources")))
            {
                RequireExactObject(source, ["label", "splitName", "sha256", "size"]);
            }

            return;
        }

        if (typeof(T) == typeof(WorkspaceExtractionManifest))
        {
            RequireExactObject(root, ["format", "schema", "cacheKey", "extractorSchema", "rewriterRecipe", "smapiBuildId", "files", "statistics"]);
            foreach (var file in RequireArray(root.GetProperty("files")))
            {
                RequireExactObject(file, ["kind", "relativePath", "size", "sha256", "sourceLabel", "sourceEntry"]);
            }

            RequireExactObject(root.GetProperty("statistics"), ["contentFileCount", "contentBytes", "assemblyFileCount", "assemblyBytes"]);
            return;
        }

        if (typeof(T) == typeof(WorkspaceRewriteManifest))
        {
            RequireExactObject(root, ["format", "schema", "cacheKey", "recipe", "status"]);
        }
    }

    private static IEnumerable<JsonElement> RequireArray(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("The JSON contract requires an array.");
        }

        return element.EnumerateArray();
    }

    private static void RequireExactObject(JsonElement element, IEnumerable<string> expectedNames)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("The JSON contract requires an object.");
        }

        var expected = expectedNames.ToHashSet(StringComparer.Ordinal);
        var actual = element.EnumerateObject().Select(static property => property.Name).ToHashSet(StringComparer.Ordinal);
        if (!actual.SetEquals(expected))
        {
            throw new JsonException("The JSON object does not match its exact contract shape.");
        }
    }

    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new JsonException("Duplicate JSON properties are not allowed.");
                }

                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                RejectDuplicateProperties(item);
            }
        }
    }
}

internal enum WorkspaceManifestValidationFailure
{
    None,
    ManifestInvalid,
    ManifestSchemaMismatch,
    SourceIdentityMismatch,
    RecipeMismatch,
    StatusMismatch,
    FileSetMismatch,
    PayloadHashMismatch,
}

internal sealed record WorkspaceManifestValidationExpectations(
    string ManifestSchema,
    string ExtractorSchema,
    string RewriteRecipe,
    string RewriteStatus,
    string SmapiBuildId);

internal sealed record WorkspaceManifestValidationResult(
    WorkspaceManifestValidationFailure Failure,
    WorkspaceExtractionManifest? ExtractionManifest,
    long TotalBytes)
{
    public bool IsValid => Failure == WorkspaceManifestValidationFailure.None;

    public static WorkspaceManifestValidationResult Invalid(WorkspaceManifestValidationFailure failure) =>
        new(failure, null, 0);
}

internal static class WorkspaceManifestValidator
{
    private const int MaximumSourceManifestBytes = 1024 * 1024;
    private const int MaximumExtractionManifestBytes = 64 * 1024 * 1024;
    private const int MaximumRewriteManifestBytes = 1024 * 1024;

    internal static WorkspaceSourceManifest CreateSourceManifest(
        GameInstallationCandidate candidate,
        string keyText,
        string manifestSchema)
    {
        var installation = candidate.Installation;
        return new WorkspaceSourceManifest(
            WorkspaceManifestConstants.SourceManifestFormat,
            manifestSchema,
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

    internal static async ValueTask<WorkspaceManifestValidationResult> ValidateAsync(
        string workspacePath,
        string keyText,
        GameInstallationCandidate candidate,
        WorkspaceManifestValidationExpectations expectations,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!Directory.Exists(workspacePath))
            {
                return WorkspaceManifestValidationResult.Invalid(WorkspaceManifestValidationFailure.FileSetMismatch);
            }

            RejectReparsePointsOnPath(workspacePath);
            var sourceManifest = await WorkspaceJson.ReadBoundedAsync<WorkspaceSourceManifest>(
                Path.Combine(workspacePath, WorkspaceManifestConstants.SourceManifestFileName),
                MaximumSourceManifestBytes,
                cancellationToken).ConfigureAwait(false);
            var extractionManifest = await WorkspaceJson.ReadBoundedAsync<WorkspaceExtractionManifest>(
                Path.Combine(workspacePath, WorkspaceManifestConstants.ExtractionManifestFileName),
                MaximumExtractionManifestBytes,
                cancellationToken).ConfigureAwait(false);
            var rewriteManifest = await WorkspaceJson.ReadBoundedAsync<WorkspaceRewriteManifest>(
                Path.Combine(workspacePath, WorkspaceManifestConstants.RewriteManifestFileName),
                MaximumRewriteManifestBytes,
                cancellationToken).ConfigureAwait(false);

            if (sourceManifest is null || extractionManifest is null || rewriteManifest is null ||
                sourceManifest.Format != WorkspaceManifestConstants.SourceManifestFormat ||
                extractionManifest.Format != WorkspaceManifestConstants.ExtractionManifestFormat ||
                rewriteManifest.Format != WorkspaceManifestConstants.RewriteManifestFormat)
            {
                return WorkspaceManifestValidationResult.Invalid(WorkspaceManifestValidationFailure.ManifestInvalid);
            }

            if (sourceManifest.Schema != expectations.ManifestSchema ||
                extractionManifest.Schema != expectations.ManifestSchema ||
                rewriteManifest.Schema != expectations.ManifestSchema ||
                extractionManifest.ExtractorSchema != expectations.ExtractorSchema)
            {
                return WorkspaceManifestValidationResult.Invalid(WorkspaceManifestValidationFailure.ManifestSchemaMismatch);
            }

            if (!SourceManifestMatches(
                    sourceManifest,
                    CreateSourceManifest(candidate, keyText, expectations.ManifestSchema)))
            {
                return WorkspaceManifestValidationResult.Invalid(WorkspaceManifestValidationFailure.SourceIdentityMismatch);
            }

            if (extractionManifest.CacheKey != keyText || rewriteManifest.CacheKey != keyText ||
                extractionManifest.SmapiBuildId != expectations.SmapiBuildId ||
                extractionManifest.Files is null || extractionManifest.Statistics is null)
            {
                return WorkspaceManifestValidationResult.Invalid(WorkspaceManifestValidationFailure.ManifestInvalid);
            }

            if (extractionManifest.RewriterRecipe != expectations.RewriteRecipe ||
                rewriteManifest.Recipe != expectations.RewriteRecipe)
            {
                return WorkspaceManifestValidationResult.Invalid(WorkspaceManifestValidationFailure.RecipeMismatch);
            }

            if (rewriteManifest.Status != expectations.RewriteStatus)
            {
                return WorkspaceManifestValidationResult.Invalid(WorkspaceManifestValidationFailure.StatusMismatch);
            }

            var expectedFiles = new HashSet<string>(StringComparer.Ordinal);
            foreach (var file in extractionManifest.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsValidManifestFile(file, candidate) || !expectedFiles.Add(file.RelativePath) ||
                    file.Size < 0 || !Sha256Digest.TryParse(file.Sha256, out _))
                {
                    return WorkspaceManifestValidationResult.Invalid(WorkspaceManifestValidationFailure.ManifestInvalid);
                }
            }

            try
            {
                RequireOutputs(extractionManifest.Files);
            }
            catch (WorkspacePreparationException)
            {
                return WorkspaceManifestValidationResult.Invalid(WorkspaceManifestValidationFailure.ManifestInvalid);
            }

            var actualFiles = EnumerateWorkspaceFiles(workspacePath);
            actualFiles.Remove(WorkspaceManifestConstants.SourceManifestFileName);
            actualFiles.Remove(WorkspaceManifestConstants.ExtractionManifestFileName);
            actualFiles.Remove(WorkspaceManifestConstants.RewriteManifestFileName);
            if (!actualFiles.SetEquals(expectedFiles))
            {
                return WorkspaceManifestValidationResult.Invalid(WorkspaceManifestValidationFailure.FileSetMismatch);
            }

            foreach (var file in extractionManifest.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var fullPath = GetContainedPayloadPath(workspacePath, file.RelativePath);
                RejectReparsePointsOnPath(fullPath, workspacePath);
                var info = new FileInfo(fullPath);
                if (!info.Exists || info.Length != file.Size)
                {
                    return WorkspaceManifestValidationResult.Invalid(WorkspaceManifestValidationFailure.PayloadHashMismatch);
                }

                await using var stream = new FileStream(
                    fullPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    128 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                var hash = Convert.ToHexStringLower(
                    await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
                if (!hash.Equals(file.Sha256, StringComparison.Ordinal))
                {
                    return WorkspaceManifestValidationResult.Invalid(WorkspaceManifestValidationFailure.PayloadHashMismatch);
                }
            }

            var computedStatistics = new WorkspaceExtractionStatistics(
                extractionManifest.Files.Count(static output => output.Kind == "content"),
                extractionManifest.Files.Where(static output => output.Kind == "content").Sum(static output => output.Size),
                extractionManifest.Files.Count(static output => output.Kind == "assembly"),
                extractionManifest.Files.Where(static output => output.Kind == "assembly").Sum(static output => output.Size));
            if (computedStatistics != extractionManifest.Statistics)
            {
                return WorkspaceManifestValidationResult.Invalid(WorkspaceManifestValidationFailure.ManifestInvalid);
            }

            var totalBytes = Directory.EnumerateFiles(workspacePath, "*", SearchOption.AllDirectories)
                .Sum(static path => new FileInfo(path).Length);
            return new WorkspaceManifestValidationResult(
                WorkspaceManifestValidationFailure.None,
                extractionManifest,
                totalBytes);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or CryptographicException or OverflowException)
        {
            return WorkspaceManifestValidationResult.Invalid(WorkspaceManifestValidationFailure.ManifestInvalid);
        }
    }

    internal static bool CandidateIdentityEquals(
        GameInstallationCandidate leftCandidate,
        GameInstallationCandidate rightCandidate,
        bool includeSourcePaths)
    {
        var left = leftCandidate.Installation;
        var right = rightCandidate.Installation;
        if (left.PackageName != right.PackageName ||
            left.VersionName != right.VersionName ||
            left.LongVersionCode != right.LongVersionCode ||
            left.SelectedAbi != right.SelectedAbi ||
            !left.SigningIdentity.CurrentSignerDigests.SequenceEqual(right.SigningIdentity.CurrentSignerDigests) ||
            !left.SigningIdentity.RotationHistory.SequenceEqual(right.SigningIdentity.RotationHistory) ||
            left.ApkSources.Count != right.ApkSources.Count)
        {
            return false;
        }

        for (var index = 0; index < left.ApkSources.Count; index++)
        {
            var leftSource = left.ApkSources[index];
            var rightSource = right.ApkSources[index];
            if (leftSource.Label != rightSource.Label ||
                leftSource.SplitName != rightSource.SplitName ||
                leftSource.Digest != rightSource.Digest ||
                leftSource.Size != rightSource.Size ||
                (includeSourcePaths && leftSource.SourcePath != rightSource.SourcePath))
            {
                return false;
            }
        }

        if (leftCandidate.SourceInventories.Count != rightCandidate.SourceInventories.Count)
        {
            return false;
        }

        for (var index = 0; index < leftCandidate.SourceInventories.Count; index++)
        {
            var leftInventory = leftCandidate.SourceInventories[index];
            var rightInventory = rightCandidate.SourceInventories[index];
            if (leftInventory.SourceLabel != rightInventory.SourceLabel ||
                !leftInventory.Roles.SequenceEqual(rightInventory.Roles, StringComparer.Ordinal) ||
                !leftInventory.NativeAbis.SequenceEqual(rightInventory.NativeAbis, StringComparer.Ordinal) ||
                !leftInventory.AssemblyStoreAbis.SequenceEqual(rightInventory.AssemblyStoreAbis, StringComparer.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    internal static void RequireOutputs(IReadOnlyList<WorkspaceExtractedFileManifest> outputs)
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

    internal static bool IsSafeWorkspaceRelativePath(string path)
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

    private static bool SourceManifestMatches(WorkspaceSourceManifest actual, WorkspaceSourceManifest expected) =>
        actual.Format == expected.Format &&
        actual.Schema == expected.Schema &&
        actual.CacheKey == expected.CacheKey &&
        actual.PackageName == expected.PackageName &&
        actual.VersionName == expected.VersionName &&
        actual.LongVersionCode == expected.LongVersionCode &&
        actual.Abi == expected.Abi &&
        actual.Signers is not null && actual.Signers.Current is not null && actual.Signers.History is not null &&
        actual.Signers.Current.SequenceEqual(expected.Signers.Current, StringComparer.Ordinal) &&
        actual.Signers.History.SequenceEqual(expected.Signers.History, StringComparer.Ordinal) &&
        actual.Sources is not null &&
        actual.Sources.SequenceEqual(expected.Sources);

    private static bool IsValidManifestFile(
        WorkspaceExtractedFileManifest? file,
        GameInstallationCandidate candidate)
    {
        if (file is null || !IsSafeWorkspaceRelativePath(file.RelativePath) ||
            string.IsNullOrWhiteSpace(file.SourceLabel) || string.IsNullOrWhiteSpace(file.SourceEntry) ||
            !IsSafeWorkspaceRelativePath(file.SourceEntry))
        {
            return false;
        }

        var inventory = candidate.SourceInventories.FirstOrDefault(source => source.SourceLabel == file.SourceLabel);
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
            abi.Equals(candidate.Installation.SelectedAbi, StringComparison.OrdinalIgnoreCase) &&
            inventory.Roles.Contains(ApkSourceRoleNames.ModernAssemblyBlob, StringComparer.Ordinal);
    }

    private static HashSet<string> EnumerateWorkspaceFiles(string workspacePath)
    {
        var files = new HashSet<string>(StringComparer.Ordinal);
        var pendingDirectories = new Stack<string>();
        pendingDirectories.Push(workspacePath);
        while (pendingDirectories.TryPop(out var directory))
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    // Check before recursing so validation never follows a workspace link outside the active tree.
                    throw new InvalidDataException("Workspace reparse points are not allowed.");
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pendingDirectories.Push(entry);
                    continue;
                }

                var relativePath = Path.GetRelativePath(workspacePath, entry)
                    .Replace(Path.DirectorySeparatorChar, '/');
                if (!files.Add(relativePath))
                {
                    throw new InvalidDataException("Workspace files must have unique canonical paths.");
                }
            }
        }

        return files;
    }

    private static string GetContainedPayloadPath(string workspacePath, string relativePath)
    {
        var canonicalWorkspace = Path.GetFullPath(workspacePath);
        var fullPath = Path.GetFullPath(Path.Combine(
            canonicalWorkspace,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = canonicalWorkspace.EndsWith(Path.DirectorySeparatorChar)
            ? canonicalWorkspace
            : canonicalWorkspace + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Workspace payload path escapes the active workspace.");
        }

        return fullPath;
    }

    private static void RejectReparsePointsOnPath(string path, string? stopAt = null)
    {
        var current = Path.GetFullPath(path);
        var stop = stopAt is null ? null : Path.GetFullPath(stopAt);
        while (true)
        {
            WorkspaceJson.RejectReparsePoint(current);
            if (stop is null || current.Equals(stop, StringComparison.Ordinal))
            {
                break;
            }

            var parent = Directory.GetParent(current)?.FullName;
            if (parent is null || !current.StartsWith(stop + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Workspace path is not contained by its active workspace.");
            }

            current = parent;
        }
    }
}
