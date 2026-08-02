using JunimoGate.Tests;
using Mono.Cecil;
using StardewModdingAPI.Framework.ModLoading;

internal static class CustomAttributeTypeScopeRewriterTests
{
    public static void RewritesNestedTypeArguments()
    {
        using var module = ModuleDefinition.CreateModule("AttributeScopeFixture", ModuleKind.Dll);
        var desktopGame = new AssemblyNameReference("DesktopGame", new Version(1, 0));
        var androidGame = new AssemblyNameReference("AndroidGame", new Version(1, 0));
        module.AssemblyReferences.Add(desktopGame);
        module.AssemblyReferences.Add(androidGame);

        var attributeType = new TypeReference("Fixture", "PatchAttribute", module, module.TypeSystem.CoreLibrary);
        var typeType = module.ImportReference(typeof(Type));
        var typeArray = new ArrayType(typeType);
        var constructor = new MethodReference(".ctor", module.TypeSystem.Void, attributeType)
        {
            HasThis = true,
        };
        constructor.Parameters.Add(new ParameterDefinition(typeType));
        constructor.Parameters.Add(new ParameterDefinition(typeArray));

        var directType = new TypeReference("Game", "DirectType", module, desktopGame);
        var arrayType = new TypeReference("Game", "ArrayType", module, desktopGame);
        var propertyType = new TypeReference("Game", "PropertyType", module, desktopGame);
        var attribute = new CustomAttribute(constructor);
        attribute.ConstructorArguments.Add(new CustomAttributeArgument(typeType, directType));
        attribute.ConstructorArguments.Add(new CustomAttributeArgument(
            typeArray,
            new[] { new CustomAttributeArgument(typeType, arrayType) }));
        attribute.Properties.Add(new CustomAttributeNamedArgument(
            "Target",
            new CustomAttributeArgument(typeType, propertyType)));

        var target = new TypeDefinition("Fixture", "Target", TypeAttributes.Public);
        target.CustomAttributes.Add(attribute);
        module.Types.Add(target);

        CustomAttributeTypeScopeRewriter.Rewrite(module, type =>
        {
            if (ReferenceEquals(type.Scope, desktopGame))
                type.Scope = androidGame;
        });

        TestHarness.True(ReferenceEquals(directType.Scope, androidGame));
        TestHarness.True(ReferenceEquals(arrayType.Scope, androidGame));
        TestHarness.True(ReferenceEquals(propertyType.Scope, androidGame));
    }

    public static void IncludesOnlyPubliclyVisibleNestedTypes()
    {
        var publicOuter = new TypeDefinition("Game", "PublicOuter", TypeAttributes.Public);
        var publicNested = new TypeDefinition("", "PublicNested", TypeAttributes.NestedPublic);
        var privateNested = new TypeDefinition("", "PrivateNested", TypeAttributes.NestedPrivate);
        publicOuter.NestedTypes.Add(publicNested);
        publicOuter.NestedTypes.Add(privateNested);

        var internalOuter = new TypeDefinition("Game", "InternalOuter", TypeAttributes.NotPublic);
        var nestedInInternal = new TypeDefinition("", "PublicNested", TypeAttributes.NestedPublic);
        internalOuter.NestedTypes.Add(nestedInInternal);

        TestHarness.True(TypeDefinitionVisibility.IsPubliclyVisible(publicOuter));
        TestHarness.True(TypeDefinitionVisibility.IsPubliclyVisible(publicNested));
        TestHarness.False(TypeDefinitionVisibility.IsPubliclyVisible(privateNested));
        TestHarness.False(TypeDefinitionVisibility.IsPubliclyVisible(nestedInInternal));
    }

}
