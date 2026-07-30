using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace JunimoGate.Rewriter;

/// <summary>Stable applied-workspace validation codes.</summary>
public static class GameHostAppliedWorkspaceErrorCodes
{
    public const string ManifestInvalid = "gamehost_applied_manifest_invalid";
    public const string IdentityMismatch = "gamehost_applied_identity_mismatch";
    public const string SourceBindingInvalid = "gamehost_applied_source_binding_invalid";
    public const string InputInvalid = "gamehost_applied_input_invalid";
    public const string MutationInvalid = "gamehost_applied_mutation_invalid";
    public const string OutputInvalid = "gamehost_applied_output_invalid";
    public const string PostValidationFailed = "gamehost_applied_post_validation_failed";
    public const string FileSetMismatch = "gamehost_applied_file_set_mismatch";
    public const string RecipeMismatch = "gamehost_applied_recipe_mismatch";
    public const string RecoveryStateInvalid = "gamehost_applied_recovery_state_invalid";
}

public static class GameHostAppliedWorkspaceContract
{
    public const string AppliedManifestFileName = "applied-workspace-manifest.json";
    public const string RewriteManifestFileName = "rewrite-manifest.json";
    public const string StateFileName = "applied-workspace-state.json";
    public const string AppliedManifestFormat = "junimogate-applied-workspace-manifest";
    public const string AppliedManifestSchema = "v2";
    public const string RewriteManifestFormat = "junimogate-rewrite-manifest";
    public const string RewriteManifestSchema = "v3";
    public const string RewriteStatusApplied = "applied";
    public const string PostValidationPassed = "passed";
    public const string StateFormat = "junimogate-applied-workspace-state";
    public const string StateSchema = "v2";
    public const string OriginalPayloadSetSchema = "junimogate-original-payload-set/v1";
    public const string AppliedWorkspaceKeySchema = "junimogate-applied-workspace-key/v2";
    public const string PinnedMonoCecilVersion = "0.11.6";
    public const int MaximumRewriteInputs = 64;
    public const int MaximumMutations = 4_096;
    public const int MaximumOverlayFiles = 256;
}

/// <summary>One immutable payload identity from the validated M4 extraction manifest.</summary>
public sealed record OriginalPayloadIdentity(
    string Kind,
    string RelativePath,
    long Size,
    string Sha256);

/// <summary>Deterministic identity of the complete original M4 payload set.</summary>
public sealed record OriginalPayloadSetSummary(
    string Schema,
    string Digest,
    int FileCount,
    long TotalBytes);

/// <summary>Hashes the exact original M4 payload inventory without copying its bytes.</summary>
public static class OriginalPayloadSetIdentity
{
    public static OriginalPayloadSetSummary Create(IEnumerable<OriginalPayloadIdentity> payloads)
    {
        ArgumentNullException.ThrowIfNull(payloads);
        var materialized = payloads.ToImmutableArray();
        if (materialized.IsDefaultOrEmpty)
        {
            throw new ArgumentException("The original payload set cannot be empty.", nameof(payloads));
        }

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long totalBytes = 0;
        foreach (var payload in materialized)
        {
            if (payload is null ||
                !GameHostAppliedWorkspaceValidator.IsValidOriginalPayloadPath(payload.Kind, payload.RelativePath) ||
                payload.Size < 0 ||
                !GameHostAppliedWorkspaceValidator.IsCanonicalSha256(payload.Sha256) ||
                !paths.Add(payload.RelativePath))
            {
                throw new ArgumentException(
                    "Original payload identities must be unique, canonical Content or assembly files.",
                    nameof(payloads));
            }

            totalBytes = checked(totalBytes + payload.Size);
        }

        var canonical = new AppliedCanonicalHashBuilder();
        canonical.Add("schema", GameHostAppliedWorkspaceContract.OriginalPayloadSetSchema);
        canonical.AddArray(
            "payload",
            materialized
                .OrderBy(static payload => payload.RelativePath, StringComparer.Ordinal)
                .Select(static payload => AppliedCanonicalHashBuilder.EncodeFields([
                    ("kind", payload.Kind),
                    ("path", payload.RelativePath),
                    ("size", payload.Size.ToString(CultureInfo.InvariantCulture)),
                    ("sha256", payload.Sha256),
                ])));
        return new OriginalPayloadSetSummary(
            GameHostAppliedWorkspaceContract.OriginalPayloadSetSchema,
            canonical.GetHash(),
            materialized.Length,
            totalBytes);
    }
}

