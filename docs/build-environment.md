# Build environment

JunimoGate uses C# and .NET for Android. JDK 17 is a build-tool dependency used by the Android SDK for Java interop stubs, DEX conversion, packaging, and signing; application and probe code remain C#.

`global.json` requests SDK `9.0.100` with `rollForward: latestFeature`. The reproducible local toolchain currently pins SDK `9.0.118`.

## Host prerequisites

The maintained scripts target a 64-bit Linux host with Bash. A fresh checkout
needs `git`, `curl`, `unzip`, `tar`, `xz`, `python3`, and standard checksum and
file utilities. The bootstrap installs the pinned .NET SDK, JDK, Android SDK,
and Android workload below the repository; system-wide Android or .NET
installations are not required.

Clone submodules before building:

```bash
git submodule update --init --recursive
```

## Current project-local toolchain

The bootstrap installs into the ignored `.toolchains/` directory and does not modify `/usr/lib64/dotnet`, the system JDK, or global .NET runtime packs.

| Component | Pinned/installed version |
|---|---|
| .NET SDK | 9.0.118 |
| .NET Android workload | android 35.0.7 / manifest 9.0.100 |
| JDK | Eclipse Temurin 17.0.16+8 |
| Android command-line tools | 19.0 |
| Android platform | API 35 |
| Android build-tools | 35.0.0 |
| Target runtime identifier | `android-arm64` |

Tool, NuGet, HTTP cache, workload temporary files, Android user state, and .NET CLI state are redirected below `.toolchains/`. `DOTNET_GENERATE_ASPNET_CERTIFICATE=false`, workload update notifications are disabled, and MSBuild node reuse is disabled for the scripted environment.

## Bootstrap

The pinned download URLs and checksums are in:

```text
build/android-toolchain-versions.sh
```

Install or repair the project-local toolchain:

```bash
./build/bootstrap-android.sh
```

An optional HTTP/SOCKS proxy can be supplied without committing machine-specific configuration:

```bash
JUNIMOGATE_PROXY_URL=http://127.0.0.1:10808 \
  nice -n 10 ionice -c2 -n7 \
  ./build/bootstrap-android.sh
```

The bootstrap supports resumed downloads, verifies the pinned archive hashes, accepts Android SDK licenses only for the project-local SDK, and installs the Android workload with manifest updates and parallel package installation disabled.

## Build script reference

The supported shell entry points under `build/` are:

| Script | Purpose and typical use |
|---|---|
| `bootstrap-android.sh` | Install or repair the pinned repository-local Android toolchain. |
| `android-env.sh` | Source in an interactive shell to select the local .NET, JDK, Android SDK, NuGet feed, and caches. |
| `install-android-workload.sh` | Retry only the .NET Android workload installation after bootstrap or network failure. |
| `build-monogame-android.sh` | Build the pinned public-source MonoGame Android package and verify its managed/native payload. Run once after bootstrap and whenever its pinned inputs change. |
| `build-harmony-android.sh` | Build the pinned Harmony/MonoMod Android package. `build-android.sh` and `test-host.sh` invoke it when needed. |
| `build-mono-android.sh` | Prepare the bounded application-local Mono runtime copy. It is normally invoked by `build-android.sh`. |
| `build-cacheflush.sh` | Build the ARM64 instruction-cache helper. `build-android.sh` invokes it for Android targets. |
| `build-android.sh <Debug|Release> <app|probe|all>` | Build the selected Android artifact set. |
| `build-production-apk.sh [--sign-only]` | Build and sign the ARM64 Release APK with the pinned JunimoGate release certificate, or sign the current unsigned Release APK. |
| `test-host.sh` | Build the host solution filter and run platform-neutral automated tests. |
| `report-android-environment.sh` | Write an auditable ignored report of tool versions, paths, hashes, SDK packages, and connected devices. |
| `verify-android-artifacts.sh` | Check package identity, SDK/ABI/signature properties, and excluded commercial payloads in built APKs. |
| `run-runtime-probe.sh <Debug|Release>` | Build, install, run, and collect one runtime-probe configuration from a connected device. |
| `verify-runtime-probe.sh` | Run both Debug and Release runtime-probe configurations. |
| `verify-game-discovery.sh` | Exercise installed-game discovery on a connected development device. |
| `verify-game-workspace.sh` | Exercise workspace preparation on a connected development device. |
| `package-openal-corresponding-source.sh` | Generate the OpenAL corresponding-source archive and checksum required beside a binary release. |

