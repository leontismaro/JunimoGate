using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace JunimoGate.Rewriter;

/// <summary>
/// Reads bounded managed metadata with Mono.Cecil. It never loads an assembly into the runtime,
/// resolves an assembly reference, writes an assembly, or creates staging output.
/// </summary>
public sealed class GameHostCompatibilityProbe
{
    public const string SchemaVersion = "junimogate.gamehost-probe/v1";

    private const string MainActivityNamespace = "StardewValley";
    private const string MainActivityName = "MainActivity";
    private const string InstanceFieldName = "instance";

    private static readonly ImmutableHashSet<string> LifecycleMethodNames =
        ImmutableHashSet.Create(StringComparer.Ordinal, ".ctor", "OnCreate", "OnResume", "OnPause", "OnDestroy");

    private static readonly ImmutableHashSet<string> BootstrapMethodNames =
        ImmutableHashSet.Create(StringComparer.Ordinal, "Bootstrap", "Main", "Run", "RunGame", "Start", "StartGame");

    public GameHostCompatibilityProbeResult Probe(
        GameHostCompatibilityProbeOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var state = new ProbeState(options.Limits, cancellationToken);

            TargetInspection target;
            try
            {
                target = InspectTarget(options.TargetAssemblyPath, state);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (ProbeLimitExceededException)
            {
                return Failure("gamehost_probe_metadata_limit_exceeded", "Managed metadata exceeds a configured probe bound.");
            }
            catch (Exception exception)
            {
                return InputFailure(options.TargetAssemblyPath, exception);
            }

            state.TargetAssemblyIdentity = target.Identity;

            if (target.MainActivities.Length == 0)
            {
                return Failure("gamehost_probe_main_activity_missing", "The target assembly does not define StardewValley.MainActivity.");
            }

            if (target.MainActivities.Length != 1)
            {
                return Failure("gamehost_probe_main_activity_duplicate", "The target assembly defines StardewValley.MainActivity more than once.");
            }

            var activity = target.MainActivities[0];
            if (activity.InstanceFields.Length == 0)
            {
                return Failure("gamehost_probe_instance_missing", "StardewValley.MainActivity does not define the instance field.");
            }

            if (activity.InstanceFields.Length != 1)
            {
                return Failure("gamehost_probe_instance_duplicate", "StardewValley.MainActivity defines the instance field more than once.");
            }

            if (!activity.InstanceFields[0].IsStatic)
            {
                return Failure("gamehost_probe_instance_signature_invalid", "StardewValley.MainActivity.instance is not static.");
            }

            foreach (var path in options.AssemblyPaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    InspectAssembly(path, state);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (ProbeLimitExceededException)
                {
                    return Failure("gamehost_probe_metadata_limit_exceeded", "Managed metadata exceeds a configured probe bound.");
                }
                catch (Exception exception)
                {
                    return InputFailure(path, exception);
                }
            }

            var fieldUses = state.FieldUses
                .OrderBy(evidence => evidence.AssemblyIdentity, StringComparer.Ordinal)
                .ThenBy(evidence => evidence.ContainingMethodSignature, StringComparer.Ordinal)
                .ThenBy(evidence => evidence.InstructionOrdinal)
                .ThenBy(evidence => evidence.OpCode, StringComparer.Ordinal)
                .ThenBy(evidence => evidence.FieldSignature, StringComparer.Ordinal)
                .ToImmutableArray();
            var callSites = state.CallSites
                .OrderBy(evidence => evidence.AssemblyIdentity, StringComparer.Ordinal)
                .ThenBy(evidence => evidence.ContainingMethodSignature, StringComparer.Ordinal)
                .ThenBy(evidence => evidence.InstructionOrdinal)
                .ThenBy(evidence => evidence.OpCode, StringComparer.Ordinal)
                .ThenBy(evidence => evidence.CalledMethodSignature, StringComparer.Ordinal)
                .ToImmutableArray();
            var pinvokes = state.PInvokes
                .OrderBy(evidence => evidence.ModuleName, StringComparer.Ordinal)
                .ThenBy(evidence => evidence.EntryPoint, StringComparer.Ordinal)
                .ThenBy(evidence => evidence.CallingConvention, StringComparer.Ordinal)
                .ThenBy(evidence => evidence.AssemblyIdentity, StringComparer.Ordinal)
                .ThenBy(evidence => evidence.MethodSignature, StringComparer.Ordinal)
                .ToImmutableArray();
            var interopAttributes = state.InteropAttributes
                .OrderBy(evidence => evidence.AssemblyIdentity, StringComparer.Ordinal)
                .ThenBy(evidence => evidence.OwnerSignature, StringComparer.Ordinal)
                .ThenBy(evidence => evidence.AttributeType, StringComparer.Ordinal)
                .ThenBy(evidence => evidence.ConstructorSignature, StringComparer.Ordinal)
                .ThenBy(evidence => string.Join("|", evidence.ArgumentFingerprints), StringComparer.Ordinal)
                .ToImmutableArray();

            var readCount = fieldUses.Count(evidence => evidence.Operation == FieldUseOperation.Read);
            var writeCount = fieldUses.Count(evidence => evidence.Operation == FieldUseOperation.Write);
            var addressCount = fieldUses.Count(evidence => evidence.Operation == FieldUseOperation.Address);
            var otherCount = fieldUses.Length - readCount - writeCount - addressCount;

            var targetEvidence = new TargetAssemblyEvidence(
                target.Identity,
                target.ModuleVersionId,
                target.TargetFramework,
                target.References
                    .Order(StringComparer.Ordinal)
                    .Select(identity => new AssemblyReferenceEvidence(identity))
                    .ToImmutableArray());
            var mainActivityEvidence = new MainActivityEvidence(
                activity.BaseType,
                activity.InstanceFields[0].Signature,
                activity.MethodSignatures.Order(StringComparer.Ordinal).ToImmutableArray(),
                activity.LifecycleMethodSignatures.Order(StringComparer.Ordinal).ToImmutableArray(),
                activity.BootstrapMethodSignatures.Order(StringComparer.Ordinal).ToImmutableArray());
            var evidence = new GameHostCompatibilityEvidence(
                SchemaVersion,
                targetEvidence,
                mainActivityEvidence,
                fieldUses,
                new FieldUseCounts(readCount, writeCount, addressCount, otherCount, fieldUses.Length),
                callSites,
                callSites.Length,
                pinvokes,
                interopAttributes);
            var managedEvidenceKey = ComputeManagedEvidenceKey(evidence);

            return new GameHostCompatibilityProbeResult(
                GameHostProbeStatus.Succeeded,
                managedEvidenceKey,
                evidence,
                ImmutableArray.Create(new GameHostProbeDiagnostic(
                    "gamehost_probe_succeeded",
                    GameHostProbeDiagnosticSeverity.Information,
                    "Managed compatibility metadata was inspected successfully.")));
        }
        catch (OperationCanceledException)
        {
            return new GameHostCompatibilityProbeResult(
                GameHostProbeStatus.Cancelled,
                null,
                null,
                ImmutableArray.Create(new GameHostProbeDiagnostic(
                    "gamehost_probe_cancelled",
                    GameHostProbeDiagnosticSeverity.Warning,
                    "Managed compatibility metadata inspection was cancelled.")));
        }
        catch (ProbeLimitExceededException)
        {
            return Failure("gamehost_probe_metadata_limit_exceeded", "Managed metadata exceeds a configured probe bound.");
        }
    }