/// <summary>Binds an applied workspace to the exact validated M4 source workspace and manifests.</summary>
public sealed record AppliedSourceWorkspaceBinding(
    string WorkspaceKey,
    string SourceManifestSha256,
    string ExtractionManifestSha256,
    string RewriteManifestV1Sha256,
    OriginalPayloadSetSummary OriginalPayloadSet);

public sealed record AppliedRewriterToolIdentity(
    string BuildId,
    string MonoCecilVersion);

public sealed record AppliedRewriteInputIdentity(
    string RelativePath,
    string AssemblyIdentity,
    long Size,
    string Sha256);

/// <summary>Local structural evidence for one semantic bridge rule.</summary>
public sealed record AppliedRewriteMutationEvidence(
    string MutationId,
    string InputRelativePath,
    string TargetMemberSignature,
    int ExpectedMatchCount,
    int ObservedMatchCount,
    IReadOnlyList<string> Replacements,
    bool PostconditionPassed);

/// <summary>One rewritten managed overlay file. Original M4 payloads are never overwritten.</summary>
public sealed record AppliedRewriteOutputIdentity(
    string InputRelativePath,
    string OverlayRelativePath,
    string AssemblyIdentity,
    long Size,
    string Sha256);

public sealed record AppliedRewritePostValidation(
    string Status,
    bool ReopenedWithIndependentReader,
    bool LocalGuardsPassed,
    bool PostconditionsPassed,
    bool AssemblyIdentityPassed,
    bool ReferenceClosurePassed);

/// <summary>Rewrite manifest v3. It describes a staged semantic-rule result, not authorization to produce one.</summary>
public sealed record GameHostRewriteManifestV2(
    string Format,
    string Schema,
    string AppliedWorkspaceKey,
    AppliedSourceWorkspaceBinding Source,
    RewriteRecipeIdentity Recipe,
    string Status,
    AppliedRewriterToolIdentity Tool,
    IReadOnlyList<AppliedRewriteInputIdentity> Inputs,
    IReadOnlyList<AppliedRewriteMutationEvidence> Mutations,
    IReadOnlyList<AppliedRewriteOutputIdentity> Outputs,
    AppliedRewritePostValidation PostValidation);

public sealed record AppliedOverlayFileIdentity(
    string Kind,
    string RelativePath,
    long Size,
    string Sha256);

/// <summary>Exact applied-directory inventory: this manifest, rewrite manifest, and listed overlay files only.</summary>
public sealed record GameHostAppliedWorkspaceManifest(
    string Format,
    string Schema,
    string AppliedWorkspaceKey,
    string SourceWorkspaceKey,
    RewriteRecipeIdentity Recipe,
    string RewriteManifestSha256,
    string OriginalPayloadSetSha256,
    IReadOnlyList<AppliedOverlayFileIdentity> OverlayFiles);

public sealed record GameHostAppliedWorkspaceState(
    string Format,
    string Schema,
    string? ActiveKey,
    string? PreviousKey);

public sealed record GameHostAppliedWorkspaceValidationResult(
    bool IsValid,
    ImmutableArray<string> ErrorCodes);

/// <summary>Creates a content-addressed applied key after staged output and postconditions are known.</summary>
public static class GameHostAppliedWorkspaceKey
{
    public static string Create(
        AppliedSourceWorkspaceBinding source,
        RewriteRecipeIdentity recipe,
        AppliedRewriterToolIdentity tool,
        IEnumerable<AppliedRewriteInputIdentity> inputs,
        IEnumerable<AppliedRewriteMutationEvidence> mutations,
        IEnumerable<AppliedRewriteOutputIdentity> outputs)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(recipe);
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(mutations);
        ArgumentNullException.ThrowIfNull(outputs);

        var inputArray = inputs.ToImmutableArray();
        var mutationArray = mutations.ToImmutableArray();
        var outputArray = outputs.ToImmutableArray();
        if (!GameHostAppliedWorkspaceValidator.IsValidSourceBinding(source) ||
            !GameHostAppliedWorkspaceValidator.IsCanonicalRecipe(recipe) ||
            !GameHostAppliedWorkspaceValidator.IsValidTool(tool) ||
            !GameHostAppliedWorkspaceValidator.AreValidInputs(inputArray) ||
            !GameHostAppliedWorkspaceValidator.AreValidMutations(mutationArray, inputArray) ||
            !GameHostAppliedWorkspaceValidator.AreValidOutputs(outputArray, inputArray))
        {
            throw new ArgumentException("Applied workspace key inputs are malformed or inconsistent.");
        }

