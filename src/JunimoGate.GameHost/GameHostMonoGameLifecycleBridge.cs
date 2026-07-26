using System.Reflection;
using Microsoft.Xna.Framework;

namespace JunimoGate.GameHost;

/// <summary>
/// Replays only the MonoGame platform activation that a delayed GameRunner misses because Android
/// resumed the host Activity before the Game existed. It does not invoke Android lifecycle methods.
/// </summary>
internal static class GameHostMonoGameLifecycleBridge
{
    private const string PlatformTypeName = "Microsoft.Xna.Framework.GamePlatform";
    private const string AndroidPlatformTypeName = "Microsoft.Xna.Framework.AndroidGamePlatform";

    internal static void ActivateDelayedGame(Game game)
    {
        ArgumentNullException.ThrowIfNull(game);

        var provider = typeof(Game).Assembly;
        var platformType = provider.GetType(PlatformTypeName, throwOnError: true, ignoreCase: false)
            ?? throw new TypeLoadException("The exact MonoGame GamePlatform type is unavailable.");
        var platform = game.Services.GetService(platformType)
            ?? throw new InvalidOperationException("The GameRunner did not register its MonoGame platform service.");
        if (!string.Equals(platform.GetType().FullName, AndroidPlatformTypeName, StringComparison.Ordinal))
        {
            throw new TypeLoadException("The GameRunner did not create the exact Android MonoGame platform.");
        }

        var activeProperty = platformType.GetProperty(
            "IsActive",
            BindingFlags.Instance | BindingFlags.Public)
            ?? throw new MissingMemberException(PlatformTypeName, "IsActive");
        var getter = activeProperty.GetMethod;
        var setter = activeProperty.GetSetMethod(nonPublic: true);
        if (activeProperty.PropertyType != typeof(bool) ||
            getter is null || !getter.IsPublic ||
            setter is null || setter.IsPublic || setter.IsStatic ||
            setter.GetParameters() is not [{ ParameterType: var parameterType }] ||
            parameterType != typeof(bool))
        {
            throw new MissingMethodException("The exact MonoGame GamePlatform.IsActive contract is unavailable.");
        }

        if ((bool)(getter.Invoke(platform, null)
            ?? throw new InvalidOperationException("MonoGame platform activity state returned null.")))
        {
            return;
        }

        setter.Invoke(platform, [true]);
        if (!(bool)(getter.Invoke(platform, null)
            ?? throw new InvalidOperationException("MonoGame platform activity state returned null.")))
        {
            throw new InvalidOperationException("MonoGame platform activation did not take effect.");
        }
    }
}
