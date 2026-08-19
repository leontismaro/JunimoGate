# RuntimeProbe

`JunimoGate.RuntimeProbe` is the Android executable used to exercise representative Harmony and MonoMod behavior with the same application-local Mono runtime as GameHost. The runtime is derived from the project-local .NET 9 Android ARM64 runtime pack during the build; the SDK pack itself is never modified.

A successful host run proves that the probe implementation works on CoreCLR. The Android conclusion comes from the physical-device reports indexed in [`../../docs/runtime-probe-result.md`](../../docs/runtime-probe-result.md).

## Projects

- `JunimoGate.RuntimeProbe.Target`: separate `net9.0` assembly containing private methods and fields, preventing same-assembly false positives.
- `JunimoGate.RuntimeProbe.Core`: platform-neutral sequential runner, report contracts, diagnostics, patched Lib.Harmony integration, and MonoMod.Utils 25.0.9.
- `JunimoGate.RuntimeProbe`: `net9.0-android35.0`, ARM64-only Android UI, native cache helper, and app-private report writer.
- `JunimoGate.RuntimeProbe.Tests`: host implementation and conclusion-logic tests.

## Execution mode

The project enforces the GameHost-relevant Mono JIT configuration:

```text
UseInterpreter=false
AndroidUseInterpreter=false
RunAOTCompilation=false
```

This is explicit because .NET for Android Debug otherwise defaults to interpreter mode. The probe build fails if these requested properties are changed. `build/build-mono-android.sh` supplies the application-local runtime before the APK is built.

## Hard cases

All ten hard cases must pass:

1. generate and execute a `DynamicMethod`;
2. initialize the pinned Harmony/MonoMod Android bionic/Linux platform fix;
3. verify that Mono method handles resolve to the real JIT entry used by the patched detour backend;
4. self-test the native ARM64 instruction-cache helper with observable generated-code changes;
5. Harmony Prefix/Postfix a private method in another assembly;
6. Harmony field injection read and write a private field in another assembly;
7. run a behavior-changing Harmony transpiler over private method/field access;
8. apply an SMAPI-style Prefix which skips `CheckStorageMigration` and returns `false`;
9. copy and invoke private-access IL through MonoMod DMD `dynamicmethod`;
10. do the same through MonoMod DMD `cecil` and validate `IgnoresAccessChecksToAttribute`.

Reflection is used to locate targets and inspect generated metadata, but reflection-only field access is never accepted as compatibility proof. The JIT entry and cache-helper cases are safety gates: Harmony writes are blocked if either diagnostic fails.

## Conclusion

The runtime selection is:

```text
application-local-mono-with-harmony-monomod-fix
```

This means:

- start from the .NET for Android `libmonosgen-2.0.so` in the local runtime pack;
- apply the versioned application-local Mono recipes documented in [`../../docs/mono-android-runtime-maintenance.md`](../../docs/mono-android-runtime-maintenance.md);
- use generated `Lib.Harmony 2.4.2-junimogate.64`;
- package `libjunimogate-cacheflush.so` for ARM64;
- do not modify or redistribute the complete SDK/runtime pack.

The result is limited to the ten runtime diagnostics listed above. The probe does not start MonoGame, SMAPI, the game host, or a Mod.

## Report

The Activity runs off the UI thread and atomically publishes:

```text
files/runtime-probe-report.json
```

Reports include runtime/device/build identity, JIT/interpreter/AOT intent, assembly versions and MVIDs, every case result and detail, and complete exception chains. The internal Release probe remains debuggable only so `adb run-as` can collect app-private output; it is not a product release configuration and requests no storage permission.

## Build and run

From the repository root:

```bash
./build/build-harmony-android.sh
./build/build-cacheflush.sh
./build/build-android.sh Debug probe
./build/build-android.sh Release probe
./build/verify-android-artifacts.sh

adb devices -l
./build/verify-runtime-probe.sh
```

`build-android.sh` automatically ensures the patched Harmony package and cache helper exist. Set `ANDROID_SERIAL` when multiple devices are attached. Reports and logcat are copied to ignored `artifacts/runtime-probe/`.

Rerun both configurations whenever .NET Android, Harmony, MonoMod, the cache helper, ABI, trimming policy, or runtime identity changes.
