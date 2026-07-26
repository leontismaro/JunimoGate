# Startup chain

The V1 target remains a single APK and single process:

1. `JunimoGate.App` discovers supported installations, presents redacted diagnostics, prepares the trusted source/applied workspaces, and enables an explicit GameHost launch only after the current checks pass.
2. `JunimoGate.Android` queries exactly the Play and Galaxy package IDs, captures package/version/base/splits/signing snapshots, returns every visible candidate without silently preferring one store, and rebuilds source/applied execution capabilities without accepting caller-selected paths.
3. `JunimoGate.Extraction` hashes each installed APK, inventories ZIP roles and ABI evidence, rejects package-update races, and builds or completely revalidates the immutable Content/assembly workspace behind the tested-certificate gate.
4. M4 writes strict source/extraction/rewrite manifests, validates exact payload files and hashes, commits the complete staging directory, revalidates installation identity, and atomically updates active/previous state. CacheHit still performs complete payload re-hashing.
5. `JunimoGate.Rewriter` applies only catalog-approved `play-1.6.15.3-gamehost-bridge@1` mutations to a separately staged overlay after certificate, manifests, file set, sizes, hashes, target MVID and complete-method guards are revalidated.
6. M5 builds/revalidates the content-addressed applied workspace; `JunimoGate.GameHost` independently obtains a fresh in-process capability, attaches its host bridges, loads only sealed plan-owned assemblies, redirects Content to exact source-plan files, constructs GameRunner, mounts the MonoGame View, and starts the original game without SMAPI.
7. M6 will insert the Android SMAPI fork into the accepted GameHost path. SMAPI, not the Launcher, will own Mod lifecycle and game-loop integration.

M1 and M2 established the reproducible ARM64 Android environment and selected stock Android Mono plus `Lib.Harmony 2.4.2-junimogate.11`; no custom runtime is maintained. Debug and Release artifacts remain API35/minSdk26/ARM64-only and contain no commercial game payload or game AOT runtime.

M3 is complete for the current Google Play scope: API36 missing-package and Play `com.chucklefish.stardewvalley` 1.6.15.3/versionCode 245 full-candidate paths passed, including signer identity, all three APK hashes, Content and ARM64 AssemblyStore roles, redaction, and independent `KnownTested` certificate verification. API26–27 remains implemented but awaits device evidence; Galaxy and dual-package acceptance are deferred. `KnownTested` is this project's tested identity anchor, not independent publisher certification.

M4 is complete for that Play scope. Built and CacheHit passed on the ARM64 test device/API36 baseline. Its app-private source workspace remains immutable and records `unrewritten:v1` / `not-applied`; M5 never overwrites or falsifies those original identities. Evidence is in [`game-workspace-result.md`](game-workspace-result.md).


The hosted path uses the explicitly adopted `TrustedInstalledSource` policy: every launch requires the currently installed tested-certificate package and fresh exact APK/workspace validation. It does not execute the original package/UID-bound Play LVL and does not claim Google-account purchase ownership.

