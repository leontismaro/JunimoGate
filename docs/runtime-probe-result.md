# RuntimeProbe result

## Decision

Phase 0 selected the second permitted outcome:

> **The .NET 9 Android Mono runtime is used as an input and a small application-local access-policy patch is applied. JunimoGate does not maintain a separate Mono source tree or full runtime fork.**

The validated report conclusion is:

```text
application-local-mono-with-harmony-monomod-fix
```

This is not a claim that unmodified upstream Harmony 2.4.2 works on Android. The build starts from the `libmonosgen-2.0.so` supplied by the local .NET Android runtime pack, patches the two Mono access-decision functions by ELF symbol location, and packages that copy with the App and RuntimeProbe. The SDK pack is not changed. JunimoGate also uses its reproducible `Lib.Harmony 2.4.2-junimogate.11` package and ARM64 instruction-cache helper.

## Validation environment

| Field | Value |
|---|---|
| Device | ARM64 test device |
| Serial used for local validation | `device-redacted` |
| Android | 16 / API 36 |
| Device ABI | `arm64-v8a` |
| Probe RID | `android-arm64` |
| Runtime input | `.NET 9.0.17` Android ARM64 runtime pack |
| Runtime in APK | Application-local Mono copy with two access-policy functions patched |
| Execution mode | JIT, interpreter disabled, AOT disabled |
| Harmony assembly version | `2.4.2.0` |
| Harmony informational version | `2.4.2.0-junimogate.11` |
| Harmony MVID | `b76a782a-f989-4f48-ae6f-8024a1488f5c` |
| MonoMod.Utils | `25.0.9+7df1f44bf` |

The RuntimeProbe project enforces:

```text
UseInterpreter=false
AndroidUseInterpreter=false
RunAOTCompilation=false
```

This is required because .NET for Android Debug defaults to interpreter mode. Interpreter/LLVM-only function descriptors have different entry semantics and are not the GameHost JIT configuration validated by this gate.

## Successful reports

The raw JSON and logcat files remain in ignored `artifacts/runtime-probe/` and are not committed. Their hashes provide a stable local evidence index.

| Configuration | Report | SHA-256 | Duration | Result |
|---|---|---|---:|---|
| Debug | `device-redacted-debug-20260724T055502Z.json` | `e707b71eda4732b0e7798b8b9f8e20570f8679df4d006fd251c569f6405b5a3c` | 1522.9506 ms | Passed |
| Release | `device-redacted-release-20260724T055648Z.json` | `75a33e8a82c50cf9fde7f95cbe456481474412183d6f44326c6e55bbf20c3860` | 1491.5530 ms | Passed |

Corresponding logcat SHA-256 values:

- Debug: `904820da95837333d9c7af6cc7c27fbf082639ba20a72697f35266d811b2c682`;
- Release: `f8e3ff6083979249e43a7ba73741a650b2226f4f00e7b4a13d12bbd64094674f`.

Both reports contain the same ten passing hard cases:

1. runtime dynamic-code generation and execution;
2. patched Harmony/MonoMod Android platform initialization;
3. managed Mono JIT entry-point validation;
4. native ARM64 instruction-cache helper self-test;
5. cross-assembly private method Prefix/Postfix;
6. cross-assembly private field by-ref injection and write-back;
7. private-access IL transpiler with observable behavior change;
8. SMAPI-style `CheckStorageMigration` Prefix which skips the original and returns `false`;
9. MonoMod DMD `dynamicmethod` private access;
10. MonoMod DMD `cecil` private access and `IgnoresAccessChecksToAttribute` validation.

## Reproducible Harmony package

The generated package is intentionally ignored, but all source inputs and the patch are tracked or pinned. The operational maintenance and upstream-upgrade procedure is in [`harmony-android-maintenance.md`](harmony-android-maintenance.md).

```text
patches/harmony-android/harmony-2.4.2-android.patch
build/harmony-android-versions.sh
build/build-harmony-android.sh
```

Final artifact identities:

| Artifact | SHA-256 |
|---|---|
| `Lib.Harmony.2.4.2-junimogate.11.nupkg` | `a476d0a4d1b2cdfe47414225ea1e547ecb21ac0dddaa8a1e412a1673ffb66ac4` |
| package `lib/net9.0/0Harmony.dll` | `240ec869c07564ec12fc212103ccbf642ee547c49d55f3db21e71bcdc9cf07a3` |
| tracked Android patch | `cfee9e3088008a2f434ae2b01a9f695668ba05c7df61a4ed5cba796aff5f95f6` |

Pinned sources:

- Harmony commit `a264a1bf1ce689e4589e8dcc54b1e2818602a90a`;
- pardeike/MonoMod commit `dfc30a1506d37fb88a2c2be004f525205f46a24c`;
- iced commit `c50f29b7bc305696895c075f3fc7719751426b12`.

The patch supplies Android bionic library/errno/page-size handling, Android-to-Linux platform selection, real Mono JIT entry resolution through `mono_compile_method`, mapping-aware patch writes, tagged-pointer-safe syscall addresses, and the cache-helper call.

## ARM64 cache helper

`native/JunimoGate.CacheFlush/clear_cache.S` is built reproducibly with pinned project-local Zig 0.14.1. It has no libc dependency, no relocations, one exported function, and exact 16 KiB ELF segment alignment.

Validated helper SHA-256:

```text
cea720cf68436ce7dfb4662cf0eef3594aaebd15ff9d98a0ab237329d503c96d
```

The device self-test executed generated code returning 41, modified it while the old instruction remained visible, invoked `junimogate_clear_cache`, and then observed 73. This prevents a false Harmony pass caused by an unverified native helper.
