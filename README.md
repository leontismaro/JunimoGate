# JunimoGate

JunimoGate is a .NET 9 Android launcher that discovers a legally installed Stardew Valley package, prepares a private workspace, and hosts its Android SMAPI runtime in an isolated game process. SMAPI is pinned through the `smapi/` submodule; this repository contains no copied GPL implementation and no commercial game DLLs or assets. Its AssemblyStore format support is an attributed adaptation of the official MIT-licensed `dotnet/android` reader semantics.

The research baseline is maintained outside this independent repository in [`../ARCHITECTURE_PLAN.md`](../ARCHITECTURE_PLAN.md) and [`../TECHNICAL_FINDINGS.md`](../TECHNICAL_FINDINGS.md). These are documentation links, not runtime dependencies or symlinks.

## Current status

The host filter, synthetic extraction tests, read-only game inspector, and platform-neutral RuntimeProbe implementation tests build in the current environment:

```bash
./build/test-host.sh
```

The full host suite currently passes 110/110 tests: Core 17, Extraction 58, Rewriter 11, Mods/Profile/SMAPI binding 19, and RuntimeProbe 5. Rewriter coverage now targets the product semantic rules and applied-cache transaction instead of the removed exact probes/catalog. One of the five RuntimeProbe host tests executes and reports all ten hard cases on CoreCLR; those ten case results are not ten additional host-suite tests. The actual Android runtime decision comes from the physical-device reports described below.

The inspector identifies APK roles by ZIP contents rather than split filenames. It can inventory and extract both legacy AssemblyStore v1 (`assemblies/*.blob`) and modern ELF-wrapped AssemblyStore v2 images supplied by the user, and inspect .NET metadata without decompiling source or extracting `assets/Content/`:

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

M2 is complete. The Android builds use JIT with interpreter and AOT disabled, deriving an application-local `libmonosgen-2.0.so` from the project-local .NET runtime pack. The bounded runtime recipe opens the two Mono member-access checks and repairs two confirmed Reflection.Emit diagnostic/fallback crashes; it does not add runtime scanning or per-frame work. Harmony uses the reproducible `Lib.Harmony 2.4.2-junimogate.63` source patch plus its ARM64 cache helper. The SDK runtime pack is never modified; see [`docs/mono-android-runtime-maintenance.md`](docs/mono-android-runtime-maintenance.md) and [`docs/runtime-probe-result.md`](docs/runtime-probe-result.md).

M3 is complete for the current Google Play scope. On a ARM64 test device running API 36, an installed `com.chucklefish.stardewvalley` 1.6.15.3/versionCode 245 base + ARM64 + Content set produced a redacted report with a complete signer identity, three matching APK hashes, `arm64-v8a` AssemblyStore evidence, and the Content role. The tested Play signing-certificate anchor is evaluated as `KnownTested`; an Android-verified single-signer rotation lineage may produce `KnownTestedAfterRotation`, while unrelated or multi-signer identities remain non-executable. Report format 2 exposes this as `gameCertificateStatus`, `allowsCodeExecution`, and the matched tested certificate without claiming independent publisher certification. API 26–27 support and its legacy signing/version branch remain declared but await future device evidence. Galaxy and dual-package acceptance are deferred compatibility work, not current M3 blockers. See [`docs/game-discovery-result.md`](docs/game-discovery-result.md).

M4's extraction and atomic-workspace capability is complete for the tested Play scope. The historical implementation fully re-hashes CacheHit payloads, but that behavior is now classified as Deep Prepare/acceptance work rather than a normal-launch requirement. The product path must reuse an unchanged active workspace through Fast Launch and enter Deep Prepare only on first import, game update, schema change, explicit repair, or detected damage. See [`docs/game-workspace-result.md`](docs/game-workspace-result.md) for historical evidence and [`docs/m5-implementation-plan.md`](docs/m5-implementation-plan.md) for the corrected path.


The PoC used exact recipe `play-1.6.15.3-gamehost-bridge@1`, complete-method hashes, fixed global counts, repeated trust validation and a three-file applied workspace. Those mechanisms are retained only as golden evidence for the tested build. They are not the product compatibility model and must not be extended version-by-version.

The M5-PoC also already established that the rewrite target set contains no licensing callbacks and that repository test assemblies contain no commercial game code. Future compatibility and launch work must not add repeated licensing-method scans, hashes, reports, gates, or duplicate synthetic proof assemblies unless a new rewrite explicitly expands into that code area.

