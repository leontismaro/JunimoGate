using System.Collections;
using System.Reflection;
using Microsoft.Xna.Framework.Audio;

namespace JunimoGate.GameHost;

/// <summary>
/// Repairs exact public MonoGame OpenAL static state after a previous Game was disposed in the same
/// Android process. It never tears down an active controller and runs only before a new GameRunner.
/// </summary>
internal static class GameHostMonoGameAudioResetBridge
{
    private static readonly object ResetLock = new();

    internal static void PrepareForNewGame()
    {
        lock (ResetLock)
        {
            var monoGameAssembly = typeof(SoundEffect).Assembly;
            var controllerType = RequireType(
                monoGameAssembly,
                "Microsoft.Xna.Framework.Audio.OpenALSoundController");
            var managerType = RequireType(
                monoGameAssembly,
                "Microsoft.Xna.Framework.Audio.OpenALSoundEffectInstanceManager");
            var poolType = RequireType(
                monoGameAssembly,
                "Microsoft.Xna.Framework.Audio.SoundEffectInstancePool");

            var controllerInstance = RequireField(controllerType, "_instance");
            var controllerManager = RequireField(controllerType, "_openALSoundEffectInstanceManager");
            if (controllerInstance.GetValue(null) is not null || controllerManager.GetValue(null) is not null)
            {
                throw new InvalidOperationException("MonoGame audio is still active and cannot be reset.");
            }

            ResetManager(managerType);
            ResetInstancePool(poolType);
            RequireField(controllerType, "_efx").SetValue(null, null);

            var systemState = RequireField(typeof(SoundEffect), "_systemState");
            systemState.SetValue(null, Enum.Parse(systemState.FieldType, "NotInitialized", ignoreCase: false));
            RequireField(typeof(SoundEffect), "ReverbSlot").SetValue(null, 0U);
            RequireField(typeof(SoundEffect), "ReverbEffect").SetValue(null, 0U);
        }
    }

    private static void ResetManager(Type managerType)
    {
        var singletonMutex = RequireField(managerType, "singletonMutex").GetValue(null)
            ?? throw new InvalidDataException("MonoGame audio singleton mutex is unavailable.");
        var instanceField = RequireField(managerType, "instance");
        var runningField = RequireField(managerType, "running");
        var threadField = RequireField(managerType, "underlyingThread");
        var instance = instanceField.GetValue(null);
        if (instance is not null)
        {
            if (runningField.GetValue(instance) is not bool running || running)
            {
                throw new InvalidOperationException("MonoGame audio manager is still running.");
            }

            var thread = threadField.GetValue(instance) as Thread
                ?? throw new InvalidDataException("MonoGame audio manager thread is unavailable.");
            if (thread.IsAlive && !thread.Join(TimeSpan.FromSeconds(2)))
            {
                throw new InvalidOperationException("MonoGame audio manager did not stop in time.");
            }

            lock (singletonMutex)
            {
                if (!ReferenceEquals(instanceField.GetValue(null), instance) ||
                    runningField.GetValue(instance) is not bool confirmedRunning ||
                    confirmedRunning ||
                    thread.IsAlive)
                {
                    throw new InvalidOperationException("MonoGame audio manager state changed during reset.");
                }

                instanceField.SetValue(null, null);
            }
        }

        var pauseMutex = RequireField(managerType, "pauseMutex").GetValue(null)
            ?? throw new InvalidDataException("MonoGame audio pause mutex is unavailable.");
        lock (pauseMutex)
        {
            RequireField(managerType, "paused").SetValue(null, false);
        }
    }

    private static void ResetInstancePool(Type poolType)
    {
        var locker = RequireField(poolType, "_locker").GetValue(null)
            ?? throw new InvalidDataException("MonoGame sound pool mutex is unavailable.");
        lock (locker)
        {
            RequireList(poolType, "_playingInstances").Clear();
            RequireList(poolType, "_pooledInstances").Clear();
        }
    }

    private static IList RequireList(Type type, string name) =>
        RequireField(type, name).GetValue(null) as IList
        ?? throw new InvalidDataException($"MonoGame audio list {name} is unavailable.");

    private static Type RequireType(Assembly assembly, string fullName) =>
        assembly.GetType(fullName, throwOnError: false, ignoreCase: false)
        ?? throw new TypeLoadException($"The exact MonoGame audio type {fullName} is unavailable.");

    private static FieldInfo RequireField(Type type, string name) =>
        type.GetField(name, BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
        ?? throw new MissingFieldException(type.FullName, name);
}
