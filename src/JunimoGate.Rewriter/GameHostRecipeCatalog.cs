using System.Collections.Immutable;

namespace JunimoGate.Rewriter;

/// <summary>Stable Gate 2 recipe eligibility outcomes. Only Approved may authorize a future writer.</summary>
public enum GameHostRecipeEligibilityStatus
{
    EvidenceMismatch,
    UnsupportedSupportKey,
    BlockedPendingBridgeRecipe,
    Approved,
}

/// <summary>Machine-readable Gate 2 recipe decision codes.</summary>
public static class GameHostRecipeDecisionCodes
{
    public const string EvidenceMismatch = "gamehost_recipe_evidence_mismatch";
    public const string UnsupportedSupportKey = "gamehost_recipe_support_key_unsupported";
    public const string BridgeRecipePending = "gamehost_recipe_bridge_recipe_pending";
    public const string Approved = "gamehost_recipe_approved";
}

/// <summary>Exact managed evidence guards frozen for one composite support identity.</summary>
public sealed record GameHostRecipeEvidenceGuard(
    string TargetAssemblyIdentity,
    string TargetModuleVersionId,
    string SelectedAbi,
    string MainActivityBaseType,
    string MainActivityInstanceFieldSignature,
    FieldUseCounts FieldUseCounts,
    int CallSiteCount,
    int DirectMainActivityCallSiteCount,
    int BootstrapMethodCount,
    ImmutableArray<string> EntitlementProtectedFieldUseMethods);

/// <summary>Exact recipe-owned mutation authorization, including its mechanical before/after guards.</summary>
public sealed record GameHostApprovedMutationContract(
    string MutationId,
    string InputRelativePath,
    string TargetMemberSignature,
    int ExpectedMatchCount,
    string PreconditionSha256,
    string PostconditionSha256,
    AppliedEntitlementBehavior EntitlementBehavior);

public enum GameHostEntitlementPolicy
{
    TrustedInstalledSource,
}

/// <summary>
/// One support-catalog entry. A recognized entry may still intentionally contain no approved recipe.
/// </summary>
public sealed record GameHostRecipeSupportEntry(
    string SupportKey,
    string ManagedEvidenceKey,
    GameHostRecipeEvidenceGuard Guard,
    GameHostEntitlementPolicy EntitlementPolicy,
    RewriteRecipeIdentity? ApprovedRecipe,
    ImmutableArray<GameHostApprovedMutationContract> ApprovedMutations,
    GameHostRecipeEligibilityStatus Status,
    string DecisionCode);

/// <summary>Fail-closed result consumed before any future rewrite transaction may be created.</summary>
public sealed record GameHostRecipeDecision
{
    internal GameHostRecipeDecision(
        GameHostRecipeEligibilityStatus status,
        string decisionCode,
        string supportKey,
        GameHostEntitlementPolicy? entitlementPolicy,
        RewriteRecipeIdentity? recipe,
        ImmutableArray<GameHostApprovedMutationContract> approvedMutations)
    {
        Status = status;
        DecisionCode = decisionCode;
        SupportKey = supportKey;
        EntitlementPolicy = entitlementPolicy;
        Recipe = recipe;
        ApprovedMutations = approvedMutations.IsDefault ? [] : approvedMutations;
    }

    public GameHostRecipeEligibilityStatus Status { get; }
    public string DecisionCode { get; }
    public string SupportKey { get; }
    public GameHostEntitlementPolicy? EntitlementPolicy { get; }
    public RewriteRecipeIdentity? Recipe { get; }
    public ImmutableArray<GameHostApprovedMutationContract> ApprovedMutations { get; }
    public bool CanRewrite =>
        Status == GameHostRecipeEligibilityStatus.Approved &&
        EntitlementPolicy == GameHostEntitlementPolicy.TrustedInstalledSource &&
        Recipe is not null &&
        !ApprovedMutations.IsDefaultOrEmpty;
}

