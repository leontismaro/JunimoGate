using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace JunimoGate.Rewriter;

/// <summary>
/// Reads only the managed metadata needed to evaluate an Android Activity bridge. It never resolves,
/// loads, writes, rewrites, or stages an assembly.
/// </summary>
public sealed class ActivityBridgeCompatibilityProbe
{
    public const string SchemaVersion = "junimogate.activity-bridge-probe/v1";

    private const string MonoGameNamespace = "Microsoft.Xna.Framework";
    private const string AndroidGameActivityName = "AndroidGameActivity";
    private const string GameName = "Game";
    private const string GameServiceContainerName = "GameServiceContainer";
    private const string StardewNamespace = "StardewValley";
    private const string GameRunnerName = "GameRunner";
    private const string MainActivityName = "MainActivity";

    private static readonly ImmutableHashSet<string> ActivityLifecycleNames =
        ImmutableHashSet.Create(StringComparer.Ordinal, ".ctor", "OnCreate", "OnResume", "OnPause", "OnDestroy");

    private static readonly ImmutableHashSet<string> MainActivityBodyNames =
        ImmutableHashSet.Create(StringComparer.Ordinal, "OnCreate", "OnResume", "OnPause", "OnDestroy");

    public ActivityBridgeCompatibilityProbeResult Probe(
        ActivityBridgeCompatibilityProbeOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var state = new ProbeState(options.Limits, cancellationToken);
            using var monoGameStream = OpenRead(options.MonoGameAssemblyPath);
            using var gameStream = OpenRead(options.GameAssemblyPath);
            using var monoGameAssembly = ReadAssembly(monoGameStream);
            using var gameAssembly = ReadAssembly(gameStream);

            var monoGameIdentity = FormatAssemblyIdentity(monoGameAssembly.Name);
            var gameIdentity = FormatAssemblyIdentity(gameAssembly.Name);
            var monoGameTypes = EnumerateTypes(
                monoGameAssembly.Modules.SelectMany(static module => module.Types),
                state).ToArray();
            var gameTypes = EnumerateTypes(
                gameAssembly.Modules.SelectMany(static module => module.Types),
                state).ToArray();
            var androidGameActivity = FindSingleType(
                monoGameTypes,
                MonoGameNamespace,
                AndroidGameActivityName,
                "Microsoft.Xna.Framework.AndroidGameActivity");
            var game = FindSingleType(
                monoGameTypes,
                MonoGameNamespace,
                GameName,
                "Microsoft.Xna.Framework.Game");
            var gameServiceContainer = FindSingleType(
                monoGameTypes,
                MonoGameNamespace,
                GameServiceContainerName,
                "Microsoft.Xna.Framework.GameServiceContainer");
            var gameRunner = FindSingleType(
                gameTypes,
                StardewNamespace,
                GameRunnerName,
                "StardewValley.GameRunner");
            var mainActivity = FindSingleType(
                gameTypes,
                StardewNamespace,
                MainActivityName,
                "StardewValley.MainActivity");

            var monoGameEvidence = new MonoGameBridgeEvidence(
                InspectType(androidGameActivity, monoGameIdentity, state),
                InspectType(game, monoGameIdentity, state),
                MethodsNamed(game, "Run"),
                MethodsNamed(game, "Exit"),
                PropertiesNamed(game, "Services"),
                InspectType(gameServiceContainer, monoGameIdentity, state),
                MethodsNamed(gameServiceContainer, "GetService"));
            var runnerEvidence = new GameRunnerBridgeEvidence(
                InspectType(gameRunner, gameIdentity, state),
                gameRunner.Fields
                    .Where(static field => field.IsStatic && field.Name.Equals("instance", StringComparison.Ordinal))
                    .Select(FormatField)
                    .Order(StringComparer.Ordinal)
                    .ToImmutableArray(),
                MethodsNamed(gameRunner, "Run"));
            var mainActivityEvidence = new MainActivityBridgeEvidence(
                InspectType(mainActivity, gameIdentity, state),
                InspectLifecycleBodies(mainActivity, state));

            if (monoGameEvidence.GameRunMethodSignatures.IsDefaultOrEmpty ||
                monoGameEvidence.GameServicesPropertySignatures.IsDefaultOrEmpty ||
                monoGameEvidence.GetServiceMethodSignatures.IsDefaultOrEmpty ||
                runnerEvidence.StaticInstanceFieldSignatures.Length != 1 ||
                !mainActivityEvidence.LifecycleBodies.Any(static body =>
                    body.MethodSignature.Contains("::OnCreate(", StringComparison.Ordinal)))
            {
                return Failure(
                    "gamehost_bridge_probe_contract_incomplete",
                    "Required MonoGame/GameRunner/MainActivity bridge metadata is incomplete.");
            }

            var requirements = ManagedApiCompatibilityInspector.InspectRequirements(
                options.ConsumerAssemblyPaths,
                "MonoGame.Framework",
                cancellationToken: cancellationToken);
            var evidence = new ActivityBridgeCompatibilityEvidence(
                SchemaVersion,
                options.ParentSupportKey,
                InspectAssembly(monoGameAssembly, cancellationToken),
                InspectAssembly(gameAssembly, cancellationToken),
                requirements,
                monoGameEvidence,
                runnerEvidence,
                mainActivityEvidence);
            var evidenceKey = ComputeEvidenceKey(evidence);
            return new ActivityBridgeCompatibilityProbeResult(
                ActivityBridgeProbeStatus.Succeeded,
                evidenceKey,
                evidence,
                ImmutableArray.Create(new ActivityBridgeProbeDiagnostic(
                    "gamehost_bridge_probe_succeeded",
                    ActivityBridgeProbeDiagnosticSeverity.Information,
                    "Activity bridge metadata was inspected successfully without loading game code.")));
        }
        catch (OperationCanceledException)
        {
            return new ActivityBridgeCompatibilityProbeResult(
                ActivityBridgeProbeStatus.Cancelled,
                null,
                null,
                ImmutableArray.Create(new ActivityBridgeProbeDiagnostic(
                    "gamehost_bridge_probe_cancelled",
                    ActivityBridgeProbeDiagnosticSeverity.Warning,
                    "Activity bridge metadata inspection was cancelled.")));
        }
        catch (ActivityBridgeProbeLimitExceededException)
        {
            return Failure(
                "gamehost_bridge_probe_metadata_limit_exceeded",
                "Activity bridge metadata exceeds a configured probe bound.");
        }
        catch (ActivityBridgeTypeException exception)
        {
            return Failure(exception.Code, exception.Message);
        }
        catch (EndOfStreamException exception)
        {
            return InputFailure(
                "gamehost_bridge_probe_assembly_malformed",
                options,
                exception,
                "A managed bridge input contains truncated metadata");
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException or IOException)
        {
            return InputFailure(
                "gamehost_bridge_probe_assembly_unreadable",
                options,
                exception,
                "A managed bridge input could not be opened");
        }
        catch (Exception exception)
        {
            return InputFailure(
                "gamehost_bridge_probe_assembly_malformed",
                options,
                exception,
                "A managed bridge input contains malformed metadata");
        }
    }

    private static ActivityBridgeAssemblyEvidence InspectAssembly(
        AssemblyDefinition assembly,
        CancellationToken cancellationToken) =>
        new(
            FormatAssemblyIdentity(assembly.Name),
            assembly.MainModule.Mvid.ToString("D", CultureInfo.InvariantCulture).ToLowerInvariant(),
            ReadTargetFramework(assembly),
            ManagedPublicApiSurfaceInspector.Inspect(
                assembly,
                ManagedPublicApiSurfaceLimits.Default,
                cancellationToken));

    private static ActivityBridgeTypeEvidence InspectType(
        TypeDefinition type,
        string assemblyIdentity,
        ProbeState state)
    {
        state.CancellationToken.ThrowIfCancellationRequested();
        var lifecycle = type.Methods
            .Where(method => ActivityLifecycleNames.Contains(method.Name))
            .Select(FormatMethod)
            .Order(StringComparer.Ordinal)
            .ToImmutableArray();
        var attributes = new List<InteropAttributeEvidence>();
        AddInteropAttributes(type, assemblyIdentity, $"type:{FormatType(type)}", attributes, state);
        foreach (var method in type.Methods.Where(method => ActivityLifecycleNames.Contains(method.Name)))
        {
            AddInteropAttributes(method, assemblyIdentity, $"method:{FormatMethod(method)}", attributes, state);
        }

        return new ActivityBridgeTypeEvidence(
            FormatType(type),
            type.BaseType is null ? "<none>" : FormatType(type.BaseType),
            type.IsAbstract,
            type.IsSealed,
            type.Methods
                .Where(static method => method.IsConstructor)
                .Select(FormatMethod)
                .Order(StringComparer.Ordinal)
                .ToImmutableArray(),
            lifecycle,
            attributes
                .OrderBy(static attribute => attribute.OwnerSignature, StringComparer.Ordinal)
                .ThenBy(static attribute => attribute.AttributeType, StringComparer.Ordinal)
                .ThenBy(static attribute => attribute.ConstructorSignature, StringComparer.Ordinal)
                .ThenBy(static attribute => string.Join("|", attribute.ArgumentFingerprints), StringComparer.Ordinal)
                .ToImmutableArray());
    }

    private static ImmutableArray<ActivityBridgeLifecycleBodyEvidence> InspectLifecycleBodies(
        TypeDefinition mainActivity,
        ProbeState state)
    {
        var bodies = new List<ActivityBridgeLifecycleBodyEvidence>();
        foreach (var method in mainActivity.Methods
                     .Where(method => MainActivityBodyNames.Contains(method.Name))
                     .OrderBy(FormatMethod, StringComparer.Ordinal))
        {
            state.CancellationToken.ThrowIfCancellationRequested();
            var methodSignature = FormatMethod(method);
            if (!method.HasBody)
            {
                bodies.Add(new ActivityBridgeLifecycleBodyEvidence(
                    methodSignature,
                    0,
                    ImmutableArray<ActivityBridgeCallEvidence>.Empty,
                    ImmutableArray<ActivityBridgeFieldEvidence>.Empty));
                continue;
            }

            state.CountInstructions(method.Body.Instructions.Count);
            var calls = new List<ActivityBridgeCallEvidence>();
            var fields = new List<ActivityBridgeFieldEvidence>();
            for (var index = 0; index < method.Body.Instructions.Count; index++)
            {
                state.CancellationToken.ThrowIfCancellationRequested();
                var instruction = method.Body.Instructions[index];
                if (instruction.Operand is MethodReference called && IsCallSiteOpCode(instruction.OpCode))
                {
                    state.AddEvidence();
                    calls.Add(new ActivityBridgeCallEvidence(index, instruction.OpCode.Name, FormatMethod(called)));
                }
                else if (instruction.Operand is FieldReference field)
                {
                    state.AddEvidence();
                    fields.Add(new ActivityBridgeFieldEvidence(index, instruction.OpCode.Name, FormatField(field)));
                }
            }

            bodies.Add(new ActivityBridgeLifecycleBodyEvidence(
                methodSignature,
                method.Body.Instructions.Count,
                calls.ToImmutableArray(),
                fields.ToImmutableArray()));
        }

        return bodies.ToImmutableArray();
    }

    private static void AddInteropAttributes(
        ICustomAttributeProvider provider,
        string assemblyIdentity,
        string ownerSignature,
        ICollection<InteropAttributeEvidence> target,
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
            state.AddEvidence();
            target.Add(new InteropAttributeEvidence(
                assemblyIdentity,
                ownerSignature,
                FormatType(attribute.AttributeType),
                FormatMethod(attribute.Constructor),
                ImmutableArray.Create($"blob:{blob.Length}:{HashBytes(blob)}")));
        }
    }

    private static TypeDefinition FindSingleType(
        IEnumerable<TypeDefinition> types,
        string @namespace,
        string name,
        string logicalName)
    {
        var matches = types
            .Where(type => type.Namespace.Equals(@namespace, StringComparison.Ordinal) &&
                type.Name.Equals(name, StringComparison.Ordinal))
            .ToArray();
        if (matches.Length == 0)
        {
            throw new ActivityBridgeTypeException(
                "gamehost_bridge_probe_type_missing",
                $"Required bridge type '{logicalName}' is missing.");
        }

        if (matches.Length != 1)
        {
            throw new ActivityBridgeTypeException(
                "gamehost_bridge_probe_type_duplicate",
                $"Required bridge type '{logicalName}' is duplicated.");
        }

        return matches[0];
    }

    private static IEnumerable<TypeDefinition> EnumerateTypes(
        IEnumerable<TypeDefinition> roots,
        ProbeState state)
    {
        var stack = new Stack<TypeDefinition>(roots.Reverse());
        while (stack.Count != 0)
        {
            state.CancellationToken.ThrowIfCancellationRequested();
            var type = stack.Pop();
            state.CountType();
            state.CountMembers(
                checked(type.Fields.Count + type.Methods.Count + type.Properties.Count +
                    type.Events.Count + type.NestedTypes.Count));
            yield return type;
            for (var index = type.NestedTypes.Count - 1; index >= 0; index--)
            {
                stack.Push(type.NestedTypes[index]);
            }
        }
    }

    private static ImmutableArray<string> MethodsNamed(TypeDefinition type, string name) =>
        type.Methods
            .Where(method => method.Name.Equals(name, StringComparison.Ordinal))
            .Select(FormatMethod)
            .Order(StringComparer.Ordinal)
            .ToImmutableArray();

    private static ImmutableArray<string> PropertiesNamed(TypeDefinition type, string name) =>
        type.Properties
            .Where(property => property.Name.Equals(name, StringComparison.Ordinal))
            .Select(property => $"{FormatType(property.PropertyType)} {FormatType(type)}::{SafeMetadataComponent(property.Name)}")
            .Order(StringComparer.Ordinal)
            .ToImmutableArray();

    private static string? ReadTargetFramework(AssemblyDefinition assembly)
    {
        var attribute = assembly.CustomAttributes.FirstOrDefault(candidate =>
            candidate.AttributeType.Namespace.Equals("System.Runtime.Versioning", StringComparison.Ordinal) &&
            candidate.AttributeType.Name.Equals("TargetFrameworkAttribute", StringComparison.Ordinal));
        if (attribute is null || attribute.ConstructorArguments.Count == 0 ||
            attribute.ConstructorArguments[0].Value is not string value)
        {
            return null;
        }

        return SafeMetadataComponent(value);
    }

    private static bool IsInteropAttribute(TypeReference type)
    {
        var fullName = $"{type.Namespace}.{type.Name}";
        return fullName.StartsWith("Android.", StringComparison.Ordinal) ||
               fullName.StartsWith("Java.", StringComparison.Ordinal) ||
               fullName.StartsWith("Xamarin.", StringComparison.Ordinal);
    }

    private static bool IsCallSiteOpCode(OpCode opCode) =>
        opCode == OpCodes.Call ||
        opCode == OpCodes.Callvirt ||
        opCode == OpCodes.Newobj ||
        opCode == OpCodes.Jmp ||
        opCode == OpCodes.Ldftn ||
        opCode == OpCodes.Ldvirtftn;

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
        var hasPathShape = value.Contains('/') || value.Contains('\\') ||
            value.StartsWith("file:", StringComparison.OrdinalIgnoreCase);
        if (hasPathShape || value.Any(char.IsControl) || Encoding.UTF8.GetByteCount(value) > 512)
        {
            return $"redacted-sha256:{HashText(value)}:length={Encoding.UTF8.GetByteCount(value)}";
        }

        return value;
    }

    private static string ComputeEvidenceKey(ActivityBridgeCompatibilityEvidence evidence)
    {
        var hash = new CanonicalHashBuilder();
        hash.Add("schema", evidence.SchemaVersion);
        hash.Add("parentSupportKey", evidence.ParentSupportKey);
        AddAssembly(hash, "monogame.assembly", evidence.MonoGameAssembly);
        AddAssembly(hash, "game.assembly", evidence.GameAssembly);
        hash.Add("monogame.requirements.schema", evidence.MonoGameRequirements.SchemaVersion);
        hash.Add("monogame.requirements.target", evidence.MonoGameRequirements.TargetAssemblyName);
        hash.Add("monogame.requirements.key", evidence.MonoGameRequirements.RequirementsKey);
        hash.Add("monogame.requirements.consumers", evidence.MonoGameRequirements.ConsumerAssemblyCount.ToString(CultureInfo.InvariantCulture));
        hash.AddArray("monogame.requirements.types", evidence.MonoGameRequirements.TypeRequirementHashes);
        hash.AddArray("monogame.requirements.members", evidence.MonoGameRequirements.MemberRequirementHashes);
        AddType(hash, "monogame.activity", evidence.MonoGame.AndroidGameActivity);
        AddType(hash, "monogame.game", evidence.MonoGame.Game);
        hash.AddArray("monogame.game.run", evidence.MonoGame.GameRunMethodSignatures);
        hash.AddArray("monogame.game.exit", evidence.MonoGame.GameExitMethodSignatures);
        hash.AddArray("monogame.game.services", evidence.MonoGame.GameServicesPropertySignatures);
        AddType(hash, "monogame.services", evidence.MonoGame.GameServiceContainer);
        hash.AddArray("monogame.services.get", evidence.MonoGame.GetServiceMethodSignatures);
        AddType(hash, "game.runner", evidence.GameRunner.Type);
        hash.AddArray("game.runner.instance", evidence.GameRunner.StaticInstanceFieldSignatures);
        hash.AddArray("game.runner.run", evidence.GameRunner.RunMethodSignatures);
        AddType(hash, "game.activity", evidence.MainActivity.Type);
        hash.AddArray("game.activity.body", evidence.MainActivity.LifecycleBodies.Select(EncodeLifecycleBody));
        return hash.GetHash();
    }

    private static void AddAssembly(
        CanonicalHashBuilder hash,
        string prefix,
        ActivityBridgeAssemblyEvidence evidence)
    {
        hash.Add($"{prefix}.identity", evidence.Identity);
        hash.Add($"{prefix}.mvid", evidence.ModuleVersionId);
        hash.Add($"{prefix}.framework", evidence.TargetFramework ?? "<none>");
        hash.Add($"{prefix}.api.schema", evidence.PublicApiSurface.SchemaVersion);
        hash.Add($"{prefix}.api.key", evidence.PublicApiSurface.SurfaceKey);
        hash.Add($"{prefix}.api.types", evidence.PublicApiSurface.TypeCount.ToString(CultureInfo.InvariantCulture));
        hash.Add($"{prefix}.api.members", evidence.PublicApiSurface.MemberCount.ToString(CultureInfo.InvariantCulture));
    }

    private static void AddType(
        CanonicalHashBuilder hash,
        string prefix,
        ActivityBridgeTypeEvidence evidence)
    {
        hash.Add($"{prefix}.signature", evidence.Signature);
        hash.Add($"{prefix}.base", evidence.BaseType);
        hash.Add($"{prefix}.abstract", evidence.IsAbstract ? "true" : "false");
        hash.Add($"{prefix}.sealed", evidence.IsSealed ? "true" : "false");
        hash.AddArray($"{prefix}.ctor", evidence.ConstructorSignatures);
        hash.AddArray($"{prefix}.lifecycle", evidence.LifecycleMethodSignatures);
        hash.AddArray($"{prefix}.interop", evidence.InteropAttributes.Select(EncodeInteropAttribute));
    }

    private static string EncodeLifecycleBody(ActivityBridgeLifecycleBodyEvidence evidence)
    {
        var builder = new StringBuilder();
        AppendEncoded(builder, "method", evidence.MethodSignature);
        AppendEncoded(builder, "instructions", evidence.InstructionCount.ToString(CultureInfo.InvariantCulture));
        foreach (var call in evidence.Calls)
        {
            AppendEncoded(builder, "call", EncodeFields(
                ("ordinal", call.InstructionOrdinal.ToString(CultureInfo.InvariantCulture)),
                ("opcode", call.OpCode),
                ("method", call.CalledMethodSignature)));
        }

        foreach (var field in evidence.Fields)
        {
            AppendEncoded(builder, "field", EncodeFields(
                ("ordinal", field.InstructionOrdinal.ToString(CultureInfo.InvariantCulture)),
                ("opcode", field.OpCode),
                ("field", field.FieldSignature)));
        }

        return builder.ToString();
    }

    private static string EncodeInteropAttribute(InteropAttributeEvidence evidence)
    {
        var builder = new StringBuilder();
        AppendEncoded(builder, "assembly", evidence.AssemblyIdentity);
        AppendEncoded(builder, "owner", evidence.OwnerSignature);
        AppendEncoded(builder, "type", evidence.AttributeType);
        AppendEncoded(builder, "constructor", evidence.ConstructorSignature);
        foreach (var argument in evidence.ArgumentFingerprints)
        {
            AppendEncoded(builder, "argument", argument);
        }

        return builder.ToString();
    }

    private static string EncodeFields(params (string Name, string Value)[] fields)
    {
        var builder = new StringBuilder();
        foreach (var (name, value) in fields)
        {
            AppendEncoded(builder, name, value);
        }

        return builder.ToString();
    }

    private static void AppendEncoded(StringBuilder builder, string name, string value) =>
        builder.Append(Encoding.UTF8.GetByteCount(name)).Append(':').Append(name)
            .Append('=').Append(Encoding.UTF8.GetByteCount(value)).Append(':').Append(value).Append('\n');

    private static string HashText(string value) => HashBytes(Encoding.UTF8.GetBytes(value));

    private static string HashBytes(ReadOnlySpan<byte> value) =>
        Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();

    private static FileStream OpenRead(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);

    private static AssemblyDefinition ReadAssembly(Stream stream) =>
        AssemblyDefinition.ReadAssembly(stream, new ReaderParameters
        {
            ReadingMode = ReadingMode.Deferred,
            ReadSymbols = false,
            InMemory = false,
        });

    private static ActivityBridgeCompatibilityProbeResult InputFailure(
        string code,
        ActivityBridgeCompatibilityProbeOptions options,
        Exception exception,
        string prefix)
    {
        var monoGameName = SafeInputName(options.MonoGameAssemblyPath);
        var gameName = SafeInputName(options.GameAssemblyPath);
        return Failure(
            code,
            $"{prefix} ({exception.GetType().Name}; inputs: {monoGameName}, {gameName}).");
    }

    private static string SafeInputName(string path)
    {
        var name = Path.GetFileName(path);
        if (string.IsNullOrWhiteSpace(name) || name.Any(char.IsControl) ||
            name.Contains('/') || name.Contains('\\') || Encoding.UTF8.GetByteCount(name) > 255)
        {
            return $"redacted-sha256:{HashText(path)}";
        }

        return name;
    }

    private static ActivityBridgeCompatibilityProbeResult Failure(string code, string message) =>
        new(
            ActivityBridgeProbeStatus.Failed,
            null,
            null,
            ImmutableArray.Create(new ActivityBridgeProbeDiagnostic(
                code,
                ActivityBridgeProbeDiagnosticSeverity.Error,
                message)));

    private sealed class ProbeState
    {
        private readonly ActivityBridgeProbeLimits limits;
        private int typeCount;
        private int memberCount;
        private int instructionCount;
        private int evidenceCount;

        public ProbeState(ActivityBridgeProbeLimits limits, CancellationToken cancellationToken)
        {
            this.limits = limits;
            CancellationToken = cancellationToken;
        }

        public CancellationToken CancellationToken { get; }

        public void CountType()
        {
            typeCount = checked(typeCount + 1);
            if (typeCount > limits.MaxTypes)
            {
                throw new ActivityBridgeProbeLimitExceededException();
            }
        }

        public void CountMembers(int count)
        {
            memberCount = checked(memberCount + count);
            if (memberCount > limits.MaxMembers)
            {
                throw new ActivityBridgeProbeLimitExceededException();
            }
        }

        public void CountInstructions(int count)
        {
            instructionCount = checked(instructionCount + count);
            if (instructionCount > limits.MaxInstructions)
            {
                throw new ActivityBridgeProbeLimitExceededException();
            }
        }

        public void AddEvidence()
        {
            evidenceCount = checked(evidenceCount + 1);
            if (evidenceCount > limits.MaxEvidenceItems)
            {
                throw new ActivityBridgeProbeLimitExceededException();
            }
        }
    }

    private sealed class ActivityBridgeProbeLimitExceededException : Exception;

    private sealed class ActivityBridgeTypeException : Exception
    {
        public ActivityBridgeTypeException(string code, string message)
            : base(message)
        {
            Code = code;
        }

        public string Code { get; }
    }

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
