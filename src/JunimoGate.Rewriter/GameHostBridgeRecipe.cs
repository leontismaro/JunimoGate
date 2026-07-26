using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
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
    string BridgeName);

internal sealed record GameHostBridgeMethodPlan(
    string Id,
    string Type,
    string Name,
    string ReturnType,
    ImmutableArray<string> Parameters,
    ImmutableArray<GameHostBridgeActionPlan> Actions,
    bool RewriteFirstLocalToActivity,
    string TargetMemberSignature,
    int ExpectedMatchCount,
    string PreconditionSha256,
    string PostconditionSha256);

/// <summary>Exact tested Play 1.6.15.3 bridge recipe. It never changes the MainActivity.instance field type.</summary>
public static class GameHostBridgeRecipe
{
    public const string InputRelativePath = "assemblies/StardewValley.dll";

    public static RewriteRecipeIdentity Identity { get; } = new("play-1.6.15.3-gamehost-bridge", "1");

    internal static ImmutableArray<GameHostBridgeMethodPlan> Plans { get; } =
    [
        Plan("directory-copy", "StardewValley.MainActivity", "DirectoryCopy", "System.Void", ["AndroidX.DocumentFile.Provider.DocumentFile", "System.String"], [Activity()], false, "static;System.Void StardewValley.MainActivity::DirectoryCopy(AndroidX.DocumentFile.Provider.DocumentFile,System.String)", 1, "e10ec7a404cb4bde583080f7cf02b414f43bf472b129106d108dae4033e6a07a", "b946c1a39e25f89c970e248a3bc96dff62347f1555320a731f55fbd525b2363b"),
        Plan("emergency-backup-permission", "StardewValley.Game1", "emergencyBackup", "System.Void", [], [Method("get_HasPermissions", "HasPermissions")], false, "static;System.Void StardewValley.Game1::emergencyBackup()", 1, "7434dc56762cccc7807cea0639e87bb8f8a6b9fd383aa8a21ca5d4c821682d78", "36f5e9f9e3bc1abc842fd7df0bf84adaf90a93cb22394d119d2e8274173e93e4"),
        Plan("rumble-activity", "StardewValley.Rumble", "SetVibration", "System.Boolean", ["Microsoft.Xna.Framework.PlayerIndex", "System.Single", "System.Single"], [Activity()], false, "static;System.Boolean StardewValley.Rumble::SetVibration(Microsoft.Xna.Framework.PlayerIndex,System.Single,System.Single)", 1, "67df4b8bf46073070ccb6f3badfa016f41c6cd7710837fc3ac70359d48b9f090", "544a4bd3083572b86dfb7b053e0546d203fcfa356e1c2c5f2b3a2ee8548ed893"),
        Plan("save-load-permission", "StardewValley.SaveGame", "Load", "System.Void", ["System.String", "System.Boolean", "System.Boolean"], [Method("PromptForPermissionsIfNecessary", "PromptForPermissionsIfNecessary")], false, "static;System.Void StardewValley.SaveGame::Load(System.String,System.Boolean,System.Boolean)", 1, "ce7e05b3263d7a2ed47b362ec77241120ed72c808e9ef15f064420b8c8a38305", "026bde7b420a3d4562ef6c2c5e0d3d8e22fc2c585348c3a5a3d8572ae5d81130"),
        Plan("save-load-permission-log", "StardewValley.SaveGame", "LoadAfterPermissionCheck", "System.Void", [], [Method("LogPermissions", "LogPermissions")], false, "static;System.Void StardewValley.SaveGame::LoadAfterPermissionCheck()", 1, "93d06b4c16ec398327717b4ac4dbbd287101598e4a40b17ea6d0e27db25405e0", "e744053c08e40697be50ac0cf2a1af62951a38bfaa781ff65aa148dab813cc0b"),
        Plan("save-disk-dialog", "StardewValley.SaveGame", "checkForDiskFull", "System.Boolean", [], [Method("ShowDiskFullDialogue", "ShowDiskFullDialogue")], false, "static;System.Boolean StardewValley.SaveGame::checkForDiskFull()", 1, "18ae5a74cf806ce20a1a4dcefd17206b325e73c96c4d5b39373ae2400c8a2da0", "4e05a415a7241b129699f0396369d9f226952600980973965f91731f2efbaeb6"),
        Plan("credits-browser-activity", "StardewValley.Menus.LinkCreditsBlock", "LaunchBrowser", "System.Void", ["System.String"], [Activity()], false, "static;System.Void StardewValley.Menus.LinkCreditsBlock::LaunchBrowser(System.String)", 1, "dffd03785cd088fcef16ee2dfd6db47c792b80b6a30104437d27f8836b0942bf", "800c842fe25c7014bdb87db94995d4fbc73bcfef866485c3db28a335da85724b"),
        Plan("load-menu-permission", "StardewValley.Menus.LoadGameMenu", ".ctor", "System.Void", ["System.String"], [Method("PromptForPermissionsIfNecessary", "PromptForPermissionsIfNecessary")], false, "instance;System.Void StardewValley.Menus.LoadGameMenu::.ctor(System.String)", 1, "c0b347a8bdab783914ee6d9251665629ae5213fae04685987aa6ecd3ad40f778", "a9b8e85083edba5923427f8e1920dddd60d2c2d92097e4273216d6578ef7a3dc"),
        Plan("startup-preferences-build", "StardewValley.Menus.OptionsPage", "SaveStartupPreferences", "System.Void", [], [Method("GetBuild", "GetBuild")], false, "static;System.Void StardewValley.Menus.OptionsPage::SaveStartupPreferences()", 1, "b369acf6656521ed91b12ae1550ab60fae51706f293ee84b86f55d42ab7d5728", "19d7e00ae0750086f63b367f568e1e9b21f8aa320c7aafc227c556b0e70dd12e"),
        Plan("title-menu-migration", "StardewValley.Menus.TitleMenu", ".ctor", "System.Void", [], [Method("CheckStorageMigration", "CheckStorageMigration"), Field("IsDoingStorageMigration", "IsDoingStorageMigration")], false, "instance;System.Void StardewValley.Menus.TitleMenu::.ctor()", 2, "962997250f40b12e461469dbccec393e5c3a9e3d69d5bfe30232dd9018353470", "926dfc7f087d202edc2b56ea6ea93053d801c09d022d6a89ca2b9eba14e7f4a3"),
        Plan("title-menu-permission", "StardewValley.Menus.TitleMenu", "releaseLeftClick", "System.Void", ["System.Int32", "System.Int32"], [Method("get_HasPermissions", "HasPermissions")], false, "instance;System.Void StardewValley.Menus.TitleMenu::releaseLeftClick(System.Int32,System.Int32)", 1, "7aa074f6bcd82dfddce6989f88201b95e83d216406dc17a4cc43ba085011c9a3", "1375fa8269d36def1af0180b18e31c2ecf49b0f69311fdf728d65c0fcf1b5f80"),
        Plan("title-menu-migration-state", "StardewValley.Menus.TitleMenu", "update", "System.Void", ["Microsoft.Xna.Framework.GameTime"], [Field("IsDoingStorageMigration", "IsDoingStorageMigration")], false, "instance;System.Void StardewValley.Menus.TitleMenu::update(Microsoft.Xna.Framework.GameTime)", 1, "12cf32ab09a010894100b91f419d288e9a94fb95141b6be5455741ce122b99dd", "e6a4e7dc70747f2c639864bd1f6e41315faa97c12e6d77faf740ddd8a525b9fa"),
        Plan("mobile-display-activity", "StardewValley.Mobile.MobileDisplay", "SetupDisplaySettings", "System.Void", [], [Activity()], true, "static;System.Void StardewValley.Mobile.MobileDisplay::SetupDisplaySettings()", 1, "fefc7af1c89148429879fbc2b457817689bb8cf249e4f4b7a4c5f2cf126bd30f", "bbd6f6ce523b113d772fc9b2e345c07c491905f57fcadb85d4b683ae331b26c0"),
    ];

