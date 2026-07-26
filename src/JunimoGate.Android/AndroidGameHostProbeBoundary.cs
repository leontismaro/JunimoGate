using System.Collections.ObjectModel;
using System.Security.Cryptography;
using Android.Content;
using JunimoGate.Core;
using JunimoGate.Extraction;
using JunimoGate.Rewriter;

namespace JunimoGate.Android;

public enum AndroidGameHostProbeStatus
{
    Succeeded,
    Failed,
    Cancelled,
}

/// <summary>Path-redacted Gate 0 diagnostic safe for an app-private report.</summary>
public sealed record AndroidGameHostProbeDiagnostic(
    DateTimeOffset TimestampUtc,
    string Code,
    string Severity,
    string Message);

public sealed record AndroidGameHostFieldUseEvidence(
    string AssemblyIdentity,
    string ContainingMethodSignature,
    int InstructionOrdinal,
    string OpCode,
    string Operation,
    string FieldSignature);

public sealed record AndroidGameHostCallSiteEvidence(
    string AssemblyIdentity,
    string ContainingMethodSignature,
    int InstructionOrdinal,
    string OpCode,
    string CalledMethodSignature,
    bool TargetsMainActivity);

public sealed record AndroidGameHostPInvokeEvidence(
    string AssemblyIdentity,
    string MethodSignature,
    string ModuleName,
    string EntryPoint,
    string CallingConvention,
    string CharacterSet,
    string Attributes);

public sealed record AndroidGameHostInteropAttributeEvidence(
    string AssemblyIdentity,
    string OwnerSignature,
    string AttributeType,
    string ConstructorSignature,
    IReadOnlyList<string> ArgumentFingerprints);

public sealed record AndroidGameHostFieldUseCounts(
    int Read,
    int Write,
    int Address,
    int Other,
    int Total);

/// <summary>Managed metadata evidence mapped to Android-owned, report-safe primitives.</summary>
public sealed record AndroidGameHostManagedEvidence(
    string SchemaVersion,
    string TargetAssemblyIdentity,
    string TargetModuleVersionId,
    string? TargetFramework,
    IReadOnlyList<string> AssemblyReferences,
    string MainActivityBaseType,
    string MainActivityInstanceFieldSignature,
    IReadOnlyList<string> MainActivityMethodSignatures,
    IReadOnlyList<string> LifecycleMethodSignatures,
    IReadOnlyList<string> BootstrapMethodSignatures,
    IReadOnlyList<AndroidGameHostFieldUseEvidence> FieldUses,
    AndroidGameHostFieldUseCounts FieldUseCounts,
    IReadOnlyList<AndroidGameHostCallSiteEvidence> CallSites,
    int CallSiteCount,
    IReadOnlyList<AndroidGameHostPInvokeEvidence> PInvokes,
    IReadOnlyList<AndroidGameHostInteropAttributeEvidence> InteropAttributes);

public sealed record AndroidGameHostNativeElfEvidence(
    int ElfClass,
    int DataEncoding,
    int IdentVersion,
    int OsAbi,
    int AbiVersion,
    int ObjectType,
    int Machine,
    uint Flags);

/// <summary>Selected-ABI native identity without APK paths or native bytes.</summary>
public sealed record AndroidGameHostNativeEntryEvidence(
    string SourceLabel,
    string EntryPath,
    long Size,
    long CompressedSize,
    string Sha256,
    AndroidGameHostNativeElfEvidence Elf);

public sealed record AndroidManagedPublicApiSurfaceEvidence(
    string SchemaVersion,
    string SurfaceKey,
    int TypeCount,
    int MemberCount);

public sealed record AndroidActivityBridgeAssemblyEvidence(
    string Identity,
    string ModuleVersionId,
    string? TargetFramework,
    AndroidManagedPublicApiSurfaceEvidence PublicApiSurface);

public sealed record AndroidManagedApiRequirementEvidence(
    string SchemaVersion,
    string TargetAssemblyName,
    string RequirementsKey,
    int ConsumerAssemblyCount,
    IReadOnlyList<string> TypeRequirementHashes,
    IReadOnlyList<string> MemberRequirementHashes);

public sealed record AndroidActivityBridgeTypeEvidence(
    string Signature,
    string BaseType,
    bool IsAbstract,
    bool IsSealed,
    IReadOnlyList<string> ConstructorSignatures,
    IReadOnlyList<string> LifecycleMethodSignatures,
    IReadOnlyList<AndroidGameHostInteropAttributeEvidence> InteropAttributes);

