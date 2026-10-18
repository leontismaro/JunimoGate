using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using HarmonyLib;
using JunimoGate.Tests;

internal static class AndroidDetourSafetyTests
{
    private const BindingFlags StaticMethods = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly Assembly Package = typeof(Harmony).Assembly;
    private static readonly Type Policy = Package.GetType("MonoMod.Core.Platforms.Systems.AndroidMemoryPatch", throwOnError: true)!;
    private delegate void ApplyPatch(bool executable, nint code, nint write, ReadOnlySpan<byte> data,
        Span<byte> backup, nint pageSize, string[] maps, Action<nint, nint, int> protect,
        Action<nint, nint> flush, Action<string, Exception> failFast);
    private static readonly ApplyPatch Apply = Policy.GetMethod("Apply", StaticMethods)!.CreateDelegate<ApplyPatch>();

    internal static (string Name, Action Test)[] Cases =>
    [
        ("Android patch rejects a gap or unreadable tail before touching memory", RejectInvalidTail),
        ("Android patch rejects nonexecutable code and native address overflow", RejectInvalidAddress),
        ("Android mapping validation normalizes ARM64 tags without accepting a wrapped range", ValidateTaggedMaps),
        ("Android patch accepts only provable shared executable aliases", ValidateAliases),
        ("ARM64 rejects undersized initial and retarget detours", ValidateArm64Size),
        ("Android Mono rejects unknown or invalid JIT body bounds", ValidateJitBounds),
        ("Android patch preserves individual mapping protections and complete backup", SuccessfulPatch),
        ("Android patch rejects undersized backup before touching memory", RejectShortBackup),
        ("Android patch restores permissions after a prewrite failure", ProtectFailure),
        ("Android patch restores original bytes and cache after a flush failure", FlushFailure),
        ("Android patch rolls back when final permission restoration fails", RestoreFailure),
        ("Android patch invokes fatal handler if rollback cannot be confirmed", RollbackFailure),
        ("Pending detour disposal does not undo an unapplied patch", PendingDetour),
    ];