        var canonical = new AppliedCanonicalHashBuilder();
        canonical.Add("schema", GameHostAppliedWorkspaceContract.AppliedWorkspaceKeySchema);
        canonical.Add("source.workspaceKey", source.WorkspaceKey);
        canonical.Add("source.sourceManifest", source.SourceManifestSha256);
        canonical.Add("source.extractionManifest", source.ExtractionManifestSha256);
        canonical.Add("source.rewriteManifestV1", source.RewriteManifestV1Sha256);
        canonical.Add("source.payloadSetSchema", source.OriginalPayloadSet.Schema);
        canonical.Add("source.payloadSetDigest", source.OriginalPayloadSet.Digest);
        canonical.Add("source.payloadCount", source.OriginalPayloadSet.FileCount.ToString(CultureInfo.InvariantCulture));
        canonical.Add("source.payloadBytes", source.OriginalPayloadSet.TotalBytes.ToString(CultureInfo.InvariantCulture));
        canonical.Add("recipe.name", recipe.Name);
        canonical.Add("recipe.version", recipe.Version);
        canonical.Add("tool.buildId", tool.BuildId);
        canonical.Add("tool.cecil", tool.MonoCecilVersion);
        canonical.AddArray(
            "input",
            inputArray.OrderBy(static input => input.RelativePath, StringComparer.Ordinal)
                .Select(FormatInput));
        canonical.AddArray(
            "mutation",
            mutationArray.OrderBy(static mutation => mutation.MutationId, StringComparer.Ordinal)
                .Select(FormatMutation));
        canonical.AddArray(
            "output",
            outputArray.OrderBy(static output => output.OverlayRelativePath, StringComparer.Ordinal)
                .Select(FormatOutput));
        return canonical.GetHash();
    }

    private static string FormatInput(AppliedRewriteInputIdentity input) =>
        AppliedCanonicalHashBuilder.EncodeFields([
            ("path", input.RelativePath),
            ("identity", input.AssemblyIdentity),
            ("size", input.Size.ToString(CultureInfo.InvariantCulture)),
            ("sha256", input.Sha256),
        ]);

    private static string FormatMutation(AppliedRewriteMutationEvidence mutation) =>
        AppliedCanonicalHashBuilder.EncodeFields([
            ("id", mutation.MutationId),
            ("input", mutation.InputRelativePath),
            ("target", mutation.TargetMemberSignature),
            ("expectedMatches", mutation.ExpectedMatchCount.ToString(CultureInfo.InvariantCulture)),
            ("observedMatches", mutation.ObservedMatchCount.ToString(CultureInfo.InvariantCulture)),
            ("replacements", string.Join('\n', mutation.Replacements.Order(StringComparer.Ordinal))),
            ("postcondition", mutation.PostconditionPassed.ToString(CultureInfo.InvariantCulture)),
        ]);

    private static string FormatOutput(AppliedRewriteOutputIdentity output) =>
        AppliedCanonicalHashBuilder.EncodeFields([
            ("input", output.InputRelativePath),
            ("overlay", output.OverlayRelativePath),
            ("identity", output.AssemblyIdentity),
            ("size", output.Size.ToString(CultureInfo.InvariantCulture)),
            ("sha256", output.Sha256),
        ]);
}

