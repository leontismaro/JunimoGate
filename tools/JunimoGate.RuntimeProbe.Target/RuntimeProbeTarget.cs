using System.Runtime.CompilerServices;

namespace JunimoGate.RuntimeProbe.Target;

/// <summary>
/// A deliberately separate-assembly target for runtime private-access probes.
/// </summary>
public sealed class RuntimeProbeTarget
{
    private int _secret = 10;
    private int _patchedOriginalCalls;
    private int _migrationOriginalCalls;

    public int PatchedOriginalCalls => _patchedOriginalCalls;

    public int MigrationOriginalCalls => _migrationOriginalCalls;

    public int InvokeHarmonyPatched(int value) => HarmonyPatched(value);

    public int InvokeFieldInjection() => ReadSecret();

    public int InvokeTranspiled(int value) => PrivateIlBody(value);

    public bool InvokeCheckStorageMigration() => CheckStorageMigration();

    [MethodImpl(MethodImplOptions.NoInlining)]
    private int HarmonyPatched(int value)
    {
        _patchedOriginalCalls++;
        return PrivateTransform(value) + _secret;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private int ReadSecret() => _secret;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private int PrivateIlBody(int value) => PrivateTransform(value) + _secret;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private bool CheckStorageMigration()
    {
        _migrationOriginalCalls++;
        return true;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private int CopyPrivateAccess(int value) => PrivateTransform(value) + _secret;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private int PrivateTransform(int value) => value * 2;
}