/// <summary>
/// Frozen support catalog for M5. Gate 0 evidence, trusted-installed-source policy and the exact
/// bridge recipe are approved only for the tested support identity.
/// </summary>
public static class GameHostRecipeCatalog
{
    public const string SchemaVersion = "junimogate.gamehost-recipe-catalog/v1";

    public const string TestedPlayPackageName = "com.chucklefish.stardewvalley";
    public const string TestedPlayVersionName = "1.6.15.3";
    public const long TestedPlayLongVersionCode = 245;
    public const string TestedPlayAbi = "arm64-v8a";

    public const string TestedPlaySupportKey =
        "59387f71429416d3aede5e57a9ee289fb14130bf6d5617797e211ebac86e5173";

    public const string TestedPlayManagedEvidenceKey =
        "f1d708e2390c77c150b0bfb498de8210ae90a471cd1b6250cad79689804eb8b0";

    public const string TestedPlayTargetIdentity =
        "StardewValley, Version=1.6.15.3, Culture=neutral, PublicKeyToken=null";

    public const string TestedPlayTargetMvid = "67691627-0fad-41aa-96c6-78b492e108c0";

    public const string TestedMainActivityBaseType = "Microsoft.Xna.Framework.AndroidGameActivity";

    public const string TestedMainActivityInstanceField =
        "StardewValley.MainActivity StardewValley.MainActivity::instance";

    private static readonly ImmutableArray<string> TestedEntitlementProtectedFieldUseMethods =
    [
        "instance;callconv=Default;generic-arity=0;System.Void StardewValley.MainActivity+LicensingChecker::.ctor()",
        "instance;callconv=Default;generic-arity=0;System.Void StardewValley.MainActivity+LicensingChecker::Allow(System.String)",
        "instance;callconv=Default;generic-arity=0;System.Void StardewValley.MainActivity+LicensingChecker::DontAllow(Android.App.PendingIntent)",
    ];

    private static readonly GameHostRecipeSupportEntry TestedPlayEntry = new(
        TestedPlaySupportKey,
        TestedPlayManagedEvidenceKey,
        new GameHostRecipeEvidenceGuard(
            TestedPlayTargetIdentity,
            TestedPlayTargetMvid,
            TestedPlayAbi,
            TestedMainActivityBaseType,
            TestedMainActivityInstanceField,
            new FieldUseCounts(Read: 17, Write: 1, Address: 0, Other: 0, Total: 18),
            CallSiteCount: 536,
            DirectMainActivityCallSiteCount: 48,
            BootstrapMethodCount: 0,
            TestedEntitlementProtectedFieldUseMethods),
        GameHostEntitlementPolicy.TrustedInstalledSource,
        GameHostBridgeRecipe.Identity,
        GameHostBridgeRecipe.ApprovedMutations,
        GameHostRecipeEligibilityStatus.Approved,
        GameHostRecipeDecisionCodes.Approved);

    /// <summary>Returns immutable recognized support identities; recognition is not rewrite approval.</summary>
    public static ImmutableArray<GameHostRecipeSupportEntry> KnownEntries => [TestedPlayEntry];