/// <summary>Pure applied-workspace contract validation. It performs no file I/O, rewrite, load, rename, or activation.</summary>
public static class GameHostAppliedWorkspaceValidator
{
    public static GameHostAppliedWorkspaceValidationResult ValidateShape(
        GameHostRewriteManifestV2 rewrite,
        GameHostAppliedWorkspaceManifest applied,
        IEnumerable<OriginalPayloadIdentity> originalPayloads,
        IEnumerable<string> actualAppliedRelativeFiles)
    {
        ArgumentNullException.ThrowIfNull(rewrite);
        ArgumentNullException.ThrowIfNull(applied);
        ArgumentNullException.ThrowIfNull(originalPayloads);
        ArgumentNullException.ThrowIfNull(actualAppliedRelativeFiles);
        var errors = new HashSet<string>(StringComparer.Ordinal);

        var originalPayloadArray = originalPayloads.ToImmutableArray();
        var inputs = rewrite.Inputs?.ToImmutableArray() ?? [];
        var mutations = rewrite.Mutations?.ToImmutableArray() ?? [];
        var outputs = rewrite.Outputs?.ToImmutableArray() ?? [];
        var overlays = applied.OverlayFiles?.ToImmutableArray() ?? [];

        if (!string.Equals(rewrite.Format, GameHostAppliedWorkspaceContract.RewriteManifestFormat, StringComparison.Ordinal) ||
            !string.Equals(rewrite.Schema, GameHostAppliedWorkspaceContract.RewriteManifestSchema, StringComparison.Ordinal) ||
            !string.Equals(applied.Format, GameHostAppliedWorkspaceContract.AppliedManifestFormat, StringComparison.Ordinal) ||
            !string.Equals(applied.Schema, GameHostAppliedWorkspaceContract.AppliedManifestSchema, StringComparison.Ordinal) ||
            !string.Equals(rewrite.Status, GameHostAppliedWorkspaceContract.RewriteStatusApplied, StringComparison.Ordinal) ||
            rewrite.Recipe is null || applied.Recipe is null || rewrite.Tool is null || rewrite.Source is null)
        {
            errors.Add(GameHostAppliedWorkspaceErrorCodes.ManifestInvalid);
        }

        if (!IsValidSourceBinding(rewrite.Source))
        {
            errors.Add(GameHostAppliedWorkspaceErrorCodes.SourceBindingInvalid);
        }

        OriginalPayloadSetSummary? computedOriginalPayloadSet = null;
        try
        {
            computedOriginalPayloadSet = OriginalPayloadSetIdentity.Create(originalPayloadArray);
        }
        catch (Exception exception) when (exception is ArgumentException or OverflowException)
        {
            errors.Add(GameHostAppliedWorkspaceErrorCodes.SourceBindingInvalid);
        }

        if (computedOriginalPayloadSet is not null &&
            (rewrite.Source?.OriginalPayloadSet is null ||
                rewrite.Source.OriginalPayloadSet != computedOriginalPayloadSet))
        {
            errors.Add(GameHostAppliedWorkspaceErrorCodes.SourceBindingInvalid);
        }

        if (!AreValidInputs(inputs) ||
            !InputsMatchOriginalPayloads(inputs, originalPayloadArray))
        {
            errors.Add(GameHostAppliedWorkspaceErrorCodes.InputInvalid);
        }

        if (!AreValidMutations(mutations, inputs))
            errors.Add(GameHostAppliedWorkspaceErrorCodes.MutationInvalid);

        if (!AreValidOutputs(outputs, inputs))
        {
            errors.Add(GameHostAppliedWorkspaceErrorCodes.OutputInvalid);
        }

        if (rewrite.PostValidation is null ||
            !string.Equals(
                rewrite.PostValidation.Status,
                GameHostAppliedWorkspaceContract.PostValidationPassed,
                StringComparison.Ordinal) ||
            !rewrite.PostValidation.ReopenedWithIndependentReader ||
            !rewrite.PostValidation.LocalGuardsPassed ||
            !rewrite.PostValidation.PostconditionsPassed ||
            !rewrite.PostValidation.AssemblyIdentityPassed ||
            !rewrite.PostValidation.ReferenceClosurePassed)
        {
            errors.Add(GameHostAppliedWorkspaceErrorCodes.PostValidationFailed);
        }

        string? recomputedKey = null;
        if (errors.Count == 0)
        {
            try
            {
                recomputedKey = GameHostAppliedWorkspaceKey.Create(
                    rewrite.Source!,
                    rewrite.Recipe!,
                    rewrite.Tool!,
                    inputs,
                    mutations,
                    outputs);
            }
            catch (ArgumentException)
            {
                errors.Add(GameHostAppliedWorkspaceErrorCodes.IdentityMismatch);
            }
        }

        if (recomputedKey is null ||
            rewrite.Source is null ||
            rewrite.Recipe is null ||
            applied.Recipe is null ||
            !IsCanonicalSha256(rewrite.AppliedWorkspaceKey) ||
            !string.Equals(rewrite.AppliedWorkspaceKey, recomputedKey, StringComparison.Ordinal) ||
            !string.Equals(applied.AppliedWorkspaceKey, rewrite.AppliedWorkspaceKey, StringComparison.Ordinal) ||
            !string.Equals(applied.SourceWorkspaceKey, rewrite.Source.WorkspaceKey, StringComparison.Ordinal) ||
            applied.Recipe != rewrite.Recipe ||
            !IsCanonicalSha256(applied.RewriteManifestSha256) ||
            !string.Equals(
                applied.OriginalPayloadSetSha256,
                rewrite.Source.OriginalPayloadSet.Digest,
                StringComparison.Ordinal))
        {
            errors.Add(GameHostAppliedWorkspaceErrorCodes.IdentityMismatch);
        }

        var expectedOverlays = outputs
            .Where(static output => output is not null)
            .Select(static output => new AppliedOverlayFileIdentity(
                "managed-assembly",
                output.OverlayRelativePath,
                output.Size,
                output.Sha256))
            .OrderBy(static file => file.RelativePath, StringComparer.Ordinal)
            .ToImmutableArray();
        var overlaysContainNull = overlays.Any(static file => file is null);
        var actualOverlays = overlays
            .Where(static file => file is not null)
            .Select(static file => file!)
            .OrderBy(static file => file.RelativePath, StringComparer.Ordinal)
            .ToImmutableArray();
        if (overlaysContainNull ||
            actualOverlays.Length > GameHostAppliedWorkspaceContract.MaximumOverlayFiles ||
            !actualOverlays.SequenceEqual(expectedOverlays) ||
            actualOverlays.Any(static file =>
                !string.Equals(file.Kind, "managed-assembly", StringComparison.Ordinal) ||
                !IsValidOverlayPath(file.RelativePath) ||
                file.Size <= 0 ||
                !IsCanonicalSha256(file.Sha256)))
        {
            errors.Add(GameHostAppliedWorkspaceErrorCodes.OutputInvalid);
        }

        var suppliedFiles = actualAppliedRelativeFiles.ToArray();
        var actualFiles = suppliedFiles.ToHashSet(StringComparer.Ordinal);
        var caseFoldedFiles = suppliedFiles.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var expectedFiles = expectedOverlays.Select(static file => file.RelativePath)
            .Append(GameHostAppliedWorkspaceContract.AppliedManifestFileName)
            .Append(GameHostAppliedWorkspaceContract.RewriteManifestFileName)
            .ToHashSet(StringComparer.Ordinal);
        if (actualFiles.Count != suppliedFiles.Length ||
            caseFoldedFiles.Count != suppliedFiles.Length ||
            actualFiles.Any(static path => !IsSafeRelativePath(path)) ||
            !actualFiles.SetEquals(expectedFiles))
        {
            errors.Add(GameHostAppliedWorkspaceErrorCodes.FileSetMismatch);
        }

        return new GameHostAppliedWorkspaceValidationResult(
            errors.Count == 0,
            errors.Order(StringComparer.Ordinal).ToImmutableArray());
    }

