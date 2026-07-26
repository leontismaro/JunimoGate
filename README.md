# JunimoGate

JunimoGate is an original scaffold for a .NET 9 Android launcher architecture that discovers a legally installed Stardew Valley package, prepares a private workspace, and will eventually host an Android SMAPI fork. This repository contains no copied GPL implementation and no commercial game DLLs or assets. Its AssemblyStore format support is an attributed adaptation of the official MIT-licensed `dotnet/android` reader semantics.

The research baseline is maintained outside this independent repository in [`../ARCHITECTURE_PLAN.md`](../ARCHITECTURE_PLAN.md) and [`../TECHNICAL_FINDINGS.md`](../TECHNICAL_FINDINGS.md). These are documentation links, not runtime dependencies or symlinks.

## Current status

The host filter, synthetic extraction tests, read-only game inspector, and platform-neutral RuntimeProbe implementation tests build in the current environment:

```bash
./build/test-host.sh
```

The full host suite currently passes 129/129 tests: Core 12, Extraction 55, Rewriter 51, Mods 6, and RuntimeProbe 5. One of the five RuntimeProbe host tests executes and reports all ten hard cases on CoreCLR; those ten case results are not ten additional host-suite tests. The actual Android runtime decision comes from the physical-device reports described below.

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

M1 now has a reproducible project-local Android toolchain: .NET SDK 9.0.118, Android workload 35.0.7, Temurin JDK 17, Android API 35/build-tools 35.0.0, project-local NuGet/cache/temp state, and environment provenance reporting. `JunimoGate.App` and `JunimoGate.RuntimeProbe` build as ARM64 Debug and Release APKs with 0 warnings/errors, and all four artifacts pass package/API/ABI/signature/commercial-payload static verification. Static verification also requires the App's final merged manifest to contain exactly the two supported game package queries and requires every artifact to omit `QUERY_ALL_PACKAGES` and broad storage permissions.

M2 is complete. On a ARM64 test device running Android 16/API 36, the final ARM64 Debug and Release probes each passed all ten hard cases in explicit stock Mono JIT mode with interpreter and AOT disabled. The selected outcome is `stock-runtime-passed-with-harmony-monomod-fix`: JunimoGate keeps the stock .NET Android Mono runtime, uses the reproducible `Lib.Harmony 2.4.2-junimogate.11` source patch plus its ARM64 cache helper, and does **not** maintain a custom runtime. See [`docs/runtime-probe-result.md`](docs/runtime-probe-result.md).

M3 is complete for the current Google Play scope. On a ARM64 test device running API 36, an installed `com.chucklefish.stardewvalley` 1.6.15.3/versionCode 245 base + ARM64 + Content set produced a redacted report with a complete signer identity, three matching APK hashes, `arm64-v8a` AssemblyStore evidence, and the Content role. The tested Play signing-certificate anchor is evaluated as `KnownTested`; an Android-verified single-signer rotation lineage may produce `KnownTestedAfterRotation`, while unrelated or multi-signer identities remain non-executable. Report format 2 exposes this as `gameCertificateStatus`, `allowsCodeExecution`, and the matched tested certificate without claiming independent publisher certification. API 26–27 support and its legacy signing/version branch remain declared but await future device evidence. Galaxy and dual-package acceptance are deferred compatibility work, not current M3 blockers. See [`docs/game-discovery-result.md`](docs/game-discovery-result.md).

M4's extraction and atomic-workspace capability is complete for the tested Play scope. The historical implementation fully re-hashes CacheHit payloads, but that behavior is now classified as Deep Prepare/acceptance work rather than a normal-launch requirement. The product path must reuse an unchanged active workspace through Fast Launch and enter Deep Prepare only on first import, game update, schema change, explicit repair, or detected damage. See [`docs/game-workspace-result.md`](docs/game-workspace-result.md) for historical evidence and [`docs/m5-implementation-plan.md`](docs/m5-implementation-plan.md) for the corrected path.


The PoC used exact recipe `play-1.6.15.3-gamehost-bridge@1`, complete-method hashes, fixed global counts, repeated trust validation and a three-file applied workspace. Those mechanisms are retained only as golden evidence for the tested build. They are not the product compatibility model and must not be extended version-by-version.