    private static TargetInspection InspectTarget(string path, ProbeState state)
    {
        using var stream = OpenRead(path);
        using var assembly = ReadAssembly(stream);
        state.CancellationToken.ThrowIfCancellationRequested();

        var activities = new List<ActivityInspection>();
        foreach (var module in assembly.Modules)
        {
            foreach (var type in EnumerateTypes(module.Types, state))
            {
                if (!IsMainActivity(type))
                {
                    continue;
                }

                var instanceFields = type.Fields
                    .Where(field => field.Name.Equals(InstanceFieldName, StringComparison.Ordinal))
                    .Select(field => new FieldInspection(FormatField(field), field.IsStatic))
                    .ToImmutableArray();
                var methods = type.Methods.Select(FormatMethod).Order(StringComparer.Ordinal).ToImmutableArray();
                var lifecycle = type.Methods
                    .Where(method => LifecycleMethodNames.Contains(method.Name))
                    .Select(FormatMethod)
                    .Order(StringComparer.Ordinal)
                    .ToImmutableArray();
                var bootstrap = type.Methods
                    .Where(method => BootstrapMethodNames.Contains(method.Name))
                    .Select(FormatMethod)
                    .Order(StringComparer.Ordinal)
                    .ToImmutableArray();
                activities.Add(new ActivityInspection(
                    type.BaseType is null ? "<none>" : FormatType(type.BaseType),
                    instanceFields,
                    methods,
                    lifecycle,
                    bootstrap));
            }
        }

        var references = assembly.Modules
            .SelectMany(module => module.AssemblyReferences)
            .Select(FormatAssemblyIdentity)
            .ToImmutableArray();
        return new TargetInspection(
            FormatAssemblyIdentity(assembly.Name),
            assembly.MainModule.Mvid.ToString("D", CultureInfo.InvariantCulture).ToLowerInvariant(),
            ReadTargetFramework(assembly),
            references,
            activities.ToImmutableArray());
    }

