# Startup chain

The V1 target is a single APK and single process:

1. `JunimoGate.App` shows diagnostics and requests launch.
2. `JunimoGate.Android` discovers one of the two explicitly visible正版 packages and reports package identity and APK source paths.
3. `JunimoGate.Extraction` inventories ZIP entries by content, computes the versioned workspace key, and will later coordinate an atomic staging transaction.
4. `JunimoGate.Rewriter` will apply a versioned Phase 2 recipe to staging output.
5. `JunimoGate.GameHost` will create the game Activity, resolve prepared assemblies, and enter the Android SMAPI fork.
6. SMAPI, not the launcher, owns Mod lifecycle and game-loop integration.

The current scaffold implements host-testable contracts, read-only APK inventory, AssemblyStore v2/ELF64/XALZ extraction, managed metadata inspection, a reproducible ARM64 Android build, and a completed Phase 0 Harmony/MonoMod RuntimeProbe. Final Debug and Release device reports selected stock Android Mono plus the pinned JunimoGate Harmony/MonoMod library fix, so no custom runtime is maintained. It does not yet discover installed packages through Android, extract a product Content workspace, rewrite or load the game, or start SMAPI. The implementation order and acceptance gates are defined in [`implementation-milestones.md`](implementation-milestones.md); the runtime evidence is in [`runtime-probe-result.md`](runtime-probe-result.md). See the external research baseline: [`../../ARCHITECTURE_PLAN.md`](../../ARCHITECTURE_PLAN.md) and [`../../TECHNICAL_FINDINGS.md`](../../TECHNICAL_FINDINGS.md).
