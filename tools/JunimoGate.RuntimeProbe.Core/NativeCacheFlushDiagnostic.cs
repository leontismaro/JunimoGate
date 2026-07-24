using System.Runtime.InteropServices;
using MonoMod.Utils;

namespace JunimoGate.RuntimeProbe.Core;

internal static class NativeCacheFlushDiagnostic
{
    private const int ProtectionReadWriteExecute = 0x01 | 0x02 | 0x04;
    private const int MapPrivateAnonymous = 0x02 | 0x20;
    private const int MapFailed = -1;
    private const int TestPageSize = 4096;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr MmapDelegate(
        IntPtr address,
        nuint length,
        int protection,
        int flags,
        int fileDescriptor,
        nint offset);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int MunmapDelegate(IntPtr address, nuint length);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ClearCacheDelegate(IntPtr start, IntPtr end);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NativeConstantDelegate();

    public static bool HarmonyWritesAllowed { get; private set; } = true;

    public static IReadOnlyDictionary<string, string> Inspect()
    {
        var details = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["detectedOperatingSystem"] = PlatformDetection.OS.ToString(),
            ["processArchitecture"] = RuntimeInformation.ProcessArchitecture.ToString(),
        };

        if (PlatformDetection.OS != OSKind.Android
            || RuntimeInformation.ProcessArchitecture != Architecture.Arm64)
        {
            HarmonyWritesAllowed = true;
            details["diagnosticRequired"] = "false";
            details["harmonyWritesAllowed"] = "true";
            return details;
        }

        HarmonyWritesAllowed = false;
        details["diagnosticRequired"] = "true";
        details["harmonyWritesAllowed"] = "false";

        var libc = NativeLibrary.Load("libc.so");
        var helper = NativeLibrary.Load("libjunimogate-cacheflush.so");
        details["libcLoaded"] = "true";
        details["cacheHelperLoaded"] = "true";

        var mmap = Marshal.GetDelegateForFunctionPointer<MmapDelegate>(
            NativeLibrary.GetExport(libc, "mmap"));
        var munmap = Marshal.GetDelegateForFunctionPointer<MunmapDelegate>(
            NativeLibrary.GetExport(libc, "munmap"));
        var clearCache = Marshal.GetDelegateForFunctionPointer<ClearCacheDelegate>(
            NativeLibrary.GetExport(helper, "junimogate_clear_cache"));

        var page = mmap(
            IntPtr.Zero,
            TestPageSize,
            ProtectionReadWriteExecute,
            MapPrivateAnonymous,
            -1,
            0);
        if (page == IntPtr.Zero || page.ToInt64() == MapFailed)
        {
            throw new InvalidOperationException($"mmap RWX failed with address 0x{unchecked((nuint)page):x16}.");
        }

        details["testPage"] = $"0x{unchecked((nuint)page):x16}";
        details["testPageSize"] = TestPageSize.ToString();
        try
        {
            var end = IntPtr.Add(page, sizeof(uint) * 2);
            WriteReturnConstant(page, 41);
            clearCache(page, end);
            var generated = Marshal.GetDelegateForFunctionPointer<NativeConstantDelegate>(page);
            var initialResult = generated();
            details["initialResult"] = initialResult.ToString();
            if (initialResult != 41)
            {
                throw new InvalidOperationException(
                    $"Native cache-helper self-test returned {initialResult} for initial code; expected 41.");
            }

            WriteReturnConstant(page, 73);
            var resultBeforeFlush = generated();
            details["resultBeforeSecondFlush"] = resultBeforeFlush.ToString();
            clearCache(page, end);
            var resultAfterFlush = generated();
            details["resultAfterSecondFlush"] = resultAfterFlush.ToString();
            if (resultAfterFlush != 73)
            {
                throw new InvalidOperationException(
                    $"Native cache-helper self-test returned {resultAfterFlush} after flushing modified code; expected 73 (before flush: {resultBeforeFlush}).");
            }

            HarmonyWritesAllowed = true;
            details["harmonyWritesAllowed"] = "true";
            return details;
        }
        finally
        {
            details["munmapResult"] = munmap(page, TestPageSize).ToString();
            NativeLibrary.Free(helper);
            NativeLibrary.Free(libc);
        }
    }

    private static void WriteReturnConstant(IntPtr destination, int value)
    {
        if ((uint)value > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }

        // movz w0, #value; ret
        var instructions = new[]
        {
            0x52800000u | ((uint)value << 5),
            0xd65f03c0u,
        };
        var bytes = new byte[instructions.Length * sizeof(uint)];
        Buffer.BlockCopy(instructions, 0, bytes, 0, bytes.Length);
        Marshal.Copy(bytes, 0, destination, bytes.Length);
    }
}