    private static void InspectAssembly(string path, ProbeState state)
    {
        using var stream = OpenRead(path);
        using var assembly = ReadAssembly(stream);
        var assemblyIdentity = FormatAssemblyIdentity(assembly.Name);

        AddInteropAttributes(assembly, assemblyIdentity, $"assembly:{assemblyIdentity}", state);
        foreach (var module in assembly.Modules)
        {
            state.CancellationToken.ThrowIfCancellationRequested();
            AddInteropAttributes(module, assemblyIdentity, $"module:{SafeMetadataComponent(module.Name)}", state);

            foreach (var type in EnumerateTypes(module.Types, state))
            {
                state.CancellationToken.ThrowIfCancellationRequested();
                var typeSignature = FormatType(type);
                AddInteropAttributes(type, assemblyIdentity, $"type:{typeSignature}", state);

                foreach (var field in type.Fields)
                {
                    AddInteropAttributes(field, assemblyIdentity, $"field:{FormatField(field)}", state);
                }

                foreach (var property in type.Properties)
                {
                    AddInteropAttributes(property, assemblyIdentity, $"property:{FormatType(property.PropertyType)} {typeSignature}::{SafeMetadataComponent(property.Name)}", state);
                }

                foreach (var @event in type.Events)
                {
                    AddInteropAttributes(@event, assemblyIdentity, $"event:{FormatType(@event.EventType)} {typeSignature}::{SafeMetadataComponent(@event.Name)}", state);
                }

                foreach (var method in type.Methods)
                {
                    state.CountMethod();
                    state.CancellationToken.ThrowIfCancellationRequested();
                    var methodSignature = FormatMethod(method);
                    AddInteropAttributes(method, assemblyIdentity, $"method:{methodSignature}", state);

                    if (method.IsPInvokeImpl && method.PInvokeInfo is not null)
                    {
                        state.AddEvidence();
                        state.PInvokes.Add(CreatePInvokeEvidence(assemblyIdentity, method, method.PInvokeInfo));
                    }

                    if (!method.HasBody)
                    {
                        continue;
                    }

                    var instructions = method.Body.Instructions;
                    state.CountInstructions(instructions.Count);
                    var methodFieldUses = new List<FieldUseEvidence>();
                    for (var index = 0; index < instructions.Count; index++)
                    {
                        if (instructions[index].Operand is not FieldReference field ||
                            !IsTargetInstanceField(field, state.TargetAssemblyIdentity))
                        {
                            continue;
                        }

                        state.AddEvidence();
                        var fieldUse = new FieldUseEvidence(
                            assemblyIdentity,
                            methodSignature,
                            index,
                            instructions[index].OpCode.Name,
                            ClassifyFieldUse(instructions[index].OpCode),
                            FormatField(field));
                        methodFieldUses.Add(fieldUse);
                        state.FieldUses.Add(fieldUse);
                    }

                    var containsTargetFieldUse = methodFieldUses.Count != 0;
                    for (var index = 0; index < instructions.Count; index++)
                    {
                        if (instructions[index].Operand is not MethodReference calledMethod ||
                            !IsCallSiteOpCode(instructions[index].OpCode) ||
                            (!containsTargetFieldUse &&
                             !IsTargetMainActivity(calledMethod.DeclaringType, state.TargetAssemblyIdentity)))
                        {
                            continue;
                        }

                        state.AddEvidence();
                        state.CallSites.Add(new CallSiteEvidence(
                            assemblyIdentity,
                            methodSignature,
                            index,
                            instructions[index].OpCode.Name,
                            FormatMethod(calledMethod),
                            IsTargetMainActivity(calledMethod.DeclaringType, state.TargetAssemblyIdentity)));
                    }
                }
            }
        }
    }

