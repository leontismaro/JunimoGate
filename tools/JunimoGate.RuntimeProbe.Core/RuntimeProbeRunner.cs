using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using HarmonyLib;
using JunimoGate.RuntimeProbe.Target;
using MonoMod;
using MonoMod.Utils;

namespace JunimoGate.RuntimeProbe.Core;

public static class RuntimeProbeRunner
{
    private sealed record CaseDefinition(string Id, Func<CaseObservation> Execute);

    private sealed record CaseObservation(string Summary, Dictionary<string, string> Details);

    private static readonly IReadOnlyList<CaseDefinition> CaseDefinitions =
    [
        new(RuntimeProbeCaseIds.DynamicCodeCapability, ProbeDynamicCodeCapability),
        new(RuntimeProbeCaseIds.HarmonyMonoModAndroidSupport, ProbeHarmonyMonoModAndroidSupport),
        new(RuntimeProbeCaseIds.MonoManagedEntryPoint, ProbeMonoManagedEntryPoint),
        new(RuntimeProbeCaseIds.NativeCacheFlush, ProbeNativeCacheFlush),
        new(RuntimeProbeCaseIds.HarmonyPrivateMethod, ProbeHarmonyPrivateMethod),
        new(RuntimeProbeCaseIds.HarmonyFieldInjection, ProbeHarmonyFieldInjection),
        new(RuntimeProbeCaseIds.HarmonyTranspiler, ProbeHarmonyTranspiler),
        new(RuntimeProbeCaseIds.HarmonySmapiPrefix, ProbeHarmonySmapiPrefix),
        new(RuntimeProbeCaseIds.MonoModDynamicMethod, () => ProbeMonoMod("dynamicmethod", requireIgnoresAccessChecks: false)),
        new(RuntimeProbeCaseIds.MonoModCecil, () => ProbeMonoMod("cecil", requireIgnoresAccessChecks: true)),
    ];

    public static async Task<RuntimeProbeReport> RunAsync(
        RuntimeProbeInput? input = null,
        Action<ProbeProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        input ??= new RuntimeProbeInput();
        var startedUtc = DateTimeOffset.UtcNow;
        var runTimer = Stopwatch.StartNew();
        var results = new List<ProbeCaseResult>(CaseDefinitions.Count);

        for (var index = 0; index < CaseDefinitions.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var definition = CaseDefinitions[index];
            progress?.Invoke(new ProbeProgress(definition.Id, index + 1, CaseDefinitions.Count, "started"));
            await Task.Yield();

            var result = ExecuteCase(definition);
            results.Add(result);
            progress?.Invoke(new ProbeProgress(definition.Id, index + 1, CaseDefinitions.Count, "completed", result));
        }

        runTimer.Stop();
        var endedUtc = DateTimeOffset.UtcNow;

        return new RuntimeProbeReport(
            startedUtc,
            endedUtc,
            runTimer.Elapsed.TotalMilliseconds,
            RuntimeProbeConclusions.Evaluate(results),
            CaptureEnvironment(),
            new Dictionary<string, string>(input.PlatformMetadata, StringComparer.Ordinal),
            results);
    }

    private static ProbeCaseResult ExecuteCase(CaseDefinition definition)
    {
        var startedUtc = DateTimeOffset.UtcNow;
        var timer = Stopwatch.StartNew();
        try
        {
            var observation = definition.Execute();
            timer.Stop();
            return new ProbeCaseResult(
                definition.Id,
                true,
                ProbeCaseStatus.Passed,
                startedUtc,
                DateTimeOffset.UtcNow,
                timer.Elapsed.TotalMilliseconds,
                observation.Summary,
                observation.Details,
                null);
        }
        catch (Exception ex)
        {
            timer.Stop();
            return new ProbeCaseResult(
                definition.Id,
                true,
                ProbeCaseStatus.Failed,
                startedUtc,
                DateTimeOffset.UtcNow,
                timer.Elapsed.TotalMilliseconds,
                "Hard runtime probe failed.",
                new Dictionary<string, string>(StringComparer.Ordinal),
                new ProbeExceptionInfo(
                    ex.GetType().FullName ?? ex.GetType().Name,
                    FormatExceptionChain(ex),
                    ex.ToString()));
        }
    }