    public static ImmutableArray<GameHostApprovedMutationContract> ApprovedMutations =>
        Plans.Select(static plan => new GameHostApprovedMutationContract(
            plan.Id,
            InputRelativePath,
            plan.TargetMemberSignature,
            plan.ExpectedMatchCount,
            plan.PreconditionSha256,
            plan.PostconditionSha256,
            AppliedEntitlementBehavior.Preserved)).ToImmutableArray();

    private static GameHostBridgeMethodPlan Plan(
        string id,
        string type,
        string name,
        string returnType,
        ImmutableArray<string> parameters,
        ImmutableArray<GameHostBridgeActionPlan> actions,
        bool rewriteFirstLocalToActivity,
        string targetMemberSignature,
        int expectedMatchCount,
        string preconditionSha256,
        string postconditionSha256) =>
        new(id, type, name, returnType, parameters, actions, rewriteFirstLocalToActivity,
            targetMemberSignature, expectedMatchCount, preconditionSha256, postconditionSha256);

    private static GameHostBridgeActionPlan Activity() =>
        new(GameHostBridgeActionKind.Activity, "instance", "GetActivity");

    private static GameHostBridgeActionPlan Method(string sourceName, string bridgeName) =>
        new(GameHostBridgeActionKind.Method, sourceName, bridgeName);