    private static IEnumerable<TypeDefinition> EnumerateTypes(IEnumerable<TypeDefinition> roots, ProbeState state)
    {
        var stack = new Stack<TypeDefinition>(roots.Reverse());
        while (stack.Count != 0)
        {
            state.CancellationToken.ThrowIfCancellationRequested();
            var type = stack.Pop();
            state.CountType();
            yield return type;
            for (var index = type.NestedTypes.Count - 1; index >= 0; index--)
            {
                stack.Push(type.NestedTypes[index]);
            }
        }
    }

    private static void AddInteropAttributes(
        ICustomAttributeProvider provider,
        string assemblyIdentity,
        string ownerSignature,
        ProbeState state)
    {
        if (!provider.HasCustomAttributes)
        {
            return;
        }

        foreach (var attribute in provider.CustomAttributes)
        {
            state.CancellationToken.ThrowIfCancellationRequested();
            if (!IsInteropAttribute(attribute.AttributeType))
            {
                continue;
            }

            var blob = attribute.GetBlob();
            var fingerprints = ImmutableArray.Create(
                $"blob:{blob.Length}:{HashBytes(blob)}");
            state.AddEvidence();
            state.InteropAttributes.Add(new InteropAttributeEvidence(
                assemblyIdentity,
                ownerSignature,
                FormatType(attribute.AttributeType),
                FormatMethod(attribute.Constructor),
                fingerprints));
        }
    }

    private static bool IsInteropAttribute(TypeReference type)
    {
        var fullName = $"{type.Namespace}.{type.Name}";
        return fullName.StartsWith("Android.", StringComparison.Ordinal) ||
               fullName.StartsWith("Java.", StringComparison.Ordinal) ||
               fullName.StartsWith("Xamarin.", StringComparison.Ordinal);
    }

    private static string HashBytes(ReadOnlySpan<byte> value) =>
        Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();

    private static PInvokeEvidence CreatePInvokeEvidence(
        string assemblyIdentity,
        MethodDefinition method,
        PInvokeInfo info) =>
        new(
            assemblyIdentity,
            FormatMethod(method),
            SafeMetadataComponent(info.Module.Name),
            SafeMetadataComponent(info.EntryPoint),
            GetCallingConvention(info.Attributes),
            GetCharacterSet(info.Attributes),
            FormatPInvokeAttributes(info));