public sealed record AndroidMonoGameBridgeEvidence(
    AndroidActivityBridgeTypeEvidence AndroidGameActivity,
    AndroidActivityBridgeTypeEvidence Game,
    IReadOnlyList<string> GameRunMethodSignatures,
    IReadOnlyList<string> GameExitMethodSignatures,
    IReadOnlyList<string> GameServicesPropertySignatures,
    AndroidActivityBridgeTypeEvidence GameServiceContainer,
    IReadOnlyList<string> GetServiceMethodSignatures);

public sealed record AndroidGameRunnerBridgeEvidence(
    AndroidActivityBridgeTypeEvidence Type,
    IReadOnlyList<string> StaticInstanceFieldSignatures,
    IReadOnlyList<string> RunMethodSignatures);

public sealed record AndroidActivityBridgeCallEvidence(
    int InstructionOrdinal,
    string OpCode,
    string CalledMethodSignature);

public sealed record AndroidActivityBridgeFieldEvidence(
    int InstructionOrdinal,
    string OpCode,
    string FieldSignature);

public sealed record AndroidActivityBridgeLifecycleBodyEvidence(
    string MethodSignature,
    int InstructionCount,
    IReadOnlyList<AndroidActivityBridgeCallEvidence> Calls,
    IReadOnlyList<AndroidActivityBridgeFieldEvidence> Fields);

public sealed record AndroidMainActivityBridgeEvidence(
    AndroidActivityBridgeTypeEvidence Type,
    IReadOnlyList<AndroidActivityBridgeLifecycleBodyEvidence> LifecycleBodies);

/// <summary>Report-safe metadata describing the exact Activity/GameRunner bridge shape.</summary>
public sealed record AndroidActivityBridgeCompatibilityEvidence(
    string SchemaVersion,
    string ParentSupportKey,
    AndroidActivityBridgeAssemblyEvidence MonoGameAssembly,
    AndroidActivityBridgeAssemblyEvidence GameAssembly,
    AndroidManagedApiRequirementEvidence MonoGameRequirements,
    AndroidMonoGameBridgeEvidence MonoGame,
    AndroidGameRunnerBridgeEvidence GameRunner,
    AndroidMainActivityBridgeEvidence MainActivity);

/// <summary>Metadata-only Gate 0 evidence. No commercial payload bytes or source paths are retained.</summary>
public sealed class AndroidGameHostProbeResult
{
    internal AndroidGameHostProbeResult(
        AndroidGameHostProbeStatus status,
        string packageName,
        string? workspaceKey,
        string? managedEvidenceKey,
        string? supportKey,
        string? activityBridgeEvidenceKey,
        AndroidGameHostManagedEvidence? managedEvidence,
        AndroidActivityBridgeCompatibilityEvidence? activityBridgeEvidence,
        IEnumerable<AndroidGameHostNativeEntryEvidence> nativeEntries,
        IEnumerable<AndroidGameHostProbeDiagnostic> diagnostics)
    {
        Status = status;
        PackageName = packageName;
        WorkspaceKey = workspaceKey;
        ManagedEvidenceKey = managedEvidenceKey;
        SupportKey = supportKey;
        ActivityBridgeEvidenceKey = activityBridgeEvidenceKey;
        ManagedEvidence = managedEvidence;
        ActivityBridgeEvidence = activityBridgeEvidence;
        NativeEntries = Array.AsReadOnly(nativeEntries.ToArray());
        Diagnostics = Array.AsReadOnly(diagnostics.ToArray());
    }

    public AndroidGameHostProbeStatus Status { get; }
    public string PackageName { get; }
    public string? WorkspaceKey { get; }
    public string? ManagedEvidenceKey { get; }
    public string? SupportKey { get; }
    public string? ActivityBridgeEvidenceKey { get; }
    public AndroidGameHostManagedEvidence? ManagedEvidence { get; }
    public AndroidActivityBridgeCompatibilityEvidence? ActivityBridgeEvidence { get; }
    public ReadOnlyCollection<AndroidGameHostNativeEntryEvidence> NativeEntries { get; }
    public ReadOnlyCollection<AndroidGameHostProbeDiagnostic> Diagnostics { get; }
}

