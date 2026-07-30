using System.Security.Cryptography;
using JunimoGate.Core;
using JunimoGate.Extraction;
using JunimoGate.Rewriter;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace JunimoGate.Rewriter.Tests;

internal sealed record SyntheticGameOptions(
    Version? AssemblyVersion = null,
    Guid? ModuleVersionId = null,
    bool AddIrrelevantCall = false,
    bool AddExtraLocal = false,
    string? MissingRuleId = null,
    string? DuplicateRuleId = null,
    string? InvalidStackRuleId = null);

internal sealed record SyntheticGameFixture(
    string WorkspacePath,
    string InputPath,
    string OutputPath,
    ValidatedExecutionPlan Plan);

internal static class SyntheticGameAssemblies
{
    public static SyntheticGameFixture Create(string root, SyntheticGameOptions? options = null)
    {
        options ??= new SyntheticGameOptions();
        var workspace = Path.Combine(root, "source");
        var input = Path.Combine(workspace, GameHostBridgeRecipe.InputRelativePath.Replace('/', Path.DirectorySeparatorChar));
        var output = Path.Combine(root, "staging", "StardewValley.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(input)!);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        WriteGame(input, options);

        var bytes = File.ReadAllBytes(input);
        var payload = new ValidatedWorkspacePayload(
            "assembly",
            GameHostBridgeRecipe.InputRelativePath,
            bytes.LongLength,
            Convert.ToHexStringLower(SHA256.HashData(bytes)));
        var plan = new ValidatedExecutionPlan(
            KnownGameCertificate.PlayPackageName,
            options.AssemblyVersion?.ToString() ?? "1.6.99",
            999,
            GameInstallationDiscoveryCoordinator.SupportedAbi,
            new string('a', 64),
            workspace,
            new string('b', 64),
            DateTimeOffset.UtcNow,
            [payload]);
        return new SyntheticGameFixture(workspace, input, output, plan);
    }

    private static void WriteGame(string path, SyntheticGameOptions options)
    {
        using var assembly = AssemblyDefinition.CreateAssembly(
            new AssemblyNameDefinition("StardewValley", options.AssemblyVersion ?? new Version(1, 6, 99, 0)),
            "StardewValley",
            ModuleKind.Dll);
        var module = assembly.MainModule;
        module.Mvid = options.ModuleVersionId ?? Guid.NewGuid();
        var monoAndroid = AddReference(module, "Mono.Android");
        var monoGame = AddReference(module, "MonoGame.Framework");
        var androidX = AddReference(module, "Xamarin.AndroidX.DocumentFile");
        var activityType = new TypeReference("Android.App", "Activity", module, monoAndroid, false);
        var mainActivity = new TypeDefinition(
            "StardewValley",
            "MainActivity",
            TypeAttributes.Public | TypeAttributes.Class,
            activityType);
        module.Types.Add(mainActivity);
        var instance = new FieldDefinition(
            "instance",
            FieldAttributes.Public | FieldAttributes.Static,
            mainActivity);
        mainActivity.Fields.Add(instance);

        var sourceMethods = new Dictionary<string, MethodDefinition>(StringComparer.Ordinal);
        var sourceFields = new Dictionary<string, FieldDefinition>(StringComparer.Ordinal);
        foreach (var action in GameHostBridgeRecipe.Plans.SelectMany(static plan => plan.Actions))
        {
            if (action.Kind == GameHostBridgeActionKind.Activity)
                continue;
            if (action.Kind == GameHostBridgeActionKind.Field)
            {
                sourceFields.TryAdd(action.SourceName, new FieldDefinition(
                    action.SourceName,
                    FieldAttributes.Public,
                    ResolveType(module, mainActivity, monoAndroid, monoGame, androidX, action.SourceReturnType)));
                continue;
            }

            var key = action.SourceName + "(" + string.Join(',', action.SourceParameters) + ")";
            if (sourceMethods.ContainsKey(key))
                continue;
            var method = new MethodDefinition(
                action.SourceName,
                MethodAttributes.Public,
                ResolveType(module, mainActivity, monoAndroid, monoGame, androidX, action.SourceReturnType));
            foreach (var parameter in action.SourceParameters)
                method.Parameters.Add(new ParameterDefinition(ResolveType(module, mainActivity, monoAndroid, monoGame, androidX, parameter)));
            EmitDefaultReturn(method);
            sourceMethods.Add(key, method);
        }
        foreach (var field in sourceFields.Values)
            mainActivity.Fields.Add(field);
        foreach (var method in sourceMethods.Values)
            mainActivity.Methods.Add(method);

        var finish = new MethodReference("Finish", module.TypeSystem.Void, activityType) { HasThis = true };
        var helper = AddHelper(module);
        foreach (var plan in GameHostBridgeRecipe.Plans)
        {
            var owner = GetOrAddType(module, mainActivity, plan.Type);
            var attributes = MethodAttributes.Public;
            var isInstance = plan.TargetMemberSignature.StartsWith("instance;", StringComparison.Ordinal);
            if (!isInstance)
                attributes |= MethodAttributes.Static;
            if (plan.Name == ".ctor")
                attributes |= MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName;
            var target = new MethodDefinition(
                plan.Name,
                attributes,
                ResolveType(module, mainActivity, monoAndroid, monoGame, androidX, plan.ReturnType));
            foreach (var parameter in plan.Parameters)
                target.Parameters.Add(new ParameterDefinition(ResolveType(module, mainActivity, monoAndroid, monoGame, androidX, parameter)));
            owner.Methods.Add(target);

            if (options.AddExtraLocal)
            {
                target.Body.InitLocals = true;
                target.Body.Variables.Add(new VariableDefinition(module.TypeSystem.Int32));
            }
            if (options.AddIrrelevantCall)
                target.Body.Instructions.Add(Instruction.Create(OpCodes.Call, helper));

            if (plan.Id != options.MissingRuleId)
            {
                EmitActions(target, plan, instance, sourceMethods, sourceFields, finish, mainActivity, duplicate: false);
                if (plan.Id == options.DuplicateRuleId)
                    EmitActions(target, plan, instance, sourceMethods, sourceFields, finish, mainActivity, duplicate: true);
            }
            if (plan.Id == options.InvalidStackRuleId)
                target.Body.Instructions.Add(Instruction.Create(OpCodes.Pop));
            EmitReturn(target);
        }

        assembly.Write(path, new WriterParameters { WriteSymbols = false });
    }

    private static void EmitActions(
        MethodDefinition target,
        GameHostBridgeMethodPlan plan,
        FieldReference instance,
        IReadOnlyDictionary<string, MethodDefinition> sourceMethods,
        IReadOnlyDictionary<string, FieldDefinition> sourceFields,
        MethodReference finish,
        TypeDefinition mainActivity,
        bool duplicate)
    {
        var il = target.Body.Instructions;
        foreach (var action in plan.Actions)
        {
            if (action.Kind == GameHostBridgeActionKind.Activity)
            {
                il.Add(Instruction.Create(OpCodes.Ldsfld, instance));
                if (plan.RewriteActivityLocal)
                {
                    target.Body.InitLocals = true;
                    var local = new VariableDefinition(mainActivity);
                    target.Body.Variables.Add(local);
                    il.Add(Instruction.Create(OpCodes.Stloc, local));
                    il.Add(Instruction.Create(OpCodes.Ldloc, local));
                }
                il.Add(Instruction.Create(OpCodes.Callvirt, finish));
                continue;
            }

            il.Add(Instruction.Create(OpCodes.Ldsfld, instance));
            if (action.Kind == GameHostBridgeActionKind.Field)
            {
                il.Add(Instruction.Create(OpCodes.Ldfld, sourceFields[action.SourceName]));
                il.Add(Instruction.Create(OpCodes.Pop));
                continue;
            }

            var key = action.SourceName + "(" + string.Join(',', action.SourceParameters) + ")";
            var source = sourceMethods[key];
            foreach (var parameter in source.Parameters)
                EmitDefaultValue(il, parameter.ParameterType);
            il.Add(Instruction.Create(OpCodes.Callvirt, source));
            if (source.ReturnType.MetadataType != MetadataType.Void)
                il.Add(Instruction.Create(OpCodes.Pop));
        }
        _ = duplicate;
    }

    private static MethodDefinition AddHelper(ModuleDefinition module)
    {
        var type = new TypeDefinition("StardewValley", "CompatibilityNoise", TypeAttributes.Public, module.TypeSystem.Object);
        module.Types.Add(type);
        var method = new MethodDefinition("Noop", MethodAttributes.Public | MethodAttributes.Static, module.TypeSystem.Void);
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
        type.Methods.Add(method);
        return method;
    }

    private static TypeDefinition GetOrAddType(ModuleDefinition module, TypeDefinition mainActivity, string fullName)
    {
        if (fullName == "StardewValley.MainActivity")
            return mainActivity;
        var existing = module.Types.SingleOrDefault(type => type.FullName == fullName);
        if (existing is not null)
            return existing;
        var separator = fullName.LastIndexOf('.');
        var created = new TypeDefinition(
            fullName[..separator],
            fullName[(separator + 1)..],
            TypeAttributes.Public | TypeAttributes.Class,
            module.TypeSystem.Object);
        module.Types.Add(created);
        return created;
    }

    private static TypeReference ResolveType(
        ModuleDefinition module,
        TypeDefinition mainActivity,
        AssemblyNameReference monoAndroid,
        AssemblyNameReference monoGame,
        AssemblyNameReference androidX,
        string fullName) => fullName switch
        {
            "System.Void" => module.TypeSystem.Void,
            "System.Boolean" => module.TypeSystem.Boolean,
            "System.Int32" => module.TypeSystem.Int32,
            "System.String" => module.TypeSystem.String,
            "System.Single" => module.TypeSystem.Single,
            "System.Action" => module.ImportReference(typeof(Action)),
            "StardewValley.MainActivity" => mainActivity,
            _ => ExternalType(module, fullName,
                fullName.StartsWith("Android.", StringComparison.Ordinal) ? monoAndroid :
                fullName.StartsWith("Microsoft.Xna.", StringComparison.Ordinal) ? monoGame : androidX),
        };

    private static TypeReference ExternalType(ModuleDefinition module, string fullName, IMetadataScope scope)
    {
        var separator = fullName.LastIndexOf('.');
        return new TypeReference(fullName[..separator], fullName[(separator + 1)..], module, scope, false);
    }

    private static AssemblyNameReference AddReference(ModuleDefinition module, string name)
    {
        var reference = new AssemblyNameReference(name, new Version(1, 0, 0, 0));
        module.AssemblyReferences.Add(reference);
        return reference;
    }

    private static void EmitDefaultReturn(MethodDefinition method)
    {
        if (method.ReturnType.MetadataType != MetadataType.Void)
            EmitDefaultValue(method.Body.Instructions, method.ReturnType);
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
    }

    private static void EmitReturn(MethodDefinition method)
    {
        if (method.ReturnType.MetadataType != MetadataType.Void)
            EmitDefaultValue(method.Body.Instructions, method.ReturnType);
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
    }

    private static void EmitDefaultValue(Mono.Collections.Generic.Collection<Instruction> instructions, TypeReference type) =>
        instructions.Add(type.MetadataType switch
        {
            MetadataType.Boolean or MetadataType.Int32 => Instruction.Create(OpCodes.Ldc_I4_0),
            MetadataType.Single => Instruction.Create(OpCodes.Ldc_R4, 0f),
            _ => Instruction.Create(OpCodes.Ldnull),
        });
}