    private static object? Invoke(Type type, string name, params object?[] args)
    {
        try { return type.GetMethod(name, StaticMethods)!.Invoke(null, args); }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(error.InnerException).Throw();
            throw;
        }
    }

    private static object Plan(string[] maps, nint code, nint write, int length, bool executable = true)
        => Invoke(Policy, "Plan", maps, code, write, length, executable, (nint)64)!;

    private static void RejectInvalidTail()
    {
        using var memory = new PatchMemory();
        foreach (var tail in new[] { "gap", "---p", "-wxp" })
        {
            string[] maps = tail == "gap" ? [memory.Map(0, 64, "r-xp")] :
                [memory.Map(0, 64, "r-xp"), memory.Map(64, 128, tail)];
            var calls = 0;
            TestHarness.Throws<InvalidOperationException>(() => memory.Run(maps,
                (_, _, _) => calls++, (_, _) => calls++));
            TestHarness.Equal(0, calls);
            TestHarness.True(memory.Bytes().SequenceEqual(memory.Original));
        }
    }

    private static void RejectInvalidAddress()
    {
        TestHarness.Throws<InvalidOperationException>(() => Plan(["1000-2000 rw-p 0 00:00 0"], 0x1000, 0x1000, 16));
        TestHarness.Throws<OverflowException>(() => Plan([], (nint)(-8), (nint)(-8), 16));
        TestHarness.Throws<ArgumentOutOfRangeException>(() => Plan([], 0, 0, 16));
        TestHarness.Throws<InvalidOperationException>(() => Plan(["1000-2000 r-xp 0 00:00 0"], 0x1ff8, 0x1ff8, 16));
    }

    private static void ValidateAliases()
    {
        string[] valid = ["1000-2000 r-xs 0 00:01 42", "3000-4000 rw-s 0 00:01 42"];
        var regions = (Array)Plan(valid, 0x1000, 0x3000, 16);
        TestHarness.Equal(1, regions.Length);
        foreach (var invalid in new[]
        {
            "3000-4000 rw-s 10 00:01 42", // different backing offset
            "3000-4000 rw-s 0 00:01 43", // different inode
            "3000-4000 rw-s 0 00:02 42", // different device
            "3000-4000 rw-p 0 00:01 42", // private mapping
            "3000-4000 rw-s 0 00:01 0", // anonymous identity
            "3000-4000 r--s 0 00:01 42", // alias is not writable
        })
            TestHarness.Throws<InvalidOperationException>(() => Plan([valid[0], invalid], 0x1000, 0x3000, 16));
    }

    private static void ValidateTaggedMaps()
    {
        if (IntPtr.Size != 8) return;
        var tagged = unchecked((nint)0xab00_0000_0000_1000UL);
        var regions = (Array)Plan(["1000-2000 r-xp 0 00:00 0"], tagged, tagged, 16);
        var region = regions.GetValue(0)!;
        TestHarness.Equal((nint)0x1000, (nint)region.GetType().GetProperty("Address")!.GetValue(region)!);
        var nearEnd = unchecked((nint)0xabff_ffff_ffff_fff8UL);
        TestHarness.Throws<OverflowException>(() => Plan([], nearEnd, nearEnd, 16));
    }

    private static void ValidateArm64Size()
    {
        var type = Package.GetType("MonoMod.Core.Platforms.Architectures.Arm64Arch", throwOnError: true)!;
        var arch = Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null, args: [null], culture: null)!;
        object Compute(int size) => type.GetMethod("ComputeDetourInfo")!.Invoke(arch, [(nint)0x1000, (nint)0x2000, size])!;
        foreach (var size in new[] { -2, 0, 1, 4, 8, 12, 15 })
        {
            var error = TestHarness.Throws<TargetInvocationException>(() => Compute(size));
            TestHarness.True(error.InnerException is NotSupportedException);
        }
        var detour = Compute(16);
        TestHarness.Equal(16, (int)detour.GetType().GetProperty("Size")!.GetValue(detour)!);
        foreach (var size in new[] { 0, 4, 8, 12 })
        {
            var error = TestHarness.Throws<TargetInvocationException>(() =>
                type.GetMethod("ComputeRetargetInfo")!.Invoke(arch, [detour, (nint)0x3000, size]));
            TestHarness.True(error.InnerException is NotSupportedException);
        }
    }

    private static void ValidateJitBounds()
    {
        var mono = Package.GetType("MonoMod.Core.Platforms.Runtimes.MonoRuntime", throwOnError: true)!;
        TestHarness.Equal(20, (int)Invoke(mono, "ValidateJitCodeSize", (nint)0x1000, (nint)0x1000, 20)!);
        foreach (var (entry, start, size) in new (nint, nint, int)[] { (0, 0, 20), (0x1004, 0x1000, 20), (0x1000, 0x1000, 0) })
            TestHarness.Throws<NotSupportedException>(() => Invoke(mono, "ValidateJitCodeSize", entry, start, size));
        TestHarness.Throws<OverflowException>(() => Invoke(mono, "ValidateJitCodeSize", (nint)(-8), (nint)(-8), 16));
    }

    private static void SuccessfulPatch()
    {
        using var memory = new PatchMemory();
        var operations = new List<(nint Address, nint Length, int Protection)>();
        var flushes = 0;
        memory.Run(memory.Maps, (address, length, protection) => operations.Add((address, length, protection)), (_, _) => flushes++);
        TestHarness.True(memory.Bytes().SequenceEqual(memory.Replacement));
        TestHarness.True(memory.Backup.SequenceEqual(memory.Original));
        TestHarness.True(operations.SequenceEqual(new[] { (memory.Base, (nint)64, 7), (memory.Base, (nint)64, 5) }));
        TestHarness.Equal(1, flushes);

        // Different permissions on both sides of a mapping boundary must both be restored.
        operations.Clear();
        memory.Run([memory.Map(0, 64, "r-xp"), memory.Map(64, 128, "r--p")],
            (a, l, p) => operations.Add((a, l, p)), (_, _) => { }, executable: false);
        TestHarness.True(operations.Select(x => x.Protection).SequenceEqual(new[] { 7, 3, 1, 5 }));
    }

    private static void RejectShortBackup()
    {
        using var memory = new PatchMemory();
        TestHarness.Throws<ArgumentException>(() => Apply(true, memory.Target, memory.Target,
            memory.Replacement, new byte[1], 64, memory.Maps,
            (_, _, _) => throw new Exception("must not protect"), (_, _) => throw new Exception("must not flush"), Fatal));
        TestHarness.True(memory.Bytes().SequenceEqual(memory.Original));
    }

    private static void ProtectFailure()
    {
        using var memory = new PatchMemory();
        var changes = 0;
        var flushes = 0;
        TestHarness.Throws<InjectedFailure>(() => memory.Run(memory.Maps,
            (_, _, _) => { if (++changes == 1) throw new InjectedFailure(); }, (_, _) => flushes++));
        TestHarness.Equal(2, changes);
        TestHarness.Equal(0, flushes);
        TestHarness.True(memory.Bytes().SequenceEqual(memory.Original));
    }

    private static void FlushFailure()
    {
        using var memory = new PatchMemory();
        var flushes = 0;
        var changes = new List<int>();
        TestHarness.Throws<InjectedFailure>(() => memory.Run(memory.Maps, (_, _, p) => changes.Add(p),
            (_, _) => { if (++flushes == 1) throw new InjectedFailure(); }));
        TestHarness.Equal(2, flushes);
        TestHarness.True(changes.SequenceEqual(new[] { 7, 7, 5 }));
        TestHarness.True(memory.Bytes().SequenceEqual(memory.Original));
    }

    private static void RestoreFailure()
    {
        using var memory = new PatchMemory();
        var changes = new List<int>();
        var flushes = 0;
        TestHarness.Throws<InjectedFailure>(() => memory.Run(memory.Maps, (_, _, p) =>
        {
            changes.Add(p);
            if (changes.Count == 2) throw new InjectedFailure();
        }, (_, _) => flushes++));
        TestHarness.True(changes.SequenceEqual(new[] { 7, 5, 7, 5 }));
        TestHarness.Equal(2, flushes);
        TestHarness.True(memory.Bytes().SequenceEqual(memory.Original));
    }

    private static void RollbackFailure()
    {
        using var memory = new PatchMemory();
        var fatalCalls = 0;
        TestHarness.Throws<FatalFailure>(() => Apply(true, memory.Target, memory.Target,
            memory.Replacement, Span<byte>.Empty, 64, memory.Maps, (_, _, _) => { },
            (_, _) => throw new InjectedFailure(), (_, error) =>
            {
                fatalCalls++;
                TestHarness.True(error is AggregateException { InnerExceptions.Count: 2 });
                throw new FatalFailure();
            }));
        TestHarness.Equal(1, fatalCalls);
        TestHarness.True(memory.Bytes().SequenceEqual(memory.Original));
    }

    private static void PendingDetour()
    {
        var type = Package.GetType("MonoMod.Core.Platforms.SimpleNativeDetour", throwOnError: true)!;
        var info = Activator.CreateInstance(Package.GetType("MonoMod.Core.Platforms.NativeDetourInfo", throwOnError: true)!)!;
        var pending = (IDisposable)Activator.CreateInstance(type,
            BindingFlags.Instance | BindingFlags.NonPublic, binder: null,
            args: [null, info, Memory<byte>.Empty, null, false], culture: null)!;
        // A null platform makes any accidental attempt to restore executable bytes fail.
        pending.Dispose();
    }

    private static void Fatal(string message, Exception error) => throw new InvalidOperationException(message, error);
    private sealed class InjectedFailure : Exception;
    private sealed class FatalFailure : Exception;

    private sealed class PatchMemory : IDisposable
    {
        private readonly nint allocation = Marshal.AllocHGlobal(256);
        internal nint Base { get; }
        internal nint Target => Base + 56;
        internal byte[] Original { get; } = Enumerable.Range(0, 16).Select(x => (byte)x).ToArray();
        internal byte[] Replacement { get; } = Enumerable.Repeat((byte)0xa5, 16).ToArray();
        internal byte[] Backup { get; } = new byte[16];
        internal string[] Maps => [Map(0, 64, "r-xp"), Map(64, 128, "rwxp")];
        internal PatchMemory()
        {
            Base = (allocation + 63) & ~(nint)63;
            Marshal.Copy(Original, 0, Target, Original.Length);
        }
        internal string Map(int start, int end, string permissions) => $"{Base + start:x}-{Base + end:x} {permissions} 0 00:00 0";
        internal byte[] Bytes()
        {
            var bytes = new byte[16];
            Marshal.Copy(Target, bytes, 0, bytes.Length);
            return bytes;
        }
        internal void Run(string[] maps, Action<nint, nint, int> protect, Action<nint, nint> flush, bool executable = true)
            => Apply(executable, Target, Target, Replacement, Backup, 64, maps, protect, flush, Fatal);
        public void Dispose() => Marshal.FreeHGlobal(allocation);
    }
}
