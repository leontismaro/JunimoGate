using System.Collections.Immutable;
using JunimoGate.Core;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace JunimoGate.Rewriter;

internal enum GameHostBridgeActionKind
{
    Activity,
    Method,
    Field,
}

internal sealed record GameHostBridgeActionPlan(
    GameHostBridgeActionKind Kind,
    string SourceName,
    string SourceReturnType,
    ImmutableArray<string> SourceParameters,
    string BridgeName);

internal sealed record GameHostBridgeMethodPlan(
    string Id,
    string Type,
    string Name,
    string ReturnType,
    ImmutableArray<string> Parameters,
    ImmutableArray<GameHostBridgeActionPlan> Actions,
    bool RewriteActivityLocal,
    string TargetMemberSignature);

internal sealed record GameHostBridgeRequiredFieldPlan(
    string Type,
    string Name,
    string FieldType,
    bool IsStatic,
    string MemberSignature);

public sealed record GameHostBridgeRuleContract(
    string RuleId,
    string InputRelativePath,
    string TargetMemberSignature,
    int ExpectedMatchCount,
    IReadOnlyList<string> Replacements);

/// <summary>Version-independent local rules for adapting Stardew's Android activity calls.</summary>
public static class GameHostBridgeRecipe
{
    public const string InputRelativePath = "assemblies/StardewValley.dll";
    public const string FamilyId = GameCompatibilityIds.StardewAndroidMainActivityBridgeV1;

    public static RewriteRecipeIdentity Identity { get; } =
        new("stardew-android-mainactivity-bridge", "2");

    internal static ImmutableArray<GameHostBridgeMethodPlan> Plans { get; } =
    [
        Plan("directory-copy", "StardewValley.MainActivity", "DirectoryCopy", "System.Void", ["AndroidX.DocumentFile.Provider.DocumentFile", "System.String"], [Activity()], false),
        Plan("emergency-backup-permission", "StardewValley.Game1", "emergencyBackup", "System.Void", [], [Method("get_HasPermissions", "System.Boolean", [], "HasPermissions")], false),
        Plan("rumble-activity", "StardewValley.Rumble", "SetVibration", "System.Boolean", ["Microsoft.Xna.Framework.PlayerIndex", "System.Single", "System.Single"], [Activity()], false),
        Plan("save-load-permission", "StardewValley.SaveGame", "Load", "System.Void", ["System.String", "System.Boolean", "System.Boolean"], [Method("PromptForPermissionsIfNecessary", "System.Void", ["System.Action"], "PromptForPermissionsIfNecessary")], false),
        Plan("save-load-permission-log", "StardewValley.SaveGame", "LoadAfterPermissionCheck", "System.Void", [], [Method("LogPermissions", "System.Void", [], "LogPermissions")], false),
        Plan("save-disk-dialog", "StardewValley.SaveGame", "checkForDiskFull", "System.Boolean", [], [Method("ShowDiskFullDialogue", "System.Void", [], "ShowDiskFullDialogue")], false),
        Plan("credits-browser-activity", "StardewValley.Menus.LinkCreditsBlock", "LaunchBrowser", "System.Void", ["System.String"], [Activity()], false),
        Plan("load-menu-permission", "StardewValley.Menus.LoadGameMenu", ".ctor", "System.Void", ["System.String"], [Method("PromptForPermissionsIfNecessary", "System.Void", ["System.Action"], "PromptForPermissionsIfNecessary")], false),
        Plan("startup-preferences-build", "StardewValley.Menus.OptionsPage", "SaveStartupPreferences", "System.Void", [], [Method("GetBuild", "System.Int32", [], "GetBuild")], false),
        Plan("title-menu-migration", "StardewValley.Menus.TitleMenu", ".ctor", "System.Void", [], [Method("CheckStorageMigration", "System.Boolean", [], "CheckStorageMigration"), Field("IsDoingStorageMigration", "System.Boolean", "IsDoingStorageMigration")], false),
        Plan("title-menu-permission", "StardewValley.Menus.TitleMenu", "releaseLeftClick", "System.Void", ["System.Int32", "System.Int32"], [Method("get_HasPermissions", "System.Boolean", [], "HasPermissions")], false, isInstance: true),
        Plan("title-menu-migration-state", "StardewValley.Menus.TitleMenu", "update", "System.Void", ["Microsoft.Xna.Framework.GameTime"], [Field("IsDoingStorageMigration", "System.Boolean", "IsDoingStorageMigration")], false, isInstance: true),
        Plan("mobile-display-activity", "StardewValley.Mobile.MobileDisplay", "SetupDisplaySettings", "System.Void", [], [Activity()], true),
    ];