    private static string GetCallingConvention(PInvokeAttributes attributes)
    {
        var convention = attributes & PInvokeAttributes.CallConvMask;
        return convention switch
        {
            PInvokeAttributes.CallConvCdecl => "cdecl",
            PInvokeAttributes.CallConvFastcall => "fastcall",
            PInvokeAttributes.CallConvStdCall => "stdcall",
            PInvokeAttributes.CallConvThiscall => "thiscall",
            PInvokeAttributes.CallConvWinapi => "winapi",
            _ => $"unknown-0x{((int)convention):x4}",
        };
    }

    private static string GetCharacterSet(PInvokeAttributes attributes)
    {
        var characterSet = attributes & PInvokeAttributes.CharSetMask;
        return characterSet switch
        {
            PInvokeAttributes.CharSetAnsi => "ansi",
            PInvokeAttributes.CharSetAuto => "auto",
            PInvokeAttributes.CharSetUnicode => "unicode",
            PInvokeAttributes.CharSetNotSpec => "not-specified",
            _ => $"unknown-0x{((int)characterSet):x4}",
        };
    }

    private static string FormatPInvokeAttributes(PInvokeInfo info) =>
        $"0x{((int)info.Attributes):x4};" +
        $"best-fit-enabled={info.IsBestFitEnabled};best-fit-disabled={info.IsBestFitDisabled};" +
        $"last-error={info.SupportsLastError};no-mangle={info.IsNoMangle};" +
        $"throw-on-unmappable-enabled={info.IsThrowOnUnmappableCharEnabled};" +
        $"throw-on-unmappable-disabled={info.IsThrowOnUnmappableCharDisabled}";

    private static FieldUseOperation ClassifyFieldUse(OpCode opCode)
    {
        if (opCode == OpCodes.Ldsfld || opCode == OpCodes.Ldfld)
        {
            return FieldUseOperation.Read;
        }

        if (opCode == OpCodes.Stsfld || opCode == OpCodes.Stfld)
        {
            return FieldUseOperation.Write;
        }

        if (opCode == OpCodes.Ldsflda || opCode == OpCodes.Ldflda)
        {
            return FieldUseOperation.Address;
        }

        return FieldUseOperation.Other;
    }

    private static bool IsCallSiteOpCode(OpCode opCode) =>
        opCode == OpCodes.Call ||
        opCode == OpCodes.Callvirt ||
        opCode == OpCodes.Newobj ||
        opCode == OpCodes.Jmp ||
        opCode == OpCodes.Ldftn ||
        opCode == OpCodes.Ldvirtftn;

    private static bool IsTargetInstanceField(FieldReference field, string targetAssemblyIdentity) =>
        field.Name.Equals(InstanceFieldName, StringComparison.Ordinal) &&
        IsTargetMainActivity(field.DeclaringType, targetAssemblyIdentity);

    private static bool IsTargetMainActivity(TypeReference type, string targetAssemblyIdentity) =>
        IsMainActivity(type) &&
        string.Equals(GetAssemblyIdentity(type.Scope), targetAssemblyIdentity, StringComparison.Ordinal);

    private static bool IsMainActivity(TypeReference type) =>
        type.DeclaringType is null &&
        type.Namespace.Equals(MainActivityNamespace, StringComparison.Ordinal) &&
        type.Name.Equals(MainActivityName, StringComparison.Ordinal);

    private static string? GetAssemblyIdentity(IMetadataScope scope) => scope switch
    {
        AssemblyNameReference assembly => FormatAssemblyIdentity(assembly),
        ModuleDefinition module when module.Assembly is not null => FormatAssemblyIdentity(module.Assembly.Name),
        _ => null,
    };