    private static CaseObservation ProbeDynamicCodeCapability()
    {
        Require(RuntimeFeature.IsDynamicCodeSupported, "RuntimeFeature.IsDynamicCodeSupported is false.");

        var method = new DynamicMethod(
            "JunimoGateDynamicCodeSmokeTest",
            typeof(int),
            Type.EmptyTypes,
            typeof(RuntimeProbeRunner).Module,
            skipVisibility: true);
        var il = method.GetILGenerator();
        il.Emit(OpCodes.Ldc_I4, 73);
        il.Emit(OpCodes.Ret);
        var generated = method.CreateDelegate<Func<int>>();
        var result = generated();
        Require(result == 73, $"DynamicMethod returned {result}; expected 73.");

        return Observation(
            "Runtime generated and executed a DynamicMethod successfully; the runtime compilation flag is recorded separately.",
            ("isDynamicCodeSupported", RuntimeFeature.IsDynamicCodeSupported),
            ("isDynamicCodeCompiled", RuntimeFeature.IsDynamicCodeCompiled),
            ("dynamicMethodResult", result));
    }

    private static CaseObservation ProbeHarmonyMonoModAndroidSupport()
    {
        var result = HarmonyMonoModAndroidSupport.Inspect();
        Require(
            !result.LibraryFixRequired || result.LibraryFixApplied,
            "Android requires the pinned JunimoGate Harmony/MonoMod source patch, but it was not detected.");

        return Observation(
            result.LibraryFixRequired
                ? "The patched Harmony package initialized its Android bionic/Linux detour platform successfully."
                : "The current OS does not require the Android Harmony/MonoMod source patch.",
            ("detectedOperatingSystem", result.DetectedOperatingSystem),
            ("libraryFixRequired", result.LibraryFixRequired),
            ("libraryFixApplied", result.LibraryFixApplied),
            ("harmonyInformationalVersion", result.HarmonyInformationalVersion),
            ("systemType", result.SystemType),
            ("systemTarget", result.SystemTarget),
            ("architectureType", result.ArchitectureType),
            ("architectureTarget", result.ArchitectureTarget),
            ("runtimeType", result.RuntimeType),
            ("runtimeTarget", result.RuntimeTarget),
            ("harmonyAssembly", typeof(Harmony).Assembly.FullName));
    }

    private static CaseObservation ProbeMonoManagedEntryPoint()
    {
        var details = new Dictionary<string, string>(
            MonoManagedEntryPointDiagnostic.Inspect(),
            StringComparer.Ordinal);
        return new CaseObservation(
            details.TryGetValue("diagnosticRequired", out var required)
                && string.Equals(required, "true", StringComparison.OrdinalIgnoreCase)
                ? "Captured MonoMethod, unmanaged-wrapper, and mono_compile_method pointers without modifying runtime memory."
                : "The current runtime does not require the Android Mono entry-point diagnostic.",
            details);
    }

    private static CaseObservation ProbeNativeCacheFlush()
    {
        var details = new Dictionary<string, string>(
            NativeCacheFlushDiagnostic.Inspect(),
            StringComparer.Ordinal);
        return new CaseObservation(
            details.TryGetValue("diagnosticRequired", out var required)
                && string.Equals(required, "true", StringComparison.OrdinalIgnoreCase)
                ? "The native ARM64 cache helper made modified executable code visible to execution."
                : "The current platform does not require the Android ARM64 native cache-helper diagnostic.",
            details);
    }