    /// <summary>Checks that the manifest contains every local rule in the selected recipe exactly once.</summary>
    public static GameHostAppliedWorkspaceValidationResult ValidateRecipeResult(
        GameHostRewriteManifestV2 rewrite)
    {
        ArgumentNullException.ThrowIfNull(rewrite);
        var expectedRules = GameHostBridgeRecipe.Rules
            .OrderBy(static rule => rule.RuleId, StringComparer.Ordinal)
            .ToArray();
        var suppliedMutations = rewrite.Mutations?.ToArray();
        if (suppliedMutations is null || suppliedMutations.Any(static mutation => mutation is null))
        {
            return new GameHostAppliedWorkspaceValidationResult(
                false,
                [GameHostAppliedWorkspaceErrorCodes.RecipeMismatch]);
        }

        var actualRules = suppliedMutations
            .OrderBy(static mutation => mutation.MutationId, StringComparer.Ordinal)
            .ToArray();
        var matches = rewrite.Recipe == GameHostBridgeRecipe.Identity &&
            actualRules.Length == expectedRules.Length;
        if (matches)
        {
            for (var index = 0; index < expectedRules.Length; index++)
            {
                var expected = expectedRules[index];
                var actual = actualRules![index];
                if (actual is null || actual.MutationId != expected.RuleId ||
                    actual.InputRelativePath != expected.InputRelativePath ||
                    actual.TargetMemberSignature != expected.TargetMemberSignature ||
                    actual.ExpectedMatchCount != expected.ExpectedMatchCount ||
                    actual.ObservedMatchCount != expected.ExpectedMatchCount ||
                    !actual.PostconditionPassed ||
                    !actual.Replacements.Order(StringComparer.Ordinal)
                        .SequenceEqual(expected.Replacements.Order(StringComparer.Ordinal), StringComparer.Ordinal))
                {
                    matches = false;
                    break;
                }
            }
        }
        if (!matches)
        {
            return new GameHostAppliedWorkspaceValidationResult(
                false,
                [GameHostAppliedWorkspaceErrorCodes.RecipeMismatch]);
        }

        return new GameHostAppliedWorkspaceValidationResult(true, []);
    }