    private static string? ReadTargetFramework(AssemblyDefinition assembly)
    {
        var attributes = assembly.CustomAttributes
            .Where(candidate =>
                candidate.AttributeType.Namespace.Equals("System.Runtime.Versioning", StringComparison.Ordinal) &&
                candidate.AttributeType.Name.Equals("TargetFrameworkAttribute", StringComparison.Ordinal))
            .Take(2)
            .ToImmutableArray();
        if (attributes.IsDefaultOrEmpty)
        {
            return null;
        }

        if (attributes.Length != 1 ||
            attributes[0].ConstructorArguments.Count != 1 ||
            attributes[0].ConstructorArguments[0].Value is not string value)
        {
            throw new BadImageFormatException("Invalid target framework metadata.");
        }

        return SafeMetadataComponent(value);
    }

    private static string FormatAssemblyIdentity(AssemblyNameReference name)
    {
        var culture = string.IsNullOrEmpty(name.Culture) ? "neutral" : SafeMetadataComponent(name.Culture);
        var token = name.PublicKeyToken is { Length: > 0 }
            ? Convert.ToHexString(name.PublicKeyToken).ToLowerInvariant()
            : "null";
        return $"{SafeMetadataComponent(name.Name)}, Version={name.Version}, Culture={culture}, PublicKeyToken={token}";
    }

    private static string FormatField(FieldReference field) =>
        $"{FormatType(field.FieldType)} {FormatType(field.DeclaringType)}::{SafeMetadataComponent(field.Name)}";

    private static string FormatMethod(MethodReference method)
    {
        var parameters = string.Join(",", method.Parameters.Select(parameter => FormatType(parameter.ParameterType)));
        var instance = method.HasThis ? "instance" : "static";
        var explicitThis = method.ExplicitThis ? ";explicit-this" : string.Empty;
        return $"{instance};callconv={method.CallingConvention}{explicitThis};generic-arity={method.GenericParameters.Count};" +
               $"{FormatType(method.ReturnType)} {FormatType(method.DeclaringType)}::{SafeMetadataComponent(method.Name)}({parameters})";
    }

    private static string FormatType(TypeReference type) => type switch
    {
        ByReferenceType byReference => $"{FormatType(byReference.ElementType)}&",
        PointerType pointer => $"{FormatType(pointer.ElementType)}*",
        ArrayType array => $"{FormatType(array.ElementType)}[{new string(',', Math.Max(0, array.Rank - 1))}]",
        GenericInstanceType generic => $"{FormatType(generic.ElementType)}<{string.Join(",", generic.GenericArguments.Select(FormatType))}>",
        OptionalModifierType optional => $"{FormatType(optional.ElementType)} modopt({FormatType(optional.ModifierType)})",
        RequiredModifierType required => $"{FormatType(required.ElementType)} modreq({FormatType(required.ModifierType)})",
        PinnedType pinned => $"{FormatType(pinned.ElementType)} pinned",
        SentinelType sentinel => $"{FormatType(sentinel.ElementType)} sentinel",
        GenericParameter parameter => parameter.Type == GenericParameterType.Method ? $"!!{parameter.Position}" : $"!{parameter.Position}",
        _ when type.DeclaringType is not null => $"{FormatType(type.DeclaringType)}+{SafeMetadataComponent(type.Name)}",
        _ when string.IsNullOrEmpty(type.Namespace) => SafeMetadataComponent(type.Name),
        _ => $"{SafeMetadataComponent(type.Namespace)}.{SafeMetadataComponent(type.Name)}",
    };

    private static string SafeMetadataComponent(string? value)
    {
        value ??= string.Empty;
        var hasPathShape = value.Contains('/') || value.Contains('\\') || value.StartsWith("file:", StringComparison.OrdinalIgnoreCase);
        var hasControl = value.Any(char.IsControl);
        if (hasPathShape || hasControl || Encoding.UTF8.GetByteCount(value) > 512)
        {
            return $"redacted-sha256:{HashText(value)}:length={Encoding.UTF8.GetByteCount(value)}";
        }

        return value;
    }