    internal static ImmutableArray<GameHostBridgeRequiredFieldPlan> RequiredFields { get; } =
    [
        RequiredField("StardewValley.Game1", "xEdge", "System.Int32", isStatic: true),
        RequiredField("StardewValley.Game1", "toolbarPaddingX", "System.Int32", isStatic: true),
    ];

    public static ImmutableArray<GameHostBridgeRuleContract> Rules =>
        Plans.Select(static plan => new GameHostBridgeRuleContract(
            plan.Id,
            InputRelativePath,
            plan.TargetMemberSignature,
            plan.Actions.Length,
            plan.Actions.Select(FormatReplacement).ToArray())).ToImmutableArray();

    private static GameHostBridgeMethodPlan Plan(
        string id,
        string type,
        string name,
        string returnType,
        ImmutableArray<string> parameters,
        ImmutableArray<GameHostBridgeActionPlan> actions,
        bool rewriteActivityLocal,
        bool isInstance = false)
    {
        var target = $"{(isInstance || name == ".ctor" ? "instance" : "static")};{returnType} {type}::{name}({string.Join(',', parameters)})";
        return new(id, type, name, returnType, parameters, actions, rewriteActivityLocal, target);
    }

    private static GameHostBridgeActionPlan Activity() =>
        new(GameHostBridgeActionKind.Activity, "instance", "StardewValley.MainActivity", [], "GetActivity");

    private static GameHostBridgeActionPlan Method(
        string sourceName,
        string returnType,
        ImmutableArray<string> parameters,
        string bridgeName) =>
        new(GameHostBridgeActionKind.Method, sourceName, returnType, parameters, bridgeName);

    private static GameHostBridgeActionPlan Field(string sourceName, string fieldType, string bridgeName) =>
        new(GameHostBridgeActionKind.Field, sourceName, fieldType, [], bridgeName);

    private static GameHostBridgeRequiredFieldPlan RequiredField(
        string type,
        string name,
        string fieldType,
        bool isStatic) =>
        new(
            type,
            name,
            fieldType,
            isStatic,
            $"{(isStatic ? "static" : "instance")};{fieldType} {type}::{name}");

    internal static string FormatReplacement(GameHostBridgeActionPlan action) =>
        action.Kind == GameHostBridgeActionKind.Activity
            ? "StardewValley.MainActivity::instance -> JunimoGate.GameHost.GameHostBridge::GetActivity"
            : $"StardewValley.MainActivity::{action.SourceName} -> JunimoGate.GameHost.GameHostBridge::{action.BridgeName}";
}

internal static class GameHostBridgeRecipeEngine
{
    private const string MainActivityType = "StardewValley.MainActivity";
    private const string InstanceFieldName = "instance";
    private const string HostAssemblyName = "JunimoGate.GameHost";
    private const string HostBridgeType = "JunimoGate.GameHost.GameHostBridge";

    private static readonly HashSet<string> ActivityCompatibleConsumers = new(StringComparer.Ordinal)
    {
        "Android.App.Activity",
        "Android.Content.Context",
        "Android.Content.ContextWrapper",
        "Java.Lang.Object",
        "System.Object",
    };

