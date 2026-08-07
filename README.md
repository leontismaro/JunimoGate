# JunimoGate

JunimoGate is an experimental .NET 9 Android launcher for a legally installed copy of Stardew Valley. It discovers the installed package, prepares an app-private workspace, and starts a source-integrated Android SMAPI runtime in an isolated `:game` process.

This repository does not contain or distribute Stardew Valley APKs, assemblies, Content, native libraries, or decompiled source. The user supplies the installed game on their own Android device.

## Project status

The current source tree includes:

- package discovery and signer-aware source selection;
- AssemblyStore v1/v2 extraction into an immutable source workspace;
- a local semantic bridge recipe with postconditions;
- Deep Prepare for first import, game updates, schema changes, and repair;
- Fast Launch using cached snapshots and one pre-SMAPI runtime inventory;
- a source-integrated SMAPI fork in the `smapi/` submodule;
- an isolated game process and one-shot launch descriptors;
- Mod archive import, a global Mod library, groups, selection, and sharing;
- product and SMAPI log access;
- save discovery, import, export, and backup management;
- English and Simplified Chinese launcher resources.

There are no public release artifacts yet. Production signing, project licensing, Android distribution-policy review, third-party source obligations, and release packaging remain open work.

## Build

Clone the repository with its SMAPI submodule:

```bash
git clone --recurse-submodules https://github.com/leontismaro/JunimoGate.git
cd JunimoGate
```

Bootstrap the repository-local Android toolchain:

```bash
./build/bootstrap-android.sh
```

Build the public-source MonoGame provider and patched runtime dependencies:

```bash
./build/build-monogame-android.sh
./build/build-harmony-android.sh
./build/build-cacheflush.sh
```

Android application builds require a local directory containing legally extracted compile-only game assemblies. The build does not search for or copy them automatically.

```bash
export JUNIMOGATE_GAME_REFERENCE_DIR="/absolute/path/to/game/assemblies"
./build/build-android.sh Debug all
```

Run host checks and inspect the resulting APKs:

```bash
./build/test-host.sh
./build/verify-android-artifacts.sh
```

Generated packages, APKs, reports, local game inputs, and toolchains remain under ignored paths such as `artifacts/`, `local-game/`, and `.toolchains/`.

## Architecture

- `JunimoGate.App` owns launcher UI and user actions.
- `JunimoGate.Android` owns Android package and private-storage boundaries.
- `JunimoGate.Extraction` creates the immutable source workspace.
- `JunimoGate.Rewriter` applies the local semantic bridge recipe.
- `JunimoGate.GameHost` owns launch descriptors, the isolated process, and SMAPI hosting.
- `JunimoGate.Mods` owns Mod library, group, selection, and transfer data.
- `smapi/` contains the Android SMAPI fork as a Git submodule.

The normal launch path does not hash APKs or workspaces, run compatibility probes, execute Cecil rewrites, or rebuild applied workspaces. Those operations belong to Deep Prepare or explicit verification.

## Documentation

Start with the [documentation index](docs/README.md). Important references include:

- [startup chain](docs/startup-chain.md);
- [SMAPI architecture](docs/smapi-architecture.md);
- [compatibility model](docs/compatibility.md);
- [build environment](docs/build-environment.md);
- [version and cache identities](docs/versioning.md);
- [public roadmap](docs/roadmap.md).

Repository-specific agent constraints are in [AGENTS.md](AGENTS.md).

## Distribution and licensing

The project-wide license has not yet been selected. Until a license is added, publication of the source does not grant general permission to use, modify, or redistribute JunimoGate.

[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) records the known dependency notices and unresolved OpenAL corresponding-source/relink requirements. Those requirements must be resolved before distributing application binaries.

JunimoGate is an independent, unofficial project. Stardew Valley and related names and marks belong to their respective owners. This project is not endorsed by or affiliated with ConcernedApe, Stardew Valley, or the SMAPI project.