    private static string HashText(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string ComputeManagedEvidenceKey(GameHostCompatibilityEvidence evidence)
    {
        var canonical = new CanonicalHashBuilder();
        canonical.Add("schema", evidence.SchemaVersion);
        canonical.Add("target.identity", evidence.TargetAssembly.Identity);
        canonical.Add("target.mvid", evidence.TargetAssembly.ModuleVersionId);
        canonical.Add("target.framework", evidence.TargetAssembly.TargetFramework ?? "<none>");
        canonical.AddArray("target.reference", evidence.TargetAssembly.References.Select(reference => reference.Identity));
        canonical.Add("activity.base", evidence.MainActivity.BaseType);
        canonical.Add("activity.instance", evidence.MainActivity.InstanceFieldSignature);
        canonical.AddArray("activity.method", evidence.MainActivity.MethodSignatures);
        canonical.AddArray("activity.lifecycle", evidence.MainActivity.LifecycleMethodSignatures);
        canonical.AddArray("activity.bootstrap", evidence.MainActivity.BootstrapMethodSignatures);
        canonical.AddArray("field-use", evidence.FieldUses.Select(FormatCanonical));
        canonical.Add("field-use.count.read", evidence.FieldUseCounts.Read.ToString(CultureInfo.InvariantCulture));
        canonical.Add("field-use.count.write", evidence.FieldUseCounts.Write.ToString(CultureInfo.InvariantCulture));
        canonical.Add("field-use.count.address", evidence.FieldUseCounts.Address.ToString(CultureInfo.InvariantCulture));
        canonical.Add("field-use.count.other", evidence.FieldUseCounts.Other.ToString(CultureInfo.InvariantCulture));
        canonical.Add("field-use.count.total", evidence.FieldUseCounts.Total.ToString(CultureInfo.InvariantCulture));
        canonical.AddArray("call-site", evidence.CallSites.Select(FormatCanonical));
        canonical.Add("call-site.count", evidence.CallSiteCount.ToString(CultureInfo.InvariantCulture));
        canonical.AddArray("pinvoke", evidence.PInvokes.Select(FormatCanonical));
        canonical.AddArray("interop", evidence.InteropAttributes.Select(FormatCanonical));
        return canonical.GetHash();
    }

    private static string FormatCanonical(FieldUseEvidence evidence) =>
        $"{evidence.AssemblyIdentity}|{evidence.ContainingMethodSignature}|{evidence.InstructionOrdinal}|{evidence.OpCode}|{evidence.Operation}|{evidence.FieldSignature}";

    private static string FormatCanonical(CallSiteEvidence evidence) =>
        $"{evidence.AssemblyIdentity}|{evidence.ContainingMethodSignature}|{evidence.InstructionOrdinal}|{evidence.OpCode}|{evidence.CalledMethodSignature}|{evidence.TargetsMainActivity}";

    private static string FormatCanonical(PInvokeEvidence evidence) =>
        $"{evidence.ModuleName}|{evidence.EntryPoint}|{evidence.CallingConvention}|{evidence.CharacterSet}|{evidence.Attributes}|{evidence.AssemblyIdentity}|{evidence.MethodSignature}";

    private static string FormatCanonical(InteropAttributeEvidence evidence) =>
        $"{evidence.AssemblyIdentity}|{evidence.OwnerSignature}|{evidence.AttributeType}|{evidence.ConstructorSignature}|{string.Join("|", evidence.ArgumentFingerprints)}";

    private static FileStream OpenRead(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);

    private static AssemblyDefinition ReadAssembly(Stream stream) =>
        AssemblyDefinition.ReadAssembly(stream, new ReaderParameters
        {
            ReadingMode = ReadingMode.Deferred,
            ReadSymbols = false,
            InMemory = false,
        });

    private static GameHostCompatibilityProbeResult InputFailure(string path, Exception exception)
    {
        var inputName = SafeInputName(path);
        return IsUnreadable(exception)
            ? Failure(
                "gamehost_probe_assembly_unreadable",
                $"Managed metadata input '{inputName}' could not be opened ({exception.GetType().Name}).")
            : Failure(
                "gamehost_probe_assembly_malformed",
                $"Managed metadata input '{inputName}' is not valid bounded managed metadata ({exception.GetType().Name}).");
    }

    private static string SafeInputName(string path)
    {
        var name = Path.GetFileName(path);
        return string.IsNullOrWhiteSpace(name) ||
            Encoding.UTF8.GetByteCount(name) > 255 ||
            name.Any(static character => char.IsControl(character) || character is '/' or '\\')
            ? $"redacted-sha256:{HashText(name ?? string.Empty)}"
            : name;
    }

    private static bool IsUnreadable(Exception exception) =>
        exception is IOException and not EndOfStreamException or UnauthorizedAccessException;

    private static GameHostCompatibilityProbeResult Failure(string code, string message) =>
        new(
            GameHostProbeStatus.Failed,
            null,
            null,
            ImmutableArray.Create(new GameHostProbeDiagnostic(code, GameHostProbeDiagnosticSeverity.Error, message)));

    private sealed record FieldInspection(string Signature, bool IsStatic);

    private sealed record ActivityInspection(
        string BaseType,
        ImmutableArray<FieldInspection> InstanceFields,
        ImmutableArray<string> MethodSignatures,
        ImmutableArray<string> LifecycleMethodSignatures,
        ImmutableArray<string> BootstrapMethodSignatures);

    private sealed record TargetInspection(
        string Identity,
        string ModuleVersionId,
        string? TargetFramework,
        ImmutableArray<string> References,
        ImmutableArray<ActivityInspection> MainActivities);

    private sealed class ProbeState
    {
        private readonly GameHostProbeLimits limits;
        private int typeCount;
        private int methodCount;
        private int instructionCount;
        private int evidenceCount;

        public ProbeState(GameHostProbeLimits limits, CancellationToken cancellationToken)
        {
            this.limits = limits;
            CancellationToken = cancellationToken;
        }

        public CancellationToken CancellationToken { get; }

        public string TargetAssemblyIdentity { get; set; } = string.Empty;

        public List<FieldUseEvidence> FieldUses { get; } = [];

        public List<CallSiteEvidence> CallSites { get; } = [];

        public List<PInvokeEvidence> PInvokes { get; } = [];

        public List<InteropAttributeEvidence> InteropAttributes { get; } = [];

        public void CountType()
        {
            typeCount = checked(typeCount + 1);
            if (typeCount > limits.MaxTypes)
            {
                throw new ProbeLimitExceededException();
            }
        }

        public void CountMethod()
        {
            methodCount = checked(methodCount + 1);
            if (methodCount > limits.MaxMethods)
            {
                throw new ProbeLimitExceededException();
            }
        }

        public void CountInstructions(int count)
        {
            instructionCount = checked(instructionCount + count);
            if (instructionCount > limits.MaxInstructions)
            {
                throw new ProbeLimitExceededException();
            }
        }

        public void AddEvidence()
        {
            evidenceCount = checked(evidenceCount + 1);
            if (evidenceCount > limits.MaxEvidenceItems)
            {
                throw new ProbeLimitExceededException();
            }
        }
    }

    private sealed class ProbeLimitExceededException : Exception;

    private sealed class CanonicalHashBuilder
    {
        private readonly IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        public void Add(string name, string value)
        {
            var text = $"{Encoding.UTF8.GetByteCount(name)}:{name}={Encoding.UTF8.GetByteCount(value)}:{value}\n";
            hash.AppendData(Encoding.UTF8.GetBytes(text));
        }

        public void AddArray(string name, IEnumerable<string> values)
        {
            var materialized = values.ToImmutableArray();
            Add($"{name}.count", materialized.Length.ToString(CultureInfo.InvariantCulture));
            for (var index = 0; index < materialized.Length; index++)
            {
                Add($"{name}[{index}]", materialized[index]);
            }
        }

        public string GetHash() => Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }
}