    internal static ImmutableArray<AppliedRewriteMutationEvidence> Apply(AssemblyDefinition assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        ValidateRequiredMembers(assembly.MainModule);
        var module = assembly.MainModule;
        var hostReference = GetOrAddHostReference(module);
        var bridgeType = new TypeReference("JunimoGate.GameHost", "GameHostBridge", module, hostReference, false);
        var monoAndroid = module.AssemblyReferences.Single(reference => reference.Name == "Mono.Android");
        var activityType = new TypeReference("Android.App", "Activity", module, monoAndroid, false);

        var evidence = ImmutableArray.CreateBuilder<AppliedRewriteMutationEvidence>(GameHostBridgeRecipe.Plans.Length);
        foreach (var plan in GameHostBridgeRecipe.Plans)
        {
            var method = FindMethod(module, plan);
            var observed = 0;
            foreach (var action in plan.Actions)
                observed += Apply(method, action, bridgeType, activityType, plan.RewriteActivityLocal);

            if (observed != plan.Actions.Length)
                throw new InvalidDataException($"Bridge rule '{plan.Id}' did not match every local action exactly once.");

            ValidateStack(method);
            evidence.Add(new AppliedRewriteMutationEvidence(
                plan.Id,
                GameHostBridgeRecipe.InputRelativePath,
                plan.TargetMemberSignature,
                plan.Actions.Length,
                observed,
                plan.Actions.Select(GameHostBridgeRecipe.FormatReplacement).ToArray(),
                PostconditionPassed: false));
        }

        return evidence.MoveToImmutable();
    }

    internal static ImmutableArray<AppliedRewriteMutationEvidence> ValidatePostconditions(AssemblyDefinition assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        ValidateRequiredMembers(assembly.MainModule);
        var module = assembly.MainModule;
        var hostReferences = module.AssemblyReferences.Where(static reference => reference.Name == HostAssemblyName).ToArray();
        if (hostReferences.Length != 1 || hostReferences[0].Version != new Version(1, 0, 0, 0))
            throw new InvalidDataException("The rewritten assembly does not contain one compatible GameHost reference.");

        var evidence = ImmutableArray.CreateBuilder<AppliedRewriteMutationEvidence>(GameHostBridgeRecipe.Plans.Length);
        foreach (var plan in GameHostBridgeRecipe.Plans)
        {
            var method = FindMethod(module, plan);
            foreach (var action in plan.Actions)
            {
                if (CountSourceUses(method, action) != 0 || CountBridgeUses(method, action) != 1)
                    throw new InvalidDataException($"Bridge postcondition failed for local rule '{plan.Id}'.");
            }

            ValidateStack(method);
            evidence.Add(new AppliedRewriteMutationEvidence(
                plan.Id,
                GameHostBridgeRecipe.InputRelativePath,
                plan.TargetMemberSignature,
                plan.Actions.Length,
                plan.Actions.Length,
                plan.Actions.Select(GameHostBridgeRecipe.FormatReplacement).ToArray(),
                PostconditionPassed: true));
        }

        return evidence.MoveToImmutable();
    }

    internal static void ValidateRequiredMembers(ModuleDefinition module)
    {
        ArgumentNullException.ThrowIfNull(module);
        foreach (var required in GameHostBridgeRecipe.RequiredFields)
        {
            var types = AllTypes(module)
                .Where(type => Normalize(type.FullName) == required.Type)
                .ToArray();
            if (types.Length != 1)
            {
                throw new InvalidDataException(
                    $"Required GameHost field type guard failed for '{required.MemberSignature}'.");
            }

            var fields = types[0].Fields
                .Where(field => field.Name == required.Name &&
                    Normalize(field.FieldType.FullName) == required.FieldType &&
                    field.IsStatic == required.IsStatic &&
                    field.IsPublic)
                .ToArray();
            if (fields.Length != 1)
            {
                throw new InvalidDataException(
                    $"Required GameHost field guard failed for '{required.MemberSignature}'.");
            }
        }
    }