    internal static bool IsValidSourceBinding(AppliedSourceWorkspaceBinding? source) =>
        source is not null &&
        IsCanonicalSha256(source.WorkspaceKey) &&
        IsCanonicalSha256(source.SourceManifestSha256) &&
        IsCanonicalSha256(source.ExtractionManifestSha256) &&
        IsCanonicalSha256(source.RewriteManifestV1Sha256) &&
        source.OriginalPayloadSet is not null &&
        string.Equals(
            source.OriginalPayloadSet.Schema,
            GameHostAppliedWorkspaceContract.OriginalPayloadSetSchema,
            StringComparison.Ordinal) &&
        IsCanonicalSha256(source.OriginalPayloadSet.Digest) &&
        source.OriginalPayloadSet.FileCount > 0 &&
        source.OriginalPayloadSet.TotalBytes > 0;

    internal static bool IsValidTool(AppliedRewriterToolIdentity? tool) =>
        tool is not null &&
        IsCanonicalToken(tool.BuildId) &&
        string.Equals(
            tool.MonoCecilVersion,
            GameHostAppliedWorkspaceContract.PinnedMonoCecilVersion,
            StringComparison.Ordinal);

    internal static bool IsCanonicalRecipe(RewriteRecipeIdentity? recipe) =>
        recipe is not null && IsCanonicalToken(recipe.Name) && IsCanonicalToken(recipe.Version);

    internal static bool AreValidInputs(ImmutableArray<AppliedRewriteInputIdentity> inputs)
    {
        if (inputs.IsDefaultOrEmpty || inputs.Length > GameHostAppliedWorkspaceContract.MaximumRewriteInputs)
        {
            return false;
        }

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return inputs.All(input =>
            input is not null &&
            IsValidManagedInputPath(input.RelativePath) &&
            !string.IsNullOrWhiteSpace(input.AssemblyIdentity) &&
            input.AssemblyIdentity.Length <= 1_024 &&
            input.Size > 0 &&
            IsCanonicalSha256(input.Sha256) &&
            paths.Add(input.RelativePath));
    }

    internal static bool InputsMatchOriginalPayloads(
        ImmutableArray<AppliedRewriteInputIdentity> inputs,
        ImmutableArray<OriginalPayloadIdentity> originalPayloads)
    {
        if (!AreValidInputs(inputs) || originalPayloads.IsDefaultOrEmpty)
        {
            return false;
        }

        var originalsByPath = new Dictionary<string, OriginalPayloadIdentity>(StringComparer.Ordinal);
        foreach (var payload in originalPayloads)
        {
            if (payload is null ||
                !IsValidOriginalPayloadPath(payload.Kind, payload.RelativePath) ||
                payload.Size < 0 ||
                !IsCanonicalSha256(payload.Sha256))
            {
                return false;
            }

            if (!string.Equals(payload.Kind, "assembly", StringComparison.Ordinal))
            {
                continue;
            }

            if (!originalsByPath.TryAdd(payload.RelativePath, payload))
            {
                return false;
            }
        }

        return inputs.All(input =>
            originalsByPath.TryGetValue(input.RelativePath, out var original) &&
            input.Size == original.Size &&
            string.Equals(input.Sha256, original.Sha256, StringComparison.Ordinal));
    }

    internal static bool AreValidMutations(
        ImmutableArray<AppliedRewriteMutationEvidence> mutations,
        ImmutableArray<AppliedRewriteInputIdentity> inputs)
    {
        if (!AreValidInputs(inputs) ||
            mutations.IsDefaultOrEmpty ||
            mutations.Length > GameHostAppliedWorkspaceContract.MaximumMutations)
        {
            return false;
        }

        var inputPaths = inputs.Select(static input => input.RelativePath).ToHashSet(StringComparer.Ordinal);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        if (!mutations.All(mutation =>
            mutation is not null &&
            IsCanonicalToken(mutation.MutationId) &&
            ids.Add(mutation.MutationId) &&
            inputPaths.Contains(mutation.InputRelativePath) &&
            !string.IsNullOrWhiteSpace(mutation.TargetMemberSignature) &&
            mutation.TargetMemberSignature.Length <= 4_096 &&
            mutation.ExpectedMatchCount > 0 &&
            mutation.ObservedMatchCount == mutation.ExpectedMatchCount &&
            mutation.Replacements is not null &&
            mutation.Replacements.Count == mutation.ExpectedMatchCount &&
            mutation.Replacements.All(static replacement =>
                !string.IsNullOrWhiteSpace(replacement) && replacement.Length <= 4_096) &&
            mutation.PostconditionPassed))
        {
            return false;
        }

        return mutations.Select(static mutation => mutation.InputRelativePath)
            .ToHashSet(StringComparer.Ordinal)
            .SetEquals(inputPaths);
    }