```bash
./build/bootstrap-android.sh

# Rebuilds the exact public-source MonoGame Android provider into the ignored local NuGet feed.
# JUNIMOGATE_PROXY_URL is optional and is never stored in repository configuration.
JUNIMOGATE_PROXY_URL=http://127.0.0.1:10808 ./build/build-monogame-android.sh

./build/build-android.sh Debug all
./build/build-android.sh Release all
./build/verify-android-artifacts.sh

# Requires a real ARM64 device and produces the actual M2 decision reports.
./build/verify-runtime-probe.sh

# Requires one online ARM64 device. It verifies either the explicit missing-package
# branch or, when a target game is installed, the complete candidate report.
./build/verify-game-discovery.sh Debug

# Requires one online ARM64 API 26+ device with the tested Play installation.
# Verifies interrupted staging recovery, Built, CacheHit, manifests, hashes,
# report v2 metrics/progress, state, redaction, and App-PID logcat.
./build/verify-game-workspace.sh

# Requires the tested Play installation on one ARM64 device. Runs the metadata-only
# Gate 0 probe twice, independently recomputes evidence keys, and copies only redacted JSON.
./build/verify-gamehost-probe.sh
```

## Boundaries

- `App -> Android, Mods, GameHost`
- `GameHost -> Core, Android, Extraction, Rewriter`
- `Mods -> Core`
- `Extraction -> Core`
- `Rewriter -> Core, Extraction`

UI and Mods do not reference game assemblies. Extraction uses the pinned `K4os.Compression.LZ4` 1.3.8 package only for bounded XALZ block decoding. The inspector does not extract commercial Content assets and does not decompile game source. Mod installation transaction interfaces remain boundaries; the scaffold does not claim a completed Mod installer. M4 keeps source extraction transactional and immutable; M5 keeps rewritten overlays separate. Product compatibility is based on local semantic rewrite guards and independent postconditions, not one exact support key, fixed MVID, complete-method hashes or global call counts. Full payload validation belongs to Deep Prepare and explicit verification, not every launch.

## Distribution and licensing

Do not commit or distribute Stardew Valley assemblies, assets, APKs, native libraries, decompiled source, inspector output, or other commercial game material. Users must supply a legally installed game on their own device. Keep all local extraction and inspection output in `/tmp`, `artifacts/`, `local-game/`, or an explicitly permitted sibling analysis directory, never in tracked source paths.

The JunimoGate license is not yet decided. [`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md) records the MIT-licensed `dotnet/android` AssemblyStore baseline, LZ4, Cecil/Harmony dependencies, and the exact public MonoGame provider's Ms-PL/MIT, OpenAL GNU Library GPL v2, and Stb public-domain notices. The provider build includes the tracked notices in its ignored local package. Public distribution remains blocked until M10 establishes exact corresponding-source and relink/replacement compliance for the pinned OpenAL binary. These notices do not apply any third-party license to JunimoGate as a whole, and this is an engineering reminder rather than legal advice.

See [`AGENTS.md`](AGENTS.md), [`docs/implementation-milestones.md`](docs/implementation-milestones.md), [`docs/m5-implementation-plan.md`](docs/m5-implementation-plan.md), [`docs/gamehost-activity-bridge-result.md`](docs/gamehost-activity-bridge-result.md), [`docs/gamehost-probe-result.md`](docs/gamehost-probe-result.md), [`docs/gamehost-gate2-contracts.md`](docs/gamehost-gate2-contracts.md), [`docs/game-discovery-result.md`](docs/game-discovery-result.md), [`docs/game-workspace-result.md`](docs/game-workspace-result.md), [`docs/runtime-probe-result.md`](docs/runtime-probe-result.md), [`docs/harmony-android-maintenance.md`](docs/harmony-android-maintenance.md), [`docs/startup-chain.md`](docs/startup-chain.md), [`docs/compatibility.md`](docs/compatibility.md), and [`docs/build-environment.md`](docs/build-environment.md).