    private static GameHostBridgeActionPlan Field(string sourceName, string bridgeName) =>
        new(GameHostBridgeActionKind.Field, sourceName, bridgeName);
}

internal static class GameHostBridgeRecipeEngine
{
    private const string MainActivityType = "StardewValley.MainActivity";
    private const string InstanceFieldName = "instance";

    private static readonly ImmutableArray<(string Method, Code OpCode)> ExpectedRemainingUses =
    [
        ("instance;System.Void StardewValley.MainActivity+LicensingChecker::.ctor()", Code.Ldsfld),
        ("instance;System.Void StardewValley.MainActivity+LicensingChecker::Allow(System.String)", Code.Ldsfld),
        ("instance;System.Void StardewValley.MainActivity+LicensingChecker::DontAllow(Android.App.PendingIntent)", Code.Ldsfld),
        ("instance;System.Void StardewValley.MainActivity::OnCreate(Android.OS.Bundle)", Code.Stsfld),
    ];

    internal static ImmutableArray<AppliedRewriteMutationEvidence> Apply(AssemblyDefinition assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        var module = assembly.MainModule;
        var hostReference = GetOrAddExactHostReference(module);
        var bridgeType = new TypeReference("JunimoGate.GameHost", "GameHostBridge", module, hostReference, false);
        var monoAndroid = module.AssemblyReferences.Single(reference => reference.Name == "Mono.Android");
        var activityType = new TypeReference("Android.App", "Activity", module, monoAndroid, false);

        if (CountInstanceUses(module) != 18)
        {
            throw new InvalidDataException("The target does not contain the exact 18 guarded MainActivity.instance uses.");
        }

        var evidence = ImmutableArray.CreateBuilder<AppliedRewriteMutationEvidence>(GameHostBridgeRecipe.Plans.Length);
        foreach (var plan in GameHostBridgeRecipe.Plans)
        {
            var method = FindMethod(module, plan);
            var precondition = Fingerprint(method);
            if (!precondition.Equals(plan.PreconditionSha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Bridge precondition failed for mutation '{plan.Id}'.");
            }

            var observed = 0;
            foreach (var action in plan.Actions)
            {
                observed += Apply(method, action, bridgeType, activityType);
            }

            if (plan.RewriteFirstLocalToActivity)
            {
                if (method.Body.Variables.Count == 0 ||
                    Normalize(method.Body.Variables[0].VariableType.FullName) != MainActivityType)
                {
                    throw new InvalidDataException($"Bridge local-variable guard failed for mutation '{plan.Id}'.");
                }

                method.Body.Variables[0].VariableType = activityType;
            }

            if (observed != plan.ExpectedMatchCount)
            {
                throw new InvalidDataException(
                    $"Bridge mutation '{plan.Id}' observed {observed} matches; expected {plan.ExpectedMatchCount}.");
            }

            var transformed = Fingerprint(method);
            if (transformed.Equals(precondition, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Bridge mutation '{plan.Id}' did not change the guarded method body.");
            }

            evidence.Add(new AppliedRewriteMutationEvidence(
                plan.Id,
                GameHostBridgeRecipe.InputRelativePath,
                plan.TargetMemberSignature,
                plan.ExpectedMatchCount,
                observed,
                precondition,
                plan.PostconditionSha256,
                AppliedEntitlementBehavior.Preserved));
        }

        ValidateRemainingUses(module);
        return evidence.MoveToImmutable();
    }

    internal static void ValidatePostconditions(AssemblyDefinition assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        var module = assembly.MainModule;
        var hostReferences = module.AssemblyReferences
            .Where(static reference => reference.Name == "JunimoGate.GameHost")
            .ToArray();
        if (hostReferences.Length != 1 || hostReferences[0].Version != new Version(1, 0, 0, 0))
        {
            throw new InvalidDataException("The rewritten assembly does not contain the exact GameHost reference.");
        }

        var mismatches = new List<string>();
        foreach (var plan in GameHostBridgeRecipe.Plans)
        {
            var method = FindMethod(module, plan);
            var actualPostcondition = Fingerprint(method);
            if (!actualPostcondition.Equals(plan.PostconditionSha256, StringComparison.Ordinal))
            {
                mismatches.Add($"{plan.Id}={actualPostcondition}");
            }
        }

        if (mismatches.Count > 0)
        {
            throw new InvalidDataException(
                $"Reopened bridge postconditions differ: {string.Join(',', mismatches)}.");
        }

        ValidateRemainingUses(module);
    }

    internal static string Fingerprint(MethodDefinition method)
    {
        var body = method.Body;
        var index = body.Instructions
            .Select(static (instruction, ordinal) => (instruction, ordinal))
            .ToDictionary(static item => item.instruction, static item => item.ordinal);
        var builder = new StringBuilder();
        builder.AppendLine(CanonicalMethod(method));
        builder.Append("init=").Append(body.InitLocals).Append(";max=").Append(body.MaxStackSize).AppendLine();
        foreach (var variable in body.Variables)
        {
            builder.Append("V|").Append(variable.Index).Append('|').Append(Normalize(variable.VariableType.FullName)).AppendLine();
        }

        foreach (var instruction in body.Instructions)
        {
            builder.Append("I|").Append(index[instruction]).Append('|').Append(instruction.OpCode.Code).Append('|')
                .Append(CanonicalOperand(instruction.Operand, index)).AppendLine();
        }

        foreach (var handler in body.ExceptionHandlers)
        {
            builder.Append("E|").Append(handler.HandlerType).Append('|')
                .Append(InstructionIndex(handler.TryStart, index)).Append('|')
                .Append(InstructionIndex(handler.TryEnd, index)).Append('|')
                .Append(InstructionIndex(handler.HandlerStart, index)).Append('|')
                .Append(InstructionIndex(handler.HandlerEnd, index)).Append('|')
                .Append(InstructionIndex(handler.FilterStart, index)).Append('|')
                .Append(handler.CatchType is null ? "-" : Normalize(handler.CatchType.FullName))
                .AppendLine();
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private static MethodDefinition FindMethod(ModuleDefinition module, GameHostBridgeMethodPlan plan)
    {
        var types = AllTypes(module).Where(type => Normalize(type.FullName) == plan.Type).ToArray();
        if (types.Length != 1)
        {
            throw new InvalidDataException($"Bridge target type guard failed for mutation '{plan.Id}'.");
        }

        var candidates = types[0].Methods.Where(method =>
            method.Name == plan.Name &&
            Normalize(method.ReturnType.FullName) == plan.ReturnType &&
            method.Parameters.Select(parameter => Normalize(parameter.ParameterType.FullName))
                .SequenceEqual(plan.Parameters, StringComparer.Ordinal)).ToArray();
        if (candidates.Length != 1 || !candidates[0].HasBody)
        {
            throw new InvalidDataException($"Bridge target method guard failed for mutation '{plan.Id}'.");
        }

        if (!CanonicalMethod(candidates[0]).Equals(plan.TargetMemberSignature, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Bridge target signature guard failed for mutation '{plan.Id}'.");
        }

        return candidates[0];
    }

    private static int Apply(
        MethodDefinition method,
        GameHostBridgeActionPlan action,
        TypeReference bridgeType,
        TypeReference activityType)
    {
        var instructions = method.Body.Instructions;
        if (action.Kind == GameHostBridgeActionKind.Activity)
        {
            var loads = instructions.Where(IsInstanceLoad).ToArray();
            if (loads.Length != 1)
            {
                throw new InvalidDataException($"Activity load guard failed in '{CanonicalMethod(method)}'.");
            }

            loads[0].OpCode = OpCodes.Call;
            loads[0].Operand = BridgeMethod(bridgeType, action.BridgeName, activityType, []);
            return 1;
        }

        Instruction source;
        TypeReference returnType;
        TypeReference[] parameters;
        if (action.Kind == GameHostBridgeActionKind.Method)
        {
            var calls = instructions.Where(instruction =>
                instruction.Operand is MethodReference reference &&
                Normalize(reference.DeclaringType.FullName) == MainActivityType &&
                reference.Name == action.SourceName).ToArray();
            if (calls.Length != 1)
            {
                throw new InvalidDataException($"Source method guard failed for '{action.SourceName}'.");
            }

            source = calls[0];
            var oldMethod = (MethodReference)source.Operand;
            returnType = oldMethod.ReturnType;
            parameters = oldMethod.Parameters.Select(static parameter => parameter.ParameterType).ToArray();
        }
        else
        {
            var fields = instructions.Where(instruction =>
                instruction.Operand is FieldReference reference &&
                Normalize(reference.DeclaringType.FullName) == MainActivityType &&
                reference.Name == action.SourceName).ToArray();
            if (fields.Length != 1)
            {
                throw new InvalidDataException($"Source field guard failed for '{action.SourceName}'.");
            }

            source = fields[0];
            returnType = ((FieldReference)source.Operand).FieldType;
            parameters = [];
        }

        var sourceIndex = instructions.IndexOf(source);
        var load = instructions.Take(sourceIndex).LastOrDefault(IsInstanceLoad) ??
            throw new InvalidDataException($"No guarded instance load precedes '{action.SourceName}'.");
        var loadIndex = instructions.IndexOf(load);
        if (instructions.Skip(loadIndex + 1).Take(sourceIndex - loadIndex - 1).Any(IsInstanceLoad))
        {
            throw new InvalidDataException($"Ambiguous instance load precedes '{action.SourceName}'.");
        }

        load.OpCode = OpCodes.Nop;
        load.Operand = null;
        source.OpCode = OpCodes.Call;
        source.Operand = BridgeMethod(bridgeType, action.BridgeName, returnType, parameters);
        return 1;
    }

    private static AssemblyNameReference GetOrAddExactHostReference(ModuleDefinition module)
    {
        var existing = module.AssemblyReferences
            .Where(static reference => reference.Name == "JunimoGate.GameHost")
            .ToArray();
        if (existing.Length > 1 || (existing.Length == 1 && existing[0].Version != new Version(1, 0, 0, 0)))
        {
            throw new InvalidDataException("The target contains an unexpected GameHost assembly reference.");
        }

        if (existing.Length == 1)
        {
            return existing[0];
        }

        var reference = new AssemblyNameReference("JunimoGate.GameHost", new Version(1, 0, 0, 0));
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
        {
            method.Parameters.Add(new ParameterDefinition(parameter));
        }

        return method;
    }

    private static void ValidateRemainingUses(ModuleDefinition module)
    {
        var actual = InstanceUses(module)
            .Select(static use => (CanonicalMethod(use.Method), use.Instruction.OpCode.Code))
            .OrderBy(static use => use.Item1, StringComparer.Ordinal)
            .ThenBy(static use => use.Code)
            .ToImmutableArray();
        var expected = ExpectedRemainingUses
            .OrderBy(static use => use.Method, StringComparer.Ordinal)
            .ThenBy(static use => use.OpCode)
            .ToImmutableArray();
        if (!actual.SequenceEqual(expected))
        {
            throw new InvalidDataException("The rewritten MainActivity.instance use set is not the exact protected four-use set.");
        }
    }

    private static bool IsInstanceLoad(Instruction instruction) =>
        instruction.OpCode.Code == Code.Ldsfld &&
        instruction.Operand is FieldReference field &&
        Normalize(field.DeclaringType.FullName) == MainActivityType &&
        field.Name == InstanceFieldName;

    private static int CountInstanceUses(ModuleDefinition module) => InstanceUses(module).Count();

    private static IEnumerable<(MethodDefinition Method, Instruction Instruction)> InstanceUses(ModuleDefinition module) =>
        AllTypes(module)
            .SelectMany(static type => type.Methods)
            .Where(static method => method.HasBody)
            .SelectMany(method => method.Body.Instructions
                .Where(instruction => instruction.Operand is FieldReference field &&
                    Normalize(field.DeclaringType.FullName) == MainActivityType &&
                    field.Name == InstanceFieldName)
                .Select(instruction => (method, instruction)));

    private static IEnumerable<TypeDefinition> AllTypes(ModuleDefinition module) =>
        module.Types.SelectMany(Flatten);

    private static IEnumerable<TypeDefinition> Flatten(TypeDefinition type)
    {
        yield return type;
        foreach (var nested in type.NestedTypes.SelectMany(Flatten))
        {
            yield return nested;
        }
    }

    private static string CanonicalMethod(MethodReference method) =>
        $"{(method.HasThis ? "instance" : "static")};{Normalize(method.ReturnType.FullName)} " +
        $"{Normalize(method.DeclaringType.FullName)}::{method.Name}(" +
        $"{string.Join(',', method.Parameters.Select(parameter => Normalize(parameter.ParameterType.FullName)))})";

    private static string CanonicalOperand(object? operand, IReadOnlyDictionary<Instruction, int> index) =>
        operand switch
        {
            null => "-",
            Instruction instruction => $"I{index[instruction]}",
            Instruction[] instructions => string.Join(',', instructions.Select(instruction => $"I{index[instruction]}")),
            MethodReference method => $"M:{CanonicalMethod(method)}",
            FieldReference field => $"F:{Normalize(field.FieldType.FullName)} {Normalize(field.DeclaringType.FullName)}::{field.Name}",
            TypeReference type => $"T:{Normalize(type.FullName)}",
            ParameterDefinition parameter => $"P:{parameter.Index}",
            VariableDefinition variable => $"V:{variable.Index}",
            string text => $"S:{Convert.ToBase64String(Encoding.UTF8.GetBytes(text))}",
            IFormattable formattable => $"N:{formattable.ToString(null, CultureInfo.InvariantCulture)}",
            _ => $"O:{operand}",
        };

    private static int InstructionIndex(Instruction? instruction, IReadOnlyDictionary<Instruction, int> index) =>
        instruction is null ? -1 : index[instruction];

    private static string Normalize(string value) => value.Replace('/', '+');
}