Android SMAPI Launch Alpha is implemented for the tested Play ARM64 baselines. The APK presents a launcher, defers first preparation and recovery until an explicit user action, creates the one-time launch request only when the user taps the button, and runs SMAPI in an isolated `:game` process. The normal reuse path is single-pass: Launcher reads one snapshot, the tap reads one PackageManager snapshot, descriptor issuance rereads no snapshot, `:game` reads one snapshot and builds one pre-SMAPI runtime file inventory reused by the loader and Content bridge. It performs no APK/workspace full hash, probe or rewrite. Home plus tapping the JunimoGate icon returns the existing game session to the foreground. Back is forwarded to Stardew as one `Escape` without finishing the Activity or killing `:game`. Deep Prepare applies `stardew-android-mainactivity-bridge/v1` from local member/IL/stack rules and no longer consults an exact version catalog, fixed MVID, complete-method hashes, global call counts, or native support fingerprint. The product UI, global Mod library, groups, import/export, logs, settings and i18n roadmap is specified in [`docs/product-ui-implementation-plan.md`](docs/product-ui-implementation-plan.md). See [`AGENTS.md`](AGENTS.md), [`docs/m5-implementation-plan.md`](docs/m5-implementation-plan.md), [`docs/smapi-integration-plan.md`](docs/smapi-integration-plan.md), [`docs/productization-direction.md`](docs/productization-direction.md), [`docs/implementation-milestones.md`](docs/implementation-milestones.md), [`docs/startup-chain.md`](docs/startup-chain.md), and [`docs/compatibility.md`](docs/compatibility.md).


```bash
git clone --recurse-submodules https://github.com/leontismaro/JunimoGate.git

# For an existing checkout:
git submodule update --init --recursive

./build/bootstrap-android.sh

# Rebuilds the exact public-source MonoGame Android provider into the ignored local NuGet feed.
# JUNIMOGATE_PROXY_URL is optional and is never stored in repository configuration.
JUNIMOGATE_PROXY_URL=http://127.0.0.1:10808 ./build/build-monogame-android.sh

# Absolute local directory containing the legally extracted compile-only game assemblies.
# The build never searches for or copies these files automatically.
export JUNIMOGATE_GAME_REFERENCE_DIR="/absolute/path/to/game/assemblies"

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

```

## Boundaries

- `App -> Android, Mods, GameHost`
- `GameHost -> Core, Android, Extraction, Rewriter, smapi`
- `Mods -> Core`
- `Extraction -> Core`
- `Rewriter -> Core, Extraction`

UI and Mods do not reference game assemblies. Extraction uses the pinned `K4os.Compression.LZ4` 1.3.8 package only for bounded XALZ block decoding. The inspector does not extract commercial Content assets and does not decompile game source. Mod installation transaction interfaces remain boundaries; the scaffold does not claim a completed Mod installer. M4 keeps source extraction transactional and immutable; M5 keeps rewritten overlays separate. Product compatibility is based on local semantic rewrite guards and independent postconditions, not one exact support key, fixed MVID, complete-method hashes or global call counts. Full payload validation belongs to Deep Prepare and explicit verification, not every launch.

## Distribution and licensing

Do not commit or distribute Stardew Valley assemblies, assets, APKs, native libraries, decompiled source, inspector output, or other commercial game material. Users must supply a legally installed game on their own device. Keep all local extraction and inspection output in `/tmp`, `artifacts/`, `local-game/`, or an explicitly permitted sibling analysis directory, never in tracked source paths.

The JunimoGate license is not yet decided. [`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md) records the MIT-licensed `dotnet/android` AssemblyStore baseline, LZ4, Cecil/Harmony dependencies, and the exact public MonoGame provider's Ms-PL/MIT, OpenAL GNU Library GPL v2, and Stb public-domain notices. The provider build includes the tracked notices in its ignored local package. Public distribution remains blocked until M10 establishes exact corresponding-source and relink/replacement compliance for the pinned OpenAL binary. These notices do not apply any third-party license to JunimoGate as a whole, and this is an engineering reminder rather than legal advice.

See [`AGENTS.md`](AGENTS.md), [`docs/implementation-milestones.md`](docs/implementation-milestones.md), [`docs/m5-implementation-plan.md`](docs/m5-implementation-plan.md), [`docs/gamehost-activity-bridge-result.md`](docs/gamehost-activity-bridge-result.md), [`docs/gamehost-probe-result.md`](docs/gamehost-probe-result.md), [`docs/gamehost-gate2-contracts.md`](docs/gamehost-gate2-contracts.md), [`docs/game-discovery-result.md`](docs/game-discovery-result.md), [`docs/game-workspace-result.md`](docs/game-workspace-result.md), [`docs/runtime-probe-result.md`](docs/runtime-probe-result.md), [`docs/harmony-android-maintenance.md`](docs/harmony-android-maintenance.md), [`docs/startup-chain.md`](docs/startup-chain.md), [`docs/compatibility.md`](docs/compatibility.md), and [`docs/build-environment.md`](docs/build-environment.md).

Version ownership and cache invalidation rules are summarized in [`docs/versioning.md`](docs/versioning.md).