    private static CaseObservation ProbeHarmonyPrivateMethod()
    {
        RequireHarmonyWritesAllowed();
        HarmonyPatchCallbacks.ResetPrivateMethod();
        var harmony = NewHarmony(RuntimeProbeCaseIds.HarmonyPrivateMethod);
        var original = RequiredPrivateMethod("HarmonyPatched");
        var details = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            var jitBytesBeforePatch = MonoManagedEntryPointDiagnostic.CapturePatchedRuntimeEntryBytes(original);
            harmony.Patch(
                original,
                prefix: new HarmonyMethod(RequiredCallback(nameof(HarmonyPatchCallbacks.PrivateMethodPrefix))),
                postfix: new HarmonyMethod(RequiredCallback(nameof(HarmonyPatchCallbacks.PrivateMethodPostfix))));
            var jitBytesAfterPatch = MonoManagedEntryPointDiagnostic.CapturePatchedRuntimeEntryBytes(original);
            details["jitBytesBeforePatch"] = jitBytesBeforePatch;
            details["jitBytesAfterPatch"] = jitBytesAfterPatch;
            details["jitBytesChanged"] = (!string.Equals(
                jitBytesBeforePatch,
                jitBytesAfterPatch,
                StringComparison.Ordinal)).ToString();

            var target = new RuntimeProbeTarget();
            var patchedCall = (Func<RuntimeProbeTarget, int, int>)original.CreateDelegate(
                typeof(Func<RuntimeProbeTarget, int, int>));
            var result = patchedCall(target, 3);
            Require(
                result == 118,
                $"Patched private method delegate returned {result}; expected 118. " +
                $"JIT bytes changed={details["jitBytesChanged"]}; " +
                $"before={details["jitBytesBeforePatch"]}; after={details["jitBytesAfterPatch"]}.");
            Require(target.PatchedOriginalCalls == 1, "Patched private original did not execute exactly once.");
            Require(HarmonyPatchCallbacks.PrivatePrefixCalls == 1, "Harmony Prefix did not execute exactly once.");
            Require(HarmonyPatchCallbacks.PrivatePostfixCalls == 1, "Harmony Postfix did not execute exactly once.");

            var precompiledWrapperResult = new RuntimeProbeTarget().InvokeHarmonyPatched(3);
            details["delegateResult"] = result.ToString();
            details["precompiledWrapperResult"] = precompiledWrapperResult.ToString();
            details["originalCalls"] = target.PatchedOriginalCalls.ToString();
            details["prefixCalls"] = HarmonyPatchCallbacks.PrivatePrefixCalls.ToString();
            details["postfixCalls"] = HarmonyPatchCallbacks.PrivatePostfixCalls.ToString();
            return new CaseObservation(
                "Harmony patched a private method in the separate Target assembly with Prefix and Postfix.",
                details);
        }
        finally
        {
            details["cleanupStatus"] = TryUnpatchForDiagnostics(harmony, original);
            HarmonyPatchCallbacks.ResetPrivateMethod();
        }
    }

    private static CaseObservation ProbeHarmonyFieldInjection()
    {
        RequireHarmonyWritesAllowed();
        HarmonyPatchCallbacks.ResetFieldInjection();
        var harmony = NewHarmony(RuntimeProbeCaseIds.HarmonyFieldInjection);
        var original = RequiredPrivateMethod("ReadSecret");
        var details = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            harmony.Patch(
                original,
                prefix: new HarmonyMethod(RequiredCallback(nameof(HarmonyPatchCallbacks.FieldInjectionPrefix))));

            var patchedCall = (Func<RuntimeProbeTarget, int>)original.CreateDelegate(
                typeof(Func<RuntimeProbeTarget, int>));
            var result = patchedCall(new RuntimeProbeTarget());
            Require(result == 17, $"Target original delegate read {result}; expected injected private field value 17.");
            Require(HarmonyPatchCallbacks.FieldInjectionCalls == 1, "Field-injection Prefix did not execute exactly once.");
            Require(HarmonyPatchCallbacks.FieldValueRead == 10, "Harmony did not inject the original private field value.");

            var precompiledWrapperResult = new RuntimeProbeTarget().InvokeFieldInjection();
            details["injectedValueRead"] = HarmonyPatchCallbacks.FieldValueRead.ToString();
            details["valueWritten"] = "17";
            details["delegateResult"] = result.ToString();
            details["precompiledWrapperResult"] = precompiledWrapperResult.ToString();
            details["prefixCalls"] = HarmonyPatchCallbacks.FieldInjectionCalls.ToString();
            return new CaseObservation(
                "Harmony injected the Target private field by reference; the Prefix read and wrote it and the original method observed the write.",
                details);
        }
        finally
        {
            details["cleanupStatus"] = TryUnpatchForDiagnostics(harmony, original);
            HarmonyPatchCallbacks.ResetFieldInjection();
        }
    }

    private static CaseObservation ProbeHarmonyTranspiler()
    {
        RequireHarmonyWritesAllowed();
        HarmonyPatchCallbacks.ResetTranspiler();
        var harmony = NewHarmony(RuntimeProbeCaseIds.HarmonyTranspiler);
        var original = RequiredPrivateMethod("PrivateIlBody");
        var details = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            harmony.Patch(
                original,
                transpiler: new HarmonyMethod(RequiredCallback(nameof(HarmonyPatchCallbacks.ReemitTranspiler))));

            var patchedCall = (Func<RuntimeProbeTarget, int, int>)original.CreateDelegate(
                typeof(Func<RuntimeProbeTarget, int, int>));
            var result = patchedCall(new RuntimeProbeTarget(), 3);
            Require(result == 66, $"Transpiled private-access delegate returned {result}; expected 66.");
            Require(HarmonyPatchCallbacks.TranspilerCalls == 1, "Transpiler did not execute exactly once.");
            Require(HarmonyPatchCallbacks.SawPrivateMethodOperand, "Original IL did not expose the expected private method operand.");
            Require(HarmonyPatchCallbacks.SawPrivateFieldOperand, "Original IL did not expose the expected private field operand.");

            var precompiledWrapperResult = new RuntimeProbeTarget().InvokeTranspiled(3);
            details["delegateResult"] = result.ToString();
            details["precompiledWrapperResult"] = precompiledWrapperResult.ToString();
            details["transpilerCalls"] = HarmonyPatchCallbacks.TranspilerCalls.ToString();
            details["sawPrivateMethodOperand"] = HarmonyPatchCallbacks.SawPrivateMethodOperand.ToString();
            details["sawPrivateFieldOperand"] = HarmonyPatchCallbacks.SawPrivateFieldOperand.ToString();
            return new CaseObservation(
                "Harmony re-emitted original IL containing private method and private field operands and the patched method executed successfully.",
                details);
        }
        finally
        {
            details["cleanupStatus"] = TryUnpatchForDiagnostics(harmony, original);
            HarmonyPatchCallbacks.ResetTranspiler();
        }
    }

    private static CaseObservation ProbeHarmonySmapiPrefix()
    {
        RequireHarmonyWritesAllowed();
        HarmonyPatchCallbacks.ResetStorageMigration();
        var harmony = NewHarmony(RuntimeProbeCaseIds.HarmonySmapiPrefix);
        var original = RequiredPrivateMethod("CheckStorageMigration");
        var details = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            harmony.Patch(
                original,
                prefix: new HarmonyMethod(RequiredCallback(nameof(HarmonyPatchCallbacks.CheckStorageMigrationPrefix))));

            var target = new RuntimeProbeTarget();
            var patchedCall = (Func<RuntimeProbeTarget, bool>)original.CreateDelegate(
                typeof(Func<RuntimeProbeTarget, bool>));
            var result = patchedCall(target);
            Require(!result, "CheckStorageMigration delegate was not forced to false.");
            Require(target.MigrationOriginalCalls == 0, "CheckStorageMigration original was not skipped.");
            Require(HarmonyPatchCallbacks.StorageMigrationPrefixCalls == 1, "SMAPI-style Prefix did not execute exactly once.");

            var precompiledWrapperResult = new RuntimeProbeTarget().InvokeCheckStorageMigration();
            details["delegateResult"] = result.ToString();
            details["precompiledWrapperResult"] = precompiledWrapperResult.ToString();
            details["originalCalls"] = target.MigrationOriginalCalls.ToString();
            details["prefixCalls"] = HarmonyPatchCallbacks.StorageMigrationPrefixCalls.ToString();
            return new CaseObservation(
                "Representative SMAPI-style Prefix forced CheckStorageMigration=false and skipped the private original.",
                details);
        }
        finally
        {
            details["cleanupStatus"] = TryUnpatchForDiagnostics(harmony, original);
            HarmonyPatchCallbacks.ResetStorageMigration();
        }
    }

    private static CaseObservation ProbeMonoMod(string backend, bool requireIgnoresAccessChecks)
    {
        Switches.SetSwitchValue(Switches.DMDType, backend);
        try
        {
            var original = RequiredPrivateMethod("CopyPrivateAccess");
            using var definition = new DynamicMethodDefinition(original);
            var generated = definition.Generate();
            var copied = (Func<RuntimeProbeTarget, int, int>)generated.CreateDelegate(
                typeof(Func<RuntimeProbeTarget, int, int>));
            var result = copied(new RuntimeProbeTarget(), 4);
            Require(result == 18, $"MonoMod {backend} copy returned {result}; expected 18.");

            var generatedAssembly = generated.Module.Assembly;
            var targetAssemblyName = typeof(RuntimeProbeTarget).Assembly.GetName().Name
                ?? throw new InvalidOperationException("Target assembly has no simple name.");
            var ignoresAccessChecksTargets = GetIgnoresAccessChecksTargets(generatedAssembly);
            var hasTargetBypass = ignoresAccessChecksTargets.Any(value =>
                string.Equals(value, targetAssemblyName, StringComparison.Ordinal));

            if (requireIgnoresAccessChecks)
            {
                Require(
                    hasTargetBypass,
                    $"Cecil-generated assembly lacks IgnoresAccessChecksToAttribute for {targetAssemblyName}. " +
                    $"Observed targets: {string.Join(", ", ignoresAccessChecksTargets)}");
            }

            return Observation(
                $"MonoMod DMD backend '{backend}' copied and invoked a private instance method whose IL accesses private Target members.",
                ("forcedBackend", backend),
                ("result", result),
                ("generatedMethod", generated.ToString() ?? generated.Name),
                ("generatedAssembly", generatedAssembly.FullName ?? generatedAssembly.GetName().Name ?? "unknown"),
                ("ignoresAccessChecksTargets", string.Join(",", ignoresAccessChecksTargets)),
                ("targetAssemblyBypassPresent", hasTargetBypass));
        }
        finally
        {
            Switches.ClearSwitchValue(Switches.DMDType);
        }
    }

    private static IReadOnlyList<string> GetIgnoresAccessChecksTargets(Assembly assembly) =>
        assembly.CustomAttributes
            .Where(attribute => string.Equals(
                attribute.AttributeType.FullName,
                "System.Runtime.CompilerServices.IgnoresAccessChecksToAttribute",
                StringComparison.Ordinal))
            .Select(attribute => attribute.ConstructorArguments.Count == 1
                ? attribute.ConstructorArguments[0].Value as string
                : null)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Cast<string>()
            .ToArray();

    private static ProbeEnvironmentInfo CaptureEnvironment() => new(
        Environment.Version.ToString(),
        RuntimeInformation.FrameworkDescription,
        RuntimeInformation.RuntimeIdentifier,
        RuntimeInformation.OSDescription,
        RuntimeInformation.ProcessArchitecture.ToString(),
        RuntimeInformation.OSArchitecture.ToString(),
        RuntimeFeature.IsDynamicCodeSupported,
        RuntimeFeature.IsDynamicCodeCompiled,
        GetMonoRuntimeDisplayName(),
        CaptureAssembly(typeof(Harmony).Assembly),
        CaptureAssembly(typeof(DynamicMethodDefinition).Assembly));

    private static ProbeAssemblyInfo CaptureAssembly(Assembly assembly)
    {
        var name = assembly.GetName();
        var informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
        return new ProbeAssemblyInfo(
            name.Name ?? "unknown",
            name.Version?.ToString(),
            informationalVersion,
            assembly.ManifestModule.ModuleVersionId.ToString("D"));
    }

    private static string? GetMonoRuntimeDisplayName()
    {
        var monoRuntime = Type.GetType("Mono.Runtime")
            ?? typeof(object).Assembly.GetType("Mono.Runtime");
        var getDisplayName = monoRuntime?.GetMethod(
            "GetDisplayName",
            BindingFlags.NonPublic | BindingFlags.Static);
        try
        {
            return getDisplayName?.Invoke(null, null) as string;
        }
        catch (Exception ex)
        {
            return $"unavailable: {ex.GetType().Name}: {ex.Message}";
        }
    }

    private static void RequireHarmonyWritesAllowed()
    {
        Require(
            MonoManagedEntryPointDiagnostic.HarmonyWritesAllowed,
            "Harmony write blocked: read-only Android Mono entry-point diagnostic must identify the executable managed method body first.");
        Require(
            NativeCacheFlushDiagnostic.HarmonyWritesAllowed,
            "Harmony write blocked: native ARM64 cache-helper self-test must pass first.");
    }

    private static Harmony NewHarmony(string caseId) =>
        new($"org.junimogate.runtimeprobe.{caseId}.{Guid.NewGuid():N}");

    private static string TryUnpatchForDiagnostics(Harmony harmony, MethodBase original)
    {
        try
        {
            harmony.Unpatch(original, HarmonyPatchType.All, harmony.Id);
            return "completed";
        }
        catch (Exception exception)
        {
            return $"unsupported-or-failed:{exception.GetType().FullName}:{exception.Message}";
        }
    }

    private static MethodInfo RequiredPrivateMethod(string name) =>
        typeof(RuntimeProbeTarget).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(typeof(RuntimeProbeTarget).FullName, name);

    private static MethodInfo RequiredCallback(string name) =>
        typeof(HarmonyPatchCallbacks).GetMethod(name, BindingFlags.Static | BindingFlags.Public)
        ?? throw new MissingMethodException(typeof(HarmonyPatchCallbacks).FullName, name);

    private static CaseObservation Observation(string summary, params (string Key, object? Value)[] details) =>
        new(summary, details.ToDictionary(
            item => item.Key,
            item => item.Value?.ToString() ?? "null",
            StringComparer.Ordinal));

    private static string FormatExceptionChain(Exception exception)
    {
        var chain = new List<string>();
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            chain.Add($"{current.GetType().FullName ?? current.GetType().Name}: {current.Message}");
        }

        return string.Join(" -> ", chain);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}