    /// <summary>
    /// Recomputes the composite support key, then applies every frozen mechanical guard.
    /// Only the exact tested identity receives the catalog-owned bridge recipe. The host path uses
    /// trusted installed-source validation and must not invoke or modify the original LVL callbacks.
    /// </summary>
    public static GameHostRecipeDecision Evaluate(
        string claimedSupportKey,
        string managedEvidenceKey,
        GameHostCompatibilityEvidence managedEvidence,
        string selectedAbi,
        IEnumerable<GameHostNativeEvidence> nativeEntries)
    {
        ArgumentNullException.ThrowIfNull(managedEvidence);
        ArgumentNullException.ThrowIfNull(nativeEntries);

        if (!IsCanonicalSha256(claimedSupportKey) || !IsCanonicalSha256(managedEvidenceKey))
        {
            return Decision(
                GameHostRecipeEligibilityStatus.EvidenceMismatch,
                GameHostRecipeDecisionCodes.EvidenceMismatch,
                claimedSupportKey ?? string.Empty);
        }

        string computedSupportKey;
        try
        {
            computedSupportKey = GameHostSupportKey.Create(managedEvidence, selectedAbi, nativeEntries);
        }
        catch (ArgumentException)
        {
            return Decision(
                GameHostRecipeEligibilityStatus.EvidenceMismatch,
                GameHostRecipeDecisionCodes.EvidenceMismatch,
                claimedSupportKey);
        }

        if (!computedSupportKey.Equals(claimedSupportKey, StringComparison.Ordinal))
        {
            return Decision(
                GameHostRecipeEligibilityStatus.EvidenceMismatch,
                GameHostRecipeDecisionCodes.EvidenceMismatch,
                claimedSupportKey);
        }

        var entry = KnownEntries.FirstOrDefault(candidate =>
            candidate.SupportKey.Equals(computedSupportKey, StringComparison.Ordinal));
        if (entry is null)
        {
            return Decision(
                GameHostRecipeEligibilityStatus.UnsupportedSupportKey,
                GameHostRecipeDecisionCodes.UnsupportedSupportKey,
                computedSupportKey);
        }

        if (!entry.ManagedEvidenceKey.Equals(managedEvidenceKey, StringComparison.Ordinal) ||
            !MatchesGuard(entry.Guard, managedEvidence, selectedAbi))
        {
            return Decision(
                GameHostRecipeEligibilityStatus.EvidenceMismatch,
                GameHostRecipeDecisionCodes.EvidenceMismatch,
                computedSupportKey);
        }

        return new GameHostRecipeDecision(
            entry.Status,
            entry.DecisionCode,
            entry.SupportKey,
            entry.EntitlementPolicy,
            entry.ApprovedRecipe,
            entry.ApprovedMutations);
    }

    private static bool MatchesGuard(
        GameHostRecipeEvidenceGuard guard,
        GameHostCompatibilityEvidence evidence,
        string selectedAbi)
    {
        var entitlementMethods = evidence.FieldUses
            .Where(static use =>
                use.Operation == FieldUseOperation.Read &&
                use.ContainingMethodSignature.Contains(
                    "StardewValley.MainActivity+LicensingChecker::",
                    StringComparison.Ordinal))
            .Select(static use => use.ContainingMethodSignature)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToImmutableArray();

        return evidence.TargetAssembly.Identity.Equals(guard.TargetAssemblyIdentity, StringComparison.Ordinal) &&
            evidence.TargetAssembly.ModuleVersionId.Equals(guard.TargetModuleVersionId, StringComparison.Ordinal) &&
            selectedAbi.Equals(guard.SelectedAbi, StringComparison.Ordinal) &&
            evidence.MainActivity.BaseType.Equals(guard.MainActivityBaseType, StringComparison.Ordinal) &&
            evidence.MainActivity.InstanceFieldSignature.Equals(
                guard.MainActivityInstanceFieldSignature,
                StringComparison.Ordinal) &&
            evidence.FieldUseCounts == guard.FieldUseCounts &&
            evidence.CallSiteCount == guard.CallSiteCount &&
            evidence.CallSites.Count(static call => call.TargetsMainActivity) == guard.DirectMainActivityCallSiteCount &&
            evidence.MainActivity.BootstrapMethodSignatures.Length == guard.BootstrapMethodCount &&
            entitlementMethods.SequenceEqual(
                guard.EntitlementProtectedFieldUseMethods.Order(StringComparer.Ordinal),
                StringComparer.Ordinal);
    }

    private static GameHostRecipeDecision Decision(
        GameHostRecipeEligibilityStatus status,
        string code,
        string supportKey) =>
        new(status, code, supportKey, null, null, []);

    private static bool IsCanonicalSha256(string? value) =>
        value is { Length: 64 } &&
        value.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