/// <summary>
/// Runs the M5 Gate 0 compatibility probe after two complete execution-trust validations.
/// This boundary never rewrites, loads, or executes managed/native game code.
/// </summary>
public static class AndroidGameHostProbeBoundary
{
    public static async ValueTask<AndroidGameHostProbeResult> ProbeAsync(
        Context context,
        GameInstallationCandidate candidate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(candidate);
        var packageName = candidate.Installation.PackageName;
        if (!IsSupportedPackage(packageName))
        {
            throw new ArgumentException("The candidate package is not supported by the Android boundary.", nameof(candidate));
        }

        var safeContext = context.ApplicationContext ?? context;
        await AndroidPrivateStorage.EnsureMigratedAsync(safeContext, cancellationToken).ConfigureAwait(false);
        var runtimeRoot = AndroidPrivateStorage.GetRuntimeRoot(safeContext);

        var diagnostics = new List<AndroidGameHostProbeDiagnostic>();
        var revalidator = new AndroidPackageWorkspaceCandidateRevalidator(safeContext, packageName);
        var trustValidator = new WorkspaceExecutionValidator(revalidator);

        try
        {
            var before = await ValidateTrustAsync(
                trustValidator,
                candidate,
                runtimeRoot,
                cancellationToken).ConfigureAwait(false);
            AddDiagnostics(diagnostics, before.Diagnostics);
            if (before.Status == WorkspaceExecutionValidationStatus.Cancelled)
            {
                return Cancelled(packageName, diagnostics);
            }

            if (before.Status != WorkspaceExecutionValidationStatus.Validated || before.Plan is null)
            {
                return Failed(packageName, null, diagnostics);
            }

            var plan = before.Plan;
            string[] paths;
            string assemblyRoot;
            string target;
            string monoGame;
            GameHostCompatibilityProbeResult managed;
            try
            {
                paths = ResolveAssemblyPaths(plan);
                assemblyRoot = Path.Combine(plan.WorkspacePath, "assemblies");
                target = paths.Single(path =>
                    Path.GetFileName(path).Equals("StardewValley.dll", StringComparison.OrdinalIgnoreCase));
                monoGame = paths.Single(path =>
                    Path.GetFileName(path).Equals("MonoGame.Framework.dll", StringComparison.OrdinalIgnoreCase));
                managed = new GameHostCompatibilityProbe().Probe(
                    new GameHostCompatibilityProbeOptions(
                        assemblyRoot,
                        target,
                        paths),
                    cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return Cancelled(packageName, diagnostics);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or InvalidOperationException)
            {
                diagnostics.Add(Diagnostic(
                    "gamehost_probe_managed_inputs_invalid",
                    "Error",
                    "The validated managed payload set cannot be safely inspected."));
                return Failed(packageName, plan.WorkspaceKey, diagnostics);
            }

            AddDiagnostics(diagnostics, managed.Diagnostics);
            if (managed.Status == GameHostProbeStatus.Cancelled)
            {
                return Cancelled(packageName, diagnostics);
            }

            if (!managed.IsSuccess || managed.Evidence is null)
            {
                return Failed(packageName, plan.WorkspaceKey, diagnostics);
            }

            var native = await new NativeEntryInventoryProbe()
                .ProbeAsync(plan, candidate, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            AddDiagnostics(diagnostics, native.Diagnostics);
            if (native.Status == NativeEntryInventoryStatus.Cancelled)
            {
                return Cancelled(packageName, diagnostics);
            }

            if (!native.IsSuccess)
            {
                return Failed(packageName, plan.WorkspaceKey, diagnostics);
            }

            var supportKey = GameHostSupportKey.Create(
                managed.Evidence,
                native.SelectedAbi,
                native.Entries.Select(static entry => new GameHostNativeEvidence(
                    entry.SourceLabel,
                    entry.EntryPath,
                    entry.Size,
                    entry.Sha256,
                    entry.Elf.ElfClass,
                    entry.Elf.DataEncoding,
                    entry.Elf.IdentVersion,
                    entry.Elf.OsAbi,
                    entry.Elf.AbiVersion,
                    entry.Elf.ObjectType,
                    entry.Elf.Machine,
                    entry.Elf.Flags)));

            var bridge = new ActivityBridgeCompatibilityProbe().Probe(
                new ActivityBridgeCompatibilityProbeOptions(
                    assemblyRoot,
                    monoGame,
                    target,
                    paths.Where(path => !path.Equals(monoGame, StringComparison.Ordinal)).ToArray(),
                    supportKey),
                cancellationToken);
            AddDiagnostics(diagnostics, bridge.Diagnostics);
            if (bridge.Status == ActivityBridgeProbeStatus.Cancelled)
            {
                return Cancelled(packageName, diagnostics);
            }

            if (!bridge.IsSuccess || bridge.Evidence is null || bridge.EvidenceKey is null)
            {
                return Failed(packageName, plan.WorkspaceKey, diagnostics);
            }

            // Rebuild the complete trust chain after every metadata probe to close package/state/payload races.
            var after = await ValidateTrustAsync(
                trustValidator,
                candidate,
                runtimeRoot,
                cancellationToken).ConfigureAwait(false);
            AddDiagnostics(diagnostics, after.Diagnostics);
            if (after.Status == WorkspaceExecutionValidationStatus.Cancelled)
            {
                return Cancelled(packageName, diagnostics);
            }

            if (after.Status != WorkspaceExecutionValidationStatus.Validated || after.Plan is null ||
                !after.Plan.WorkspaceKey.Equals(plan.WorkspaceKey, StringComparison.Ordinal) ||
                !after.Plan.IdentityDigest.Equals(plan.IdentityDigest, StringComparison.Ordinal))
            {
                diagnostics.Add(Diagnostic(
                    "gamehost_probe_trust_changed",
                    "Error",
                    "The active execution identity changed during compatibility inspection."));
                return Failed(packageName, plan.WorkspaceKey, diagnostics);
            }

            diagnostics.Add(Diagnostic(
                "gamehost_probe_gate0_succeeded",
                "Information",
                "Managed, native, and Activity bridge metadata passed compatibility inspection."));
            return new AndroidGameHostProbeResult(
                AndroidGameHostProbeStatus.Succeeded,
                packageName,
                plan.WorkspaceKey,
                managed.ManagedEvidenceKey,
                supportKey,
                bridge.EvidenceKey,
                MapManagedEvidence(managed.Evidence),
                MapActivityBridgeEvidence(bridge.Evidence),
                native.Entries.Select(MapNativeEvidence),
                diagnostics);
        }
        catch (OperationCanceledException)
        {
            return Cancelled(packageName, diagnostics);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or CryptographicException)
        {
            diagnostics.Add(Diagnostic(
                "gamehost_probe_failed_safely",
                "Error",
                "Gate 0 compatibility inspection failed without loading game code."));
            return Failed(packageName, null, diagnostics);
        }
    }

    private static ValueTask<WorkspaceExecutionValidationResult> ValidateTrustAsync(
        WorkspaceExecutionValidator validator,
        GameInstallationCandidate candidate,
        string runtimeRoot,
        CancellationToken cancellationToken) =>
        validator.ValidateAsync(
            candidate,
            runtimeRoot,
            WorkspacePreparationOptions.DefaultExtractorSchema,
            WorkspacePreparationOptions.DefaultManifestSchema,
            WorkspaceExecutionTrustDefaults.Gate0RewriteRecipe,
            WorkspaceExecutionTrustDefaults.Gate0RewriteStatus,
            cancellationToken);

    private static string[] ResolveAssemblyPaths(ValidatedExecutionPlan plan)
    {
        var assemblyRoot = Path.GetFullPath(Path.Combine(plan.WorkspacePath, "assemblies"));
        var prefix = assemblyRoot.EndsWith(Path.DirectorySeparatorChar)
            ? assemblyRoot
            : assemblyRoot + Path.DirectorySeparatorChar;
        var paths = new List<string>();
        foreach (var payload in plan.Payloads.Where(static payload => payload.Kind == "assembly"))
        {
            var path = Path.GetFullPath(Path.Combine(
                plan.WorkspacePath,
                payload.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
            if (!path.StartsWith(prefix, StringComparison.Ordinal))
            {
                throw new InvalidDataException("A validated assembly path escapes its assembly root.");
            }

            paths.Add(path);
        }

        if (paths.Count == 0 ||
            paths.Count(path => Path.GetFileName(path).Equals("StardewValley.dll", StringComparison.OrdinalIgnoreCase)) != 1)
        {
            throw new InvalidDataException("The validated assembly set does not contain exactly one game target assembly.");
        }

        return paths.Order(StringComparer.Ordinal).ToArray();
    }

    private static void AddDiagnostics(
        ICollection<AndroidGameHostProbeDiagnostic> target,
        IEnumerable<DiagnosticRecord> source)
    {
        foreach (var diagnostic in source)
        {
            target.Add(new AndroidGameHostProbeDiagnostic(
                diagnostic.Timestamp,
                diagnostic.Code,
                diagnostic.Severity.ToString(),
                diagnostic.Message));
        }
    }

    private static void AddDiagnostics(
        ICollection<AndroidGameHostProbeDiagnostic> target,
        IEnumerable<GameHostProbeDiagnostic> source)
    {
        foreach (var diagnostic in source)
        {
            target.Add(Diagnostic(diagnostic.Code, diagnostic.Severity.ToString(), diagnostic.Message));
        }
    }

    private static void AddDiagnostics(
        ICollection<AndroidGameHostProbeDiagnostic> target,
        IEnumerable<ActivityBridgeProbeDiagnostic> source)
    {
        foreach (var diagnostic in source)
        {
            target.Add(Diagnostic(diagnostic.Code, diagnostic.Severity.ToString(), diagnostic.Message));
        }
    }

    private static AndroidGameHostManagedEvidence MapManagedEvidence(GameHostCompatibilityEvidence evidence) =>
        new(
            evidence.SchemaVersion,
            evidence.TargetAssembly.Identity,
            evidence.TargetAssembly.ModuleVersionId,
            evidence.TargetAssembly.TargetFramework,
            evidence.TargetAssembly.References.Select(static reference => reference.Identity).ToArray(),
            evidence.MainActivity.BaseType,
            evidence.MainActivity.InstanceFieldSignature,
            evidence.MainActivity.MethodSignatures.ToArray(),
            evidence.MainActivity.LifecycleMethodSignatures.ToArray(),
            evidence.MainActivity.BootstrapMethodSignatures.ToArray(),
            evidence.FieldUses.Select(static item => new AndroidGameHostFieldUseEvidence(
                item.AssemblyIdentity,
                item.ContainingMethodSignature,
                item.InstructionOrdinal,
                item.OpCode,
                item.Operation.ToString(),
                item.FieldSignature)).ToArray(),
            new AndroidGameHostFieldUseCounts(
                evidence.FieldUseCounts.Read,
                evidence.FieldUseCounts.Write,
                evidence.FieldUseCounts.Address,
                evidence.FieldUseCounts.Other,
                evidence.FieldUseCounts.Total),
            evidence.CallSites.Select(static item => new AndroidGameHostCallSiteEvidence(
                item.AssemblyIdentity,
                item.ContainingMethodSignature,
                item.InstructionOrdinal,
                item.OpCode,
                item.CalledMethodSignature,
                item.TargetsMainActivity)).ToArray(),
            evidence.CallSiteCount,
            evidence.PInvokes.Select(static item => new AndroidGameHostPInvokeEvidence(
                item.AssemblyIdentity,
                item.MethodSignature,
                item.ModuleName,
                item.EntryPoint,
                item.CallingConvention,
                item.CharacterSet,
                item.Attributes)).ToArray(),
            evidence.InteropAttributes.Select(static item => new AndroidGameHostInteropAttributeEvidence(
                item.AssemblyIdentity,
                item.OwnerSignature,
                item.AttributeType,
                item.ConstructorSignature,
                item.ArgumentFingerprints.ToArray())).ToArray());

    private static AndroidActivityBridgeCompatibilityEvidence MapActivityBridgeEvidence(
        ActivityBridgeCompatibilityEvidence evidence) =>
        new(
            evidence.SchemaVersion,
            evidence.ParentSupportKey,
            MapActivityBridgeAssembly(evidence.MonoGameAssembly),
            MapActivityBridgeAssembly(evidence.GameAssembly),
            new AndroidManagedApiRequirementEvidence(
                evidence.MonoGameRequirements.SchemaVersion,
                evidence.MonoGameRequirements.TargetAssemblyName,
                evidence.MonoGameRequirements.RequirementsKey,
                evidence.MonoGameRequirements.ConsumerAssemblyCount,
                evidence.MonoGameRequirements.TypeRequirementHashes.ToArray(),
                evidence.MonoGameRequirements.MemberRequirementHashes.ToArray()),
            new AndroidMonoGameBridgeEvidence(
                MapActivityBridgeType(evidence.MonoGame.AndroidGameActivity),
                MapActivityBridgeType(evidence.MonoGame.Game),
                evidence.MonoGame.GameRunMethodSignatures.ToArray(),
                evidence.MonoGame.GameExitMethodSignatures.ToArray(),
                evidence.MonoGame.GameServicesPropertySignatures.ToArray(),
                MapActivityBridgeType(evidence.MonoGame.GameServiceContainer),
                evidence.MonoGame.GetServiceMethodSignatures.ToArray()),
            new AndroidGameRunnerBridgeEvidence(
                MapActivityBridgeType(evidence.GameRunner.Type),
                evidence.GameRunner.StaticInstanceFieldSignatures.ToArray(),
                evidence.GameRunner.RunMethodSignatures.ToArray()),
            new AndroidMainActivityBridgeEvidence(
                MapActivityBridgeType(evidence.MainActivity.Type),
                evidence.MainActivity.LifecycleBodies.Select(static body =>
                    new AndroidActivityBridgeLifecycleBodyEvidence(
                        body.MethodSignature,
                        body.InstructionCount,
                        body.Calls.Select(static call => new AndroidActivityBridgeCallEvidence(
                            call.InstructionOrdinal,
                            call.OpCode,
                            call.CalledMethodSignature)).ToArray(),
                        body.Fields.Select(static field => new AndroidActivityBridgeFieldEvidence(
                            field.InstructionOrdinal,
                            field.OpCode,
                            field.FieldSignature)).ToArray())).ToArray()));

    private static AndroidActivityBridgeAssemblyEvidence MapActivityBridgeAssembly(
        ActivityBridgeAssemblyEvidence evidence) =>
        new(
            evidence.Identity,
            evidence.ModuleVersionId,
            evidence.TargetFramework,
            new AndroidManagedPublicApiSurfaceEvidence(
                evidence.PublicApiSurface.SchemaVersion,
                evidence.PublicApiSurface.SurfaceKey,
                evidence.PublicApiSurface.TypeCount,
                evidence.PublicApiSurface.MemberCount));

    private static AndroidActivityBridgeTypeEvidence MapActivityBridgeType(
        ActivityBridgeTypeEvidence evidence) =>
        new(
            evidence.Signature,
            evidence.BaseType,
            evidence.IsAbstract,
            evidence.IsSealed,
            evidence.ConstructorSignatures.ToArray(),
            evidence.LifecycleMethodSignatures.ToArray(),
            evidence.InteropAttributes.Select(static attribute => new AndroidGameHostInteropAttributeEvidence(
                attribute.AssemblyIdentity,
                attribute.OwnerSignature,
                attribute.AttributeType,
                attribute.ConstructorSignature,
                attribute.ArgumentFingerprints.ToArray())).ToArray());

    private static AndroidGameHostNativeEntryEvidence MapNativeEvidence(NativeEntryEvidence evidence) =>
        new(
            evidence.SourceLabel,
            evidence.EntryPath,
            evidence.Size,
            evidence.CompressedSize,
            evidence.Sha256,
            new AndroidGameHostNativeElfEvidence(
                evidence.Elf.ElfClass,
                evidence.Elf.DataEncoding,
                evidence.Elf.IdentVersion,
                evidence.Elf.OsAbi,
                evidence.Elf.AbiVersion,
                evidence.Elf.ObjectType,
                evidence.Elf.Machine,
                evidence.Elf.Flags));

    private static AndroidGameHostProbeResult Failed(
        string packageName,
        string? workspaceKey,
        IEnumerable<AndroidGameHostProbeDiagnostic> diagnostics) =>
        new(
            AndroidGameHostProbeStatus.Failed,
            packageName,
            workspaceKey,
            null,
            null,
            null,
            null,
            null,
            [],
            diagnostics);

    private static AndroidGameHostProbeResult Cancelled(
        string packageName,
        IEnumerable<AndroidGameHostProbeDiagnostic> diagnostics) =>
        new(
            AndroidGameHostProbeStatus.Cancelled,
            packageName,
            null,
            null,
            null,
            null,
            null,
            null,
            [],
            diagnostics);

    private static AndroidGameHostProbeDiagnostic Diagnostic(string code, string severity, string message) =>
        new(DateTimeOffset.UtcNow, code, severity, message);

    private static bool IsSupportedPackage(string packageName) =>
        packageName.Equals(AndroidPlatformBoundary.PlayPackageName, StringComparison.Ordinal) ||
        packageName.Equals(AndroidPlatformBoundary.SamsungPackageName, StringComparison.Ordinal);
}