    private static MethodDefinition FindMethod(ModuleDefinition module, GameHostBridgeMethodPlan plan)
    {
        var types = AllTypes(module).Where(type => Normalize(type.FullName) == plan.Type).ToArray();
        if (types.Length != 1)
            throw new InvalidDataException($"Bridge target type guard failed for rule '{plan.Id}'.");

        var candidates = types[0].Methods.Where(method =>
            method.Name == plan.Name &&
            Normalize(method.ReturnType.FullName) == plan.ReturnType &&
            method.Parameters.Select(parameter => Normalize(parameter.ParameterType.FullName))
                .SequenceEqual(plan.Parameters, StringComparer.Ordinal)).ToArray();
        if (candidates.Length != 1 || !candidates[0].HasBody ||
            !CanonicalMethod(candidates[0]).Equals(plan.TargetMemberSignature, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Bridge target method guard failed for rule '{plan.Id}'.");
        }

        return candidates[0];
    }

    private static int Apply(
        MethodDefinition method,
        GameHostBridgeActionPlan action,
        TypeReference bridgeType,
        TypeReference activityType,
        bool rewriteActivityLocal)
    {
        var instructions = method.Body.Instructions;
        if (action.Kind == GameHostBridgeActionKind.Activity)
        {
            var loads = instructions.Where(IsInstanceLoad).ToArray();
            if (loads.Length != 1)
                throw new InvalidDataException($"Activity source guard failed in '{CanonicalMethod(method)}'.");

            ValidateActivityConsumers(method, loads[0], activityType, rewriteActivityLocal);
            loads[0].OpCode = OpCodes.Call;
            loads[0].Operand = BridgeMethod(bridgeType, action.BridgeName, activityType, []);
            return 1;
        }

        var sources = instructions.Where(instruction => MatchesSource(instruction, action)).ToArray();
        if (sources.Length != 1)
            throw new InvalidDataException($"Source member guard failed for '{action.SourceName}'.");

        var source = sources[0];
        var receiver = FindReceiverLoad(method, source, action.SourceParameters.Length);
        receiver.OpCode = OpCodes.Nop;
        receiver.Operand = null;
        source.OpCode = OpCodes.Call;
        source.Operand = BridgeMethod(
            bridgeType,
            action.BridgeName,
            action.Kind == GameHostBridgeActionKind.Field
                ? ((FieldReference)source.Operand).FieldType
                : ((MethodReference)source.Operand).ReturnType,
            action.Kind == GameHostBridgeActionKind.Field
                ? []
                : ((MethodReference)source.Operand).Parameters.Select(static parameter => parameter.ParameterType));
        return 1;
    }

    private static bool MatchesSource(Instruction instruction, GameHostBridgeActionPlan action)
    {
        if (action.Kind == GameHostBridgeActionKind.Method)
        {
            return instruction.OpCode.Code is Code.Call or Code.Callvirt &&
                instruction.Operand is MethodReference method &&
                Normalize(method.DeclaringType.FullName) == MainActivityType &&
                method.Name == action.SourceName &&
                Normalize(method.ReturnType.FullName) == action.SourceReturnType &&
                method.Parameters.Select(parameter => Normalize(parameter.ParameterType.FullName))
                    .SequenceEqual(action.SourceParameters, StringComparer.Ordinal);
        }

        return instruction.OpCode.Code == Code.Ldfld &&
            instruction.Operand is FieldReference field &&
            Normalize(field.DeclaringType.FullName) == MainActivityType &&
            field.Name == action.SourceName &&
            Normalize(field.FieldType.FullName) == action.SourceReturnType;
    }

    private static Instruction FindReceiverLoad(MethodDefinition method, Instruction source, int explicitArgumentCount)
    {
        var incoming = TraceStackOrigins(method);
        if (!incoming.TryGetValue(source, out var stack) || stack.Count < explicitArgumentCount + 1)
            throw new InvalidDataException($"The MainActivity receiver for '{CanonicalMethod(method)}' is missing or ambiguous.");
        var receiverOrigins = stack[stack.Count - explicitArgumentCount - 1];
        if (receiverOrigins.Count != 1 || !IsInstanceLoad(receiverOrigins.Single()))
            throw new InvalidDataException($"The MainActivity receiver for '{CanonicalMethod(method)}' is missing or ambiguous.");
        return receiverOrigins.Single();
    }

    private static void ValidateActivityConsumers(
        MethodDefinition method,
        Instruction load,
        TypeReference activityType,
        bool rewriteActivityLocal)
    {
        var instructions = method.Body.Instructions;
        var next = NextNonNop(instructions, instructions.IndexOf(load) + 1);
        if (next is not null && TryGetStoredVariable(method.Body, next, out var variable))
        {
            if (!rewriteActivityLocal || Normalize(variable.VariableType.FullName) != MainActivityType)
                throw new InvalidDataException($"Activity local guard failed in '{CanonicalMethod(method)}'.");
            variable.VariableType = activityType;
            var loads = instructions.Where(instruction => LoadsVariable(instruction, variable)).ToArray();
            if (loads.Length == 0 || loads.Any(localLoad => !HasCompatibleActivityConsumer(method, localLoad)))
                throw new InvalidDataException($"Activity local consumer guard failed in '{CanonicalMethod(method)}'.");
            return;
        }

        if (rewriteActivityLocal || !HasCompatibleActivityConsumer(method, load))
            throw new InvalidDataException($"Activity consumer guard failed in '{CanonicalMethod(method)}'.");
    }

    private static bool HasCompatibleActivityConsumer(MethodDefinition method, Instruction producer)
    {
        var instructions = method.Body.Instructions;
        var depth = 1;
        for (var index = instructions.IndexOf(producer) + 1; index < instructions.Count; index++)
        {
            var instruction = instructions[index];
            if (IsControlFlowBoundary(instruction))
                return false;
            var (pop, push) = StackEffect(method, instruction);
            if (pop >= depth)
            {
                if (instruction.Operand is not MethodReference methodReference)
                    return false;
                var inputs = new List<TypeReference>();
                if (methodReference.HasThis)
                    inputs.Add(methodReference.DeclaringType);
                inputs.AddRange(methodReference.Parameters.Select(static parameter => parameter.ParameterType));
                var targetInput = inputs.Count - depth;
                return targetInput >= 0 && targetInput < inputs.Count &&
                    ActivityCompatibleConsumers.Contains(Normalize(inputs[targetInput].FullName));
            }
            depth = depth - pop + push;
        }
        return false;
    }

    private static int CountSourceUses(MethodDefinition method, GameHostBridgeActionPlan action) =>
        action.Kind == GameHostBridgeActionKind.Activity
            ? method.Body.Instructions.Count(IsInstanceLoad)
            : method.Body.Instructions.Count(instruction => MatchesSource(instruction, action));

    private static int CountBridgeUses(MethodDefinition method, GameHostBridgeActionPlan action) =>
        method.Body.Instructions.Count(instruction =>
            instruction.OpCode.Code == Code.Call &&
            instruction.Operand is MethodReference called &&
            Normalize(called.DeclaringType.FullName) == HostBridgeType &&
            called.Name == action.BridgeName &&
            Normalize(called.ReturnType.FullName) == (action.Kind == GameHostBridgeActionKind.Activity
                ? "Android.App.Activity"
                : action.SourceReturnType) &&
            called.Parameters.Select(parameter => Normalize(parameter.ParameterType.FullName))
                .SequenceEqual(action.SourceParameters, StringComparer.Ordinal));

    private static AssemblyNameReference GetOrAddHostReference(ModuleDefinition module)
    {
        var existing = module.AssemblyReferences.Where(static reference => reference.Name == HostAssemblyName).ToArray();
        if (existing.Length > 1 || (existing.Length == 1 && existing[0].Version != new Version(1, 0, 0, 0)))
            throw new InvalidDataException("The target contains a conflicting GameHost assembly reference.");
        if (existing.Length == 1)
            return existing[0];
        var reference = new AssemblyNameReference(HostAssemblyName, new Version(1, 0, 0, 0));
        module.AssemblyReferences.Add(reference);
        return reference;
    }

    private static MethodReference BridgeMethod(
        TypeReference bridgeType,
        string name,
        TypeReference returnType,
        IEnumerable<TypeReference> parameters)
    {
        var method = new MethodReference(name, returnType, bridgeType)
        {
            HasThis = false,
            ExplicitThis = false,
            CallingConvention = MethodCallingConvention.Default,
        };
        foreach (var parameter in parameters)
            method.Parameters.Add(new ParameterDefinition(parameter));
        return method;
    }

    private static void ValidateStack(MethodDefinition method)
    {
        var instructions = method.Body.Instructions;
        if (instructions.Count == 0)
            throw new InvalidDataException($"Method '{CanonicalMethod(method)}' has no body.");
        var incoming = new Dictionary<Instruction, int>();
        var pending = new Queue<(Instruction Instruction, int Depth)>();
        pending.Enqueue((instructions[0], 0));
        foreach (var handler in method.Body.ExceptionHandlers)
        {
            if (handler.FilterStart is not null)
                pending.Enqueue((handler.FilterStart, 1));
            if (handler.HandlerStart is not null)
                pending.Enqueue((handler.HandlerStart, handler.HandlerType is ExceptionHandlerType.Catch or ExceptionHandlerType.Filter ? 1 : 0));
        }

        while (pending.TryDequeue(out var state))
        {
            if (incoming.TryGetValue(state.Instruction, out var known))
            {
                if (known != state.Depth)
                    throw new InvalidDataException($"Stack merge mismatch in '{CanonicalMethod(method)}'.");
                continue;
            }
            incoming.Add(state.Instruction, state.Depth);
            var (pop, push) = StackEffect(method, state.Instruction);
            if (pop > state.Depth)
                throw new InvalidDataException(
                    $"Stack underflow at '{state.Instruction}' with depth {state.Depth} and pop {pop} in '{CanonicalMethod(method)}'.");
            var outgoing = state.Instruction.OpCode.Code is Code.Leave or Code.Leave_S
                ? 0
                : state.Depth - pop + push;
            foreach (var successor in Successors(instructions, state.Instruction))
                pending.Enqueue((successor, outgoing));
        }
    }

    private static IReadOnlyDictionary<Instruction, List<HashSet<Instruction>>> TraceStackOrigins(
        MethodDefinition method)
    {
        var instructions = method.Body.Instructions;
        var incoming = new Dictionary<Instruction, List<HashSet<Instruction>>>();
        var pending = new Queue<Instruction>();
        Merge(instructions[0], []);
        foreach (var handler in method.Body.ExceptionHandlers)
        {
            if (handler.FilterStart is not null)
                Merge(handler.FilterStart, [new HashSet<Instruction>()]);
            if (handler.HandlerStart is not null)
            {
                Merge(
                    handler.HandlerStart,
                    handler.HandlerType is ExceptionHandlerType.Catch or ExceptionHandlerType.Filter
                        ? new List<HashSet<Instruction>> { new() }
                        : new List<HashSet<Instruction>>());
            }
        }

        while (pending.TryDequeue(out var instruction))
        {
            var stack = Clone(incoming[instruction]);
            if (instruction.OpCode.Code == Code.Dup)
            {
                if (stack.Count == 0)
                    throw new InvalidDataException($"Stack underflow in '{CanonicalMethod(method)}'.");
                stack.Add(new HashSet<Instruction>(stack[^1]));
            }
            else
            {
                var (pop, push) = StackEffect(method, instruction);
                if (pop > stack.Count)
                    throw new InvalidDataException($"Stack underflow in '{CanonicalMethod(method)}'.");
                if (pop > 0)
                    stack.RemoveRange(stack.Count - pop, pop);
                for (var index = 0; index < push; index++)
                    stack.Add(new HashSet<Instruction> { instruction });
            }
            if (instruction.OpCode.Code is Code.Leave or Code.Leave_S)
                stack.Clear();
            foreach (var successor in Successors(instructions, instruction))
                Merge(successor, stack);
        }

        return incoming;

        void Merge(Instruction instruction, List<HashSet<Instruction>> stack)
        {
            if (!incoming.TryGetValue(instruction, out var existing))
            {
                incoming.Add(instruction, Clone(stack));
                pending.Enqueue(instruction);
                return;
            }
            if (existing.Count != stack.Count)
                throw new InvalidDataException($"Stack merge mismatch in '{CanonicalMethod(method)}'.");
            var changed = false;
            for (var index = 0; index < existing.Count; index++)
            {
                var before = existing[index].Count;
                existing[index].UnionWith(stack[index]);
                changed |= existing[index].Count != before;
            }
            if (changed)
                pending.Enqueue(instruction);
        }

        static List<HashSet<Instruction>> Clone(IEnumerable<HashSet<Instruction>> stack) =>
            stack.Select(static origins => new HashSet<Instruction>(origins)).ToList();
    }

    private static IEnumerable<Instruction> Successors(Mono.Collections.Generic.Collection<Instruction> instructions, Instruction instruction)
    {
        var code = instruction.OpCode.Code;
        if (instruction.Operand is Instruction target)
            yield return target;
        else if (instruction.Operand is Instruction[] targets)
        {
            foreach (var item in targets)
                yield return item;
        }
        if (code is Code.Br or Code.Br_S or Code.Leave or Code.Leave_S or Code.Ret or Code.Throw or Code.Rethrow or
            Code.Endfinally or Code.Endfilter or Code.Jmp)
            yield break;
        var index = instructions.IndexOf(instruction);
        if (index + 1 < instructions.Count)
            yield return instructions[index + 1];
    }

    private static (int Pop, int Push) StackEffect(MethodDefinition owner, Instruction instruction)
    {
        var pop = instruction.OpCode.StackBehaviourPop switch
        {
            StackBehaviour.Pop0 => 0,
            StackBehaviour.Pop1 or StackBehaviour.Popi or StackBehaviour.Popref => 1,
            StackBehaviour.Pop1_pop1 or StackBehaviour.Popi_pop1 or StackBehaviour.Popi_popi or
                StackBehaviour.Popi_popi8 or StackBehaviour.Popi_popr4 or StackBehaviour.Popi_popr8 or
                StackBehaviour.Popref_pop1 or StackBehaviour.Popref_popi => 2,
            StackBehaviour.Popi_popi_popi or StackBehaviour.Popref_popi_popi or
                StackBehaviour.Popref_popi_popi8 or StackBehaviour.Popref_popi_popr4 or
                StackBehaviour.Popref_popi_popr8 or StackBehaviour.Popref_popi_popref => 3,
            // leave clears the evaluation stack; the control-flow pass sets its outgoing depth to zero.
            StackBehaviour.PopAll => 0,
            StackBehaviour.Varpop => VariablePop(owner, instruction),
            _ => throw new InvalidDataException($"Unsupported stack pop behavior in '{CanonicalMethod(owner)}'."),
        };
        var push = instruction.OpCode.StackBehaviourPush switch
        {
            StackBehaviour.Push0 => 0,
            StackBehaviour.Push1 or StackBehaviour.Pushi or StackBehaviour.Pushi8 or
                StackBehaviour.Pushr4 or StackBehaviour.Pushr8 or StackBehaviour.Pushref => 1,
            StackBehaviour.Push1_push1 => 2,
            StackBehaviour.Varpush => VariablePush(instruction),
            _ => throw new InvalidDataException($"Unsupported stack push behavior in '{CanonicalMethod(owner)}'."),
        };
        return (pop, push);
    }

    private static int VariablePop(MethodDefinition owner, Instruction instruction)
    {
        if (instruction.OpCode.Code == Code.Ret)
            return Normalize(owner.ReturnType.FullName) == "System.Void" ? 0 : 1;
        if (instruction.Operand is MethodReference method)
            return method.Parameters.Count + (instruction.OpCode.Code == Code.Newobj ? 0 : method.HasThis ? 1 : 0) +
                (instruction.OpCode.Code == Code.Calli ? 1 : 0);
        throw new InvalidDataException($"Unsupported variable stack pop in '{CanonicalMethod(owner)}'.");
    }

    private static int VariablePush(Instruction instruction) =>
        instruction.Operand is MethodReference method &&
        (instruction.OpCode.Code == Code.Newobj || Normalize(method.ReturnType.FullName) != "System.Void") ? 1 : 0;

    private static bool IsControlFlowBoundary(Instruction instruction) =>
        instruction.OpCode.FlowControl is FlowControl.Branch or FlowControl.Cond_Branch or FlowControl.Return or FlowControl.Throw;

    private static Instruction? NextNonNop(Mono.Collections.Generic.Collection<Instruction> instructions, int start)
    {
        for (var index = start; index < instructions.Count; index++)
            if (instructions[index].OpCode.Code != Code.Nop) return instructions[index];
        return null;
    }

    private static bool TryGetStoredVariable(MethodBody body, Instruction instruction, out VariableDefinition variable)
    {
        variable = null!;
        var index = instruction.OpCode.Code switch
        {
            Code.Stloc_0 => 0,
            Code.Stloc_1 => 1,
            Code.Stloc_2 => 2,
            Code.Stloc_3 => 3,
            Code.Stloc or Code.Stloc_S when instruction.Operand is VariableDefinition stored => stored.Index,
            _ => -1,
        };
        if (index < 0 || index >= body.Variables.Count)
            return false;
        variable = body.Variables[index];
        return true;
    }

    private static bool LoadsVariable(Instruction instruction, VariableDefinition variable) =>
        instruction.OpCode.Code switch
        {
            Code.Ldloc_0 => variable.Index == 0,
            Code.Ldloc_1 => variable.Index == 1,
            Code.Ldloc_2 => variable.Index == 2,
            Code.Ldloc_3 => variable.Index == 3,
            Code.Ldloc or Code.Ldloc_S => ReferenceEquals(instruction.Operand, variable),
            _ => false,
        };

    private static bool IsInstanceLoad(Instruction instruction) =>
        instruction.OpCode.Code == Code.Ldsfld &&
        instruction.Operand is FieldReference field &&
        Normalize(field.DeclaringType.FullName) == MainActivityType &&
        field.Name == InstanceFieldName &&
        Normalize(field.FieldType.FullName) == MainActivityType;

    private static IEnumerable<TypeDefinition> AllTypes(ModuleDefinition module) =>
        module.Types.SelectMany(Flatten);

    private static IEnumerable<TypeDefinition> Flatten(TypeDefinition type)
    {
        yield return type;
        foreach (var nested in type.NestedTypes.SelectMany(Flatten))
            yield return nested;
    }

    internal static string CanonicalMethod(MethodReference method) =>
        $"{(method.HasThis ? "instance" : "static")};{Normalize(method.ReturnType.FullName)} " +
        $"{Normalize(method.DeclaringType.FullName)}::{method.Name}(" +
        $"{string.Join(',', method.Parameters.Select(parameter => Normalize(parameter.ParameterType.FullName)))})";

    private static string Normalize(string value) => value.Replace('/', '+');
}