    internal static bool AreValidOutputs(
        ImmutableArray<AppliedRewriteOutputIdentity> outputs,
        ImmutableArray<AppliedRewriteInputIdentity> inputs)
    {
        if (!AreValidInputs(inputs) ||
            outputs.IsDefaultOrEmpty ||
            outputs.Length > GameHostAppliedWorkspaceContract.MaximumOverlayFiles)
        {
            return false;
        }

        var inputByPath = inputs.ToDictionary(static input => input.RelativePath, StringComparer.Ordinal);
        var inputPaths = new HashSet<string>(StringComparer.Ordinal);
        var overlayPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var output in outputs)
        {
            if (output is null ||
                !inputByPath.TryGetValue(output.InputRelativePath, out var input) ||
                !inputPaths.Add(output.InputRelativePath) ||
                !IsValidOverlayPath(output.OverlayRelativePath) ||
                !overlayPaths.Add(output.OverlayRelativePath) ||
                !output.AssemblyIdentity.Equals(input.AssemblyIdentity, StringComparison.Ordinal) ||
                output.Size <= 0 ||
                !IsCanonicalSha256(output.Sha256))
            {
                return false;
            }
        }

        return inputPaths.SetEquals(inputByPath.Keys);
    }

    internal static bool IsValidOriginalPayloadPath(string kind, string path)
    {
        if (!IsSafeRelativePath(path))
        {
            return false;
        }

        var segments = path.Split('/', StringSplitOptions.None);
        if (string.Equals(kind, "content", StringComparison.Ordinal))
        {
            return segments.Length >= 2 && segments[0].Equals("Content", StringComparison.Ordinal);
        }

        return string.Equals(kind, "assembly", StringComparison.Ordinal) &&
            segments.Length == 2 &&
            segments[0].Equals("assemblies", StringComparison.Ordinal) &&
            segments[1].EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsValidManagedInputPath(string path) =>
        IsValidOriginalPayloadPath("assembly", path);

    internal static bool IsValidOverlayPath(string path)
    {
        if (!IsSafeRelativePath(path))
        {
            return false;
        }

        var segments = path.Split('/', StringSplitOptions.None);
        return segments.Length == 3 &&
            segments[0].Equals("overlay", StringComparison.Ordinal) &&
            segments[1].Equals("assemblies", StringComparison.Ordinal) &&
            segments[2].EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsSafeRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) ||
            path.Length > 1_024 ||
            path.StartsWith("/", StringComparison.Ordinal) ||
            path.IndexOfAny(['\\', '\0', '<', '>', ':', '"', '|', '?', '*']) >= 0 ||
            path.Any(char.IsControl) ||
            !path.Equals(path.Normalize(NormalizationForm.FormC), StringComparison.Ordinal))
        {
            return false;
        }

        var segments = path.Split('/', StringSplitOptions.None);
        return segments.All(static segment =>
            segment.Length is > 0 and <= 255 &&
            segment is not "." and not ".." &&
            !segment.EndsWith(' ') &&
            !segment.EndsWith('.'));
    }

    internal static bool IsCanonicalToken(string value) =>
        value is not null &&
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 128 &&
        value.All(static character =>
            character is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_' or '-');

    internal static bool IsCanonicalSha256(string? value) =>
        value is { Length: 64 } &&
        value.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}

public sealed record GameHostAppliedWorkspaceRecoveryPlan(
    bool IsValid,
    string? ActiveKey,
    string? PreviousKey,
    ImmutableArray<string> OwnedStagingDirectoriesToDelete,
    ImmutableArray<string> OrphanedCommittedKeysToQuarantine,
    ImmutableArray<string> ErrorCodes);