Files named `*-versions.sh`, MSBuild `.targets` files, and Python files in this
directory are pinned configuration or implementations called by these public
entry points; they are not separate user workflows.

To retry only the workload step:

```bash
JUNIMOGATE_PROXY_URL=http://127.0.0.1:10808 \
  nice -n 10 ionice -c2 -n7 \
  ./build/install-android-workload.sh
```

The workload installer checks the proxy/feed first and uses a private project-local temporary directory.

Activate the environment in an interactive shell:

```bash
source ./build/android-env.sh
```

Generate an auditable local environment report:

```bash
./build/report-android-environment.sh
```

The ignored report is written to `artifacts/android/environment.json` and records expected versions, executable paths/hashes, installed workload and SDK package output, plus current adb device state.

## Host build

The host filter excludes Android targets but includes the platform-neutral RuntimeProbe Core/Target and host probe tests:

```bash
./build/test-host.sh
```

The script prefers the project-local .NET SDK when present, builds `JunimoGate.Host.slnf`, and runs:

- Core tests;
- Extraction tests;
- Rewriter tests;
- Mods tests;
- RuntimeProbe host implementation tests.

RuntimeProbe host tests run the same ten hard cases on CoreCLR. Passing them verifies the probe implementation; the Android compatibility decision is based on the physical-device reports in [`runtime-probe-result.md`](runtime-probe-result.md).

Extraction tests use synthetic legacy AssemblyStore v1, ELF64-wrapped AssemblyStore v2, APK ZIP, MZ, and XALZ fixtures only; no game material is present.

## Patched Harmony and cache helper

RuntimeProbe and GameHost use the generated `Lib.Harmony 2.4.2-junimogate.63` package from the ignored local NuGet feed. The package is rebuilt from pinned Harmony, MonoMod, and iced source archives plus the tracked patch whenever it is absent or stale:

```bash
./build/build-harmony-android.sh
```

The source inputs are pinned in `build/harmony-android-versions.sh`; the patch is `patches/harmony-android/harmony-2.4.2-android.patch`; generated package provenance is written beside the ignored package under `artifacts/nuget/`. Normal use, patch regeneration, upstream upgrades, conflict handling, rollback, and script troubleshooting are documented in [`harmony-android-maintenance.md`](harmony-android-maintenance.md).

RuntimeProbe also packages an ARM64 no-libc instruction-cache helper built from `native/JunimoGate.CacheFlush/clear_cache.S` with pinned project-local Zig 0.14.1:

```bash
./build/build-cacheflush.sh
```

`build-android.sh` invokes both prerequisite builders automatically for probe targets.

## Android builds

Build the ARM64 RuntimeProbe and Launcher scaffold:

```bash
export JUNIMOGATE_GAME_REFERENCE_DIR="/absolute/path/to/legally-extracted/game/assemblies"
```

The path is required for compile-time references only. It must be absolute; the build does not
search for game files or copy them into the repository or final APK.

```bash
./build/build-android.sh Debug probe
./build/build-android.sh Release probe
./build/build-android.sh Debug app
./build/build-android.sh Release app
```

Or build both projects for one configuration:

```bash
./build/build-android.sh Debug all
```

Current artifacts are selected only from the RID-specific path:

```text
bin/<Configuration>/net9.0-android35.0/android-arm64/
```

This avoids accidentally selecting stale non-RID/multi-ABI APKs left in ignored `bin/` caches.

Validate all four signed artifacts:

```bash
./build/verify-android-artifacts.sh
```

The verifier checks:

- exact application ID and launcher Activity;
- compile/target SDK 35 and minimum SDK 26;
- `arm64-v8a` as the only packaged native ABI;
- expected debuggable state;
- APK Signature Scheme v2/v3;
- signer certificate digest;
- absence of known commercial game payload markers.

It writes `artifacts/android/apk-verification.json`. Current builds use an Android development certificate and are not production releases.

## Production signing

Normal `build-android.sh` invocations always retain the Android development
certificate. They need no private signing input and remain suitable for local
builds, automated checks, and device development.

The maintainer-only production path receives the protected PKCS12 keystore
location through a local environment variable. The repository does not define
or record where that file is stored:

```bash
export JUNIMOGATE_RELEASE_KEYSTORE="/path/to/junimogate-release.p12"
```

Keep the file outside the repository. With the variable set in the current
shell, build and sign in one operation:

```bash
./build/build-production-apk.sh
```

The script builds the unsigned ARM64 Release APK before asking for the PKCS12
password. Password input is hidden and is passed only to `keytool` and
`apksigner` through a process environment reference; it is not placed in a
command argument or build log. The script rejects a keystore whose certificate
does not match the pinned JunimoGate release fingerprint, then verifies the v2
and v3 signatures and certificate on the completed APK. A version name carrying
the `-dev` suffix is rejected before signing.

The public release-certificate SHA-256 fingerprint is:

```text
FA:26:F2:F1:83:F4:BE:61:62:04:23:BF:55:85:81:4A:
63:AF:05:CA:ED:F6:E3:3D:D7:74:25:A4:28:C8:10:36
```

When the unsigned Release APK was already built, the private-key holder can do
only the final signing step:

```bash
./build/build-production-apk.sh --sign-only
```

`JUNIMOGATE_RELEASE_KEYSTORE` is required for both modes and should be defined
only in the maintainer's local shell or protected CI configuration. CI may
provide `JUNIMOGATE_RELEASE_KEYSTORE_PASSWORD` through a protected secret; do
not place the password in shell history, tracked files, workflow arguments, or
logs. Output is written under ignored `artifacts/release/` with a SHA-256
checksum. This command does not create a tag, GitHub Release, or upload.

## RuntimeProbe device gate

A real ARM64 Android device is required for the actual M2 conclusion:

```bash
adb devices -l
./build/verify-runtime-probe.sh
```

The script builds, installs, clears old probe-app state, launches each configuration, waits for app-private JSON, collects it through `adb run-as`, captures logcat, and accepts either a direct application-local Mono pass or the application-local Mono plus Harmony/MonoMod fix conclusion.

Set `ANDROID_SERIAL` when multiple devices are attached. Output is stored under ignored `artifacts/runtime-probe/`.

M1 and M2 are complete. A ARM64 test device running Android 16/API 36 installed and launched the probe, and final Debug and Release reports passed all ten hard cases in explicit application-local Mono JIT/no-interpreter/no-AOT mode. The runtime input is the project-local .NET Android pack; the ignored APK copy receives only the bounded recipes documented in [`mono-android-runtime-maintenance.md`](mono-android-runtime-maintenance.md). The pinned Harmony/MonoMod library patch and cache helper remain separate required inputs. See [`runtime-probe-result.md`](runtime-probe-result.md) for report hashes and scope.

## Local game inspection

Run the inspector only against APKs obtained legally by the user:

```bash
dotnet run --project tools/JunimoGate.GameInspector -- inventory <apk...>
dotnet run --project tools/JunimoGate.GameInspector -- extract-assemblies /tmp/junimogate-inspector-check <apk...>
dotnet run --project tools/JunimoGate.GameInspector -- inspect-assemblies /tmp/junimogate-inspector-check
```

`inventory` reports APK SHA-256/size, content-derived roles, legacy v1 or modern v2 AssemblyStore entries, and runtime native libraries. `extract-assemblies` reads either the exact legacy `assemblies/assemblies.blob` + selected-ABI blob + manifest set, or matching modern `lib/<abi>/libassemblies.<abi>.blob.so` entries. Both paths refuse malformed bounds, unsafe/duplicate names, and overwrites. `inspect-assemblies` reads metadata identities, target frameworks, assembly/module references, and P/Invoke declarations; it does not decompile method bodies or source.

Never write or copy APKs, game assets, DLLs, native libraries, decompiled source, or generated manifests into tracked repository paths. Use `/tmp` or ignored `artifacts/` and `local-game/` directories.

## Runtime boundary

M2 uses the project-local .NET Android Mono runtime pack as an input, then creates an ignored application-local ARM64 copy with narrow symbol- and instruction-guarded patches. `build/build-mono-android.sh` performs this once before an APK build; it does not alter the global pack and does not copy the original game's AOT images. Harmony/MonoMod remains a separate pinned library patch. The exact recipes and runtime-upgrade procedure are in [`mono-android-runtime-maintenance.md`](mono-android-runtime-maintenance.md).

Related architecture and validation documents are indexed in [`README.md`](README.md).
