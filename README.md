# JunimoGate

JunimoGate is an original scaffold for a .NET 9 Android launcher architecture that discovers a legally installed Stardew Valley package, prepares a private workspace, and will eventually host an Android SMAPI fork. This repository contains no copied GPL implementation and no commercial game DLLs or assets. Its AssemblyStore format support is an attributed adaptation of the official MIT-licensed `dotnet/android` reader semantics.

The research baseline is maintained outside this independent repository in [`../ARCHITECTURE_PLAN.md`](../ARCHITECTURE_PLAN.md) and [`../TECHNICAL_FINDINGS.md`](../TECHNICAL_FINDINGS.md). These are documentation links, not runtime dependencies or symlinks.

## Current status

The host filter, synthetic extraction tests, read-only game inspector, and platform-neutral RuntimeProbe implementation tests build in the current environment:

```bash
./build/test-host.sh
```

The full host suite currently passes 37/37 tests. The ten RuntimeProbe hard cases pass on host CoreCLR, which verifies the probe implementation. The actual Android decision comes from the physical-device reports described below.

The inspector identifies APK roles by ZIP contents rather than split filenames. It can inventory AssemblyStore v2 entries, extract managed assembly images supplied by the user, and inspect .NET metadata without decompiling source or extracting `assets/Content/`:

```bash
# JSON inventory goes to stdout.
dotnet run --project tools/JunimoGate.GameInspector -- \
  inventory \
  "../Stardew Valley_1.6.15.3/base.apk" \
  "../Stardew Valley_1.6.15.3/split_config.arm64_v8a.apk" \
  "../Stardew Valley_1.6.15.3/split_content.apk" \
  > /tmp/junimogate-inventory.json

# Keep extracted commercial assemblies outside this repository.
dotnet run --project tools/JunimoGate.GameInspector -- \
  extract-assemblies /tmp/junimogate-inspector-check \
  "../Stardew Valley_1.6.15.3/base.apk" \
  "../Stardew Valley_1.6.15.3/split_config.arm64_v8a.apk" \
  "../Stardew Valley_1.6.15.3/split_content.apk"

dotnet run --project tools/JunimoGate.GameInspector -- \
  inspect-assemblies /tmp/junimogate-inspector-check \
  > /tmp/junimogate-assembly-inspection.json
```

When local policy permits it, `../Stardew Valley_1.6.15.3/analysis/` is an alternative sibling output directory. Never copy that output into `JunimoGate/`.

M1 now has a reproducible project-local Android toolchain: .NET SDK 9.0.118, Android workload 35.0.7, Temurin JDK 17, Android API 35/build-tools 35.0.0, project-local NuGet/cache/temp state, and environment provenance reporting. `JunimoGate.App` and `JunimoGate.RuntimeProbe` build as ARM64 Debug and Release APKs with 0 warnings/errors, and all four artifacts pass package/API/ABI/signature/commercial-payload static verification.

M2 is complete. On a ARM64 test device running Android 16/API 36, the final ARM64 Debug and Release probes each passed all ten hard cases in explicit stock Mono JIT mode with interpreter and AOT disabled. The selected outcome is `stock-runtime-passed-with-harmony-monomod-fix`: JunimoGate keeps the stock .NET Android Mono runtime, uses the reproducible `Lib.Harmony 2.4.2-junimogate.11` source patch plus its ARM64 cache helper, and does **not** maintain a custom runtime. See [`docs/runtime-probe-result.md`](docs/runtime-probe-result.md).

```bash
./build/bootstrap-android.sh
./build/build-android.sh Debug all
./build/build-android.sh Release all
./build/verify-android-artifacts.sh

# Requires a real ARM64 device and produces the actual M2 decision reports.
./build/verify-runtime-probe.sh
```

## Boundaries

- `App -> Android, Mods, GameHost`
- `GameHost -> Core, Android, Extraction, Rewriter`
- `Mods -> Core`
- `Extraction -> Core`
- `Rewriter -> Core`

UI and Mods do not reference game assemblies. Extraction uses the pinned `K4os.Compression.LZ4` 1.3.8 package only for bounded XALZ block decoding. The inspector does not extract commercial Content assets and does not decompile game source. Mod installation transaction interfaces remain boundaries; the scaffold does not claim a completed Mod installer. The current assembly extraction helper atomically publishes each completed DLL, but whole-workspace atomic switching remains Phase 1 work.

## Distribution and licensing

Do not commit or distribute Stardew Valley assemblies, assets, APKs, native libraries, decompiled source, inspector output, or other commercial game material. Users must supply a legally installed game on their own device. Keep all local extraction and inspection output in `/tmp`, `artifacts/`, `local-game/`, or an explicitly permitted sibling analysis directory, never in tracked source paths.

The JunimoGate license is not yet decided. [`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md) records the MIT-licensed `dotnet/android` AssemblyStore format baseline and the LZ4 dependency without applying MIT to this repository as a whole. Before incorporating or distributing SMAPI, MonoMod, Harmony, Mono.Cecil, AssemblyStore code, or code informed by existing Android loaders, perform and document the applicable GPL/LGPL and third-party license compliance assessment. This notice is an engineering reminder, not legal advice.

See [`docs/implementation-milestones.md`](docs/implementation-milestones.md), [`docs/runtime-probe-result.md`](docs/runtime-probe-result.md), [`docs/harmony-android-maintenance.md`](docs/harmony-android-maintenance.md), [`docs/startup-chain.md`](docs/startup-chain.md), [`docs/compatibility.md`](docs/compatibility.md), and [`docs/build-environment.md`](docs/build-environment.md).
