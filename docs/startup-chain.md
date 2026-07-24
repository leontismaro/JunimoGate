# Startup chain

The V1 target is a single APK and single process:

1. `JunimoGate.App` shows discovery and workspace diagnostics, atomically writes redacted app-private reports, and will later request launch.
2. `JunimoGate.Android` queries exactly the Play and Galaxy package IDs, captures package/version/base/splits/signing snapshots, returns every visible candidate without silently preferring one store, and adapts the live candidate to the fixed app-private workspace root.
3. `JunimoGate.Extraction` hashes each installed APK, inventories ZIP entry roles and ABI evidence without copying payloads during discovery, rejects package-update races, then builds or fully revalidates an immutable Content/assembly workspace behind the tested-certificate gate.
4. M4 writes strict source/extraction/rewrite manifests, validates the exact payload file set and hashes, commits the whole staging directory, revalidates installation identity, and atomically updates active/previous state. CacheHit still performs complete payload re-hashing.
5. `JunimoGate.Rewriter` will apply a versioned M5 recipe to separately staged output only after certificate, manifests, file set, sizes, and hashes are revalidated again.
6. `JunimoGate.GameHost` will create the game Activity, resolve the newly verified prepared assemblies, and enter the Android SMAPI fork.
7. SMAPI, not the launcher, owns Mod lifecycle and game-loop integration.

The current scaffold implements host-testable contracts, PackageManager-backed M3 discovery, signer set/rotation capture, tested Play-certificate identity enforcement, complete APK-source hash/inventory reporting, strict Content extraction, read-only AssemblyStore v2/ELF64/XALZ extraction, immutable app-private workspace build/cache/quarantine/activation, managed metadata inspection, a reproducible ARM64 Android build, and a completed Phase 0 Harmony/MonoMod RuntimeProbe. Final Debug and Release M2 device reports selected stock Android Mono plus the pinned JunimoGate Harmony/MonoMod library fix, so no custom runtime is maintained.

M3 is complete for the current Google Play scope: API 36 missing-package and Play `com.chucklefish.stardewvalley` 1.6.15.3/versionCode 245 full-candidate paths passed, including signer identity, all three APK hashes, Content and ARM64 AssemblyStore roles, report structure, redaction, and independent report verification of `KnownTested` certificate status. API 26–27 remains declared and implemented but awaits device evidence; Galaxy and dual-package acceptance are deferred. `KnownTested` denotes this project's tested identity anchor, not independent publisher certification.

M4 is also complete for that Play scope. Built and CacheHit were accepted on the same ARM64 test device/API 36 baseline, and the host suite now passes 68/68. Workspace report format 2 adds duration, peak temporary bytes, final workspace bytes, and ordered de-duplicated progress stages. The device verifier now also requires interrupted staging recovery, exact payload plus three-manifest file sets, full device-side payload hashes, strict rewrite status, state integrity, redaction, and App-PID-filtered logcat. Evidence and commands are in [`game-workspace-result.md`](game-workspace-result.md).

M4 deliberately records `unrewritten:v1` / `not-applied` and does not rewrite, load, or execute any game assembly. JunimoGate still does not start the game or SMAPI. M5 must not trust only the active key or report: immediately before rewrite/load/execute it must re-check the current certificate policy and strictly revalidate all three workspace manifests, expected file set, sizes, and SHA-256 hashes.

The implementation order and acceptance gates are defined in [`implementation-milestones.md`](implementation-milestones.md); M3 evidence is in [`game-discovery-result.md`](game-discovery-result.md), M4 evidence is in [`game-workspace-result.md`](game-workspace-result.md), and M2 runtime evidence is in [`runtime-probe-result.md`](runtime-probe-result.md). See the external research baseline: [`../../ARCHITECTURE_PLAN.md`](../../ARCHITECTURE_PLAN.md) and [`../../TECHNICAL_FINDINGS.md`](../../TECHNICAL_FINDINGS.md).