/// <summary>
/// Pure crash-recovery planning. It never deletes, moves, activates, or guesses an unreferenced workspace.
/// </summary>
public static class GameHostAppliedWorkspaceRecoveryPlanner
{
    public static GameHostAppliedWorkspaceRecoveryPlan Create(
        GameHostAppliedWorkspaceState? state,
        IEnumerable<string> committedWorkspaceKeys,
        IEnumerable<string> stagingDirectoryNames)
    {
        ArgumentNullException.ThrowIfNull(committedWorkspaceKeys);
        ArgumentNullException.ThrowIfNull(stagingDirectoryNames);
        var committedSupplied = committedWorkspaceKeys.ToArray();
        var stagingSupplied = stagingDirectoryNames.ToArray();
        var committed = committedSupplied.ToHashSet(StringComparer.Ordinal);
        var errors = new HashSet<string>(StringComparer.Ordinal);

        if (committed.Count != committedSupplied.Length ||
            committed.Any(static key => !GameHostAppliedWorkspaceValidator.IsCanonicalSha256(key)))
        {
            errors.Add(GameHostAppliedWorkspaceErrorCodes.RecoveryStateInvalid);
        }

        string? active = null;
        string? previous = null;
        if (state is not null)
        {
            if (!string.Equals(state.Format, GameHostAppliedWorkspaceContract.StateFormat, StringComparison.Ordinal) ||
                !string.Equals(state.Schema, GameHostAppliedWorkspaceContract.StateSchema, StringComparison.Ordinal) ||
                (state.ActiveKey is not null && !GameHostAppliedWorkspaceValidator.IsCanonicalSha256(state.ActiveKey)) ||
                (state.PreviousKey is not null && !GameHostAppliedWorkspaceValidator.IsCanonicalSha256(state.PreviousKey)) ||
                (state.ActiveKey is null && state.PreviousKey is not null) ||
                (state.ActiveKey is not null && state.ActiveKey.Equals(state.PreviousKey, StringComparison.Ordinal)))
            {
                errors.Add(GameHostAppliedWorkspaceErrorCodes.RecoveryStateInvalid);
            }
            else
            {
                active = state.ActiveKey;
                previous = state.PreviousKey;
            }
        }

        if ((active is not null && !committed.Contains(active)) ||
            (previous is not null && !committed.Contains(previous)))
        {
            errors.Add(GameHostAppliedWorkspaceErrorCodes.RecoveryStateInvalid);
        }

        if (errors.Count != 0)
        {
            return new GameHostAppliedWorkspaceRecoveryPlan(
                false,
                null,
                null,
                [],
                [],
                errors.Order(StringComparer.Ordinal).ToImmutableArray());
        }

        var ownedStaging = stagingSupplied
            .Where(IsOwnedPendingDirectoryName)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToImmutableArray();
        var retained = new HashSet<string>(StringComparer.Ordinal);
        if (active is not null)
        {
            retained.Add(active);
        }

        if (previous is not null)
        {
            retained.Add(previous);
        }

        var orphans = committed
            .Where(key => !retained.Contains(key))
            .Order(StringComparer.Ordinal)
            .ToImmutableArray();
        return new GameHostAppliedWorkspaceRecoveryPlan(
            true,
            active,
            previous,
            ownedStaging,
            orphans,
            []);
    }

    public static bool IsOwnedPendingDirectoryName(string name) =>
        name is { Length: 40 } &&
        name.StartsWith("pending-", StringComparison.Ordinal) &&
        name.AsSpan(8).ToString().All(static character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');
}

internal sealed class AppliedCanonicalHashBuilder
{
    private readonly IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

    public void Add(string name, string value)
    {
        var text = EncodeFields([(name, value)]);
        hash.AppendData(Encoding.UTF8.GetBytes(text));
    }

    public void AddArray(string name, IEnumerable<string> values)
    {
        var materialized = values.ToImmutableArray();
        Add($"{name}.count", materialized.Length.ToString(CultureInfo.InvariantCulture));
        for (var index = 0; index < materialized.Length; index++)
        {
            Add($"{name}[{index.ToString(CultureInfo.InvariantCulture)}]", materialized[index]);
        }
    }

    public string GetHash() => Convert.ToHexStringLower(hash.GetHashAndReset());

    public static string EncodeFields(IEnumerable<(string Name, string Value)> fields)
    {
        var result = new StringBuilder();
        foreach (var (name, value) in fields)
        {
            result.Append(Encoding.UTF8.GetByteCount(name).ToString(CultureInfo.InvariantCulture))
                .Append(':').Append(name).Append('=')
                .Append(Encoding.UTF8.GetByteCount(value).ToString(CultureInfo.InvariantCulture))
                .Append(':').Append(value).Append('\n');
        }

        return result.ToString();
    }
}