public static class HarmonyPatchCallbacks
{
    public static int PrivatePrefixCalls { get; private set; }
    public static int PrivatePostfixCalls { get; private set; }
    public static int FieldInjectionCalls { get; private set; }
    public static int FieldValueRead { get; private set; }
    public static int TranspilerCalls { get; private set; }
    public static bool SawPrivateMethodOperand { get; private set; }
    public static bool SawPrivateFieldOperand { get; private set; }
    public static int StorageMigrationPrefixCalls { get; private set; }

    public static void PrivateMethodPrefix(ref int value)
    {
        PrivatePrefixCalls++;
        value++;
    }

    public static void PrivateMethodPostfix(ref int __result)
    {
        PrivatePostfixCalls++;
        __result += 100;
    }

    public static void FieldInjectionPrefix(ref int ____secret)
    {
        FieldInjectionCalls++;
        FieldValueRead = ____secret;
        ____secret += 7;
    }

    public static IEnumerable<CodeInstruction> ReemitTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        TranspilerCalls++;
        var originals = instructions.ToArray();
        SawPrivateMethodOperand = originals.Any(instruction =>
            instruction.operand is MethodInfo method && method.IsPrivate);
        SawPrivateFieldOperand = originals.Any(instruction =>
            instruction.operand is FieldInfo field && field.IsPrivate);
        var rewritten = new List<CodeInstruction>(originals.Length + 2);
        foreach (var instruction in originals)
        {
            if (instruction.opcode == OpCodes.Ret)
            {
                rewritten.Add(new CodeInstruction(OpCodes.Ldc_I4, 50));
                rewritten.Add(new CodeInstruction(OpCodes.Add));
            }

            rewritten.Add(new CodeInstruction(instruction));
        }

        return rewritten;
    }

    public static bool CheckStorageMigrationPrefix(ref bool __result)
    {
        StorageMigrationPrefixCalls++;
        __result = false;
        return false;
    }

    public static void ResetPrivateMethod()
    {
        PrivatePrefixCalls = 0;
        PrivatePostfixCalls = 0;
    }

    public static void ResetFieldInjection()
    {
        FieldInjectionCalls = 0;
        FieldValueRead = 0;
    }

    public static void ResetTranspiler()
    {
        TranspilerCalls = 0;
        SawPrivateMethodOperand = false;
        SawPrivateFieldOperand = false;
    }

    public static void ResetStorageMigration() => StorageMigrationPrefixCalls = 0;
}
