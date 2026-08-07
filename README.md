**English** | [简体中文](README.zh-CN.md)

# JunimoGate

JunimoGate is an Android SMAPI launcher for Stardew Valley. It integrates an
Android SMAPI runtime with launch, Mod, save, and diagnostics management.

<p align="center">
  <img src="docs/assets/junimogate-home.webp" alt="JunimoGate home screen" width="360">
  <img src="docs/assets/junimogate-mods.webp" alt="JunimoGate Mod library" width="360">
</p>

## Features

JunimoGate currently provides:

- launch Stardew Valley through the integrated Android SMAPI runtime;
- import Mod archives and manage installed Mod versions in one library;
- search Mods, organize them into groups, choose which Mods are enabled, and
  share them;
- discover saves, import or export them, and manage backups;
- view launcher and SMAPI logs for troubleshooting;
- faster routine launches after the initial setup;
- English and Simplified Chinese interfaces.

## Community and support

- Join the [JunimoGate Discord community](https://discord.gg/q29GT4Vh6) to
  connect with other users, exchange ideas, and get community support.
- Use [GitHub Issues](https://github.com/leontismaro/JunimoGate/issues) to
  report bugs, request features, or submit feedback that needs to be tracked.

## Acknowledgements

JunimoGate continues, at the project-goal level, the Android SMAPI launcher
work pursued by [NRTnarathip/SMAPILoader](https://github.com/NRTnarathip/SMAPILoader).
The JunimoGate launcher code is an independent implementation. We thank that
project and the related community work that preceded it.

The bundled JunimoGate-SMAPI fork is derived from
[Pathoschild/SMAPI](https://github.com/Pathoschild/SMAPI) and
[NRTnarathip/SMAPI-Android-1.6](https://github.com/NRTnarathip/SMAPI-Android-1.6),
with further Android integration and maintenance by the JunimoGate project.
Exact source commits are recorded in the
[Android branch provenance](smapi/docs/android/provenance.md).

## Build guide

Clone the repository with its SMAPI submodule:

```bash
git clone --recurse-submodules https://github.com/leontismaro/JunimoGate.git
cd JunimoGate
```

The complete [build environment and script guide](docs/build-environment.md)
documents prerequisites, the repository-local Android toolchain, every public
entry point under `build/`, local game compile references, verification, and
generated artifacts.

A typical development build is:

```bash
./build/bootstrap-android.sh
./build/build-monogame-android.sh

export JUNIMOGATE_GAME_REFERENCE_DIR="/absolute/path/to/game/assemblies"
./build/build-android.sh Debug app
```

The reference directory is a local, compile-only input. Build scripts do not
search for game files or copy commercial game payloads into the repository or
APK.

## Project structure

- `src/JunimoGate.App` contains the Android launcher application and UI;
- `src/JunimoGate.Android` owns Android package and private-storage boundaries;
- `src/JunimoGate.Extraction` discovers and prepares game inputs;
- `src/JunimoGate.Rewriter` applies guarded Android bridge rewrites;
- `src/JunimoGate.GameHost` owns the isolated process and SMAPI host contract;
- `src/JunimoGate.Mods` owns the Mod library, groups, selection, and transfer
  data;
- `smapi/` is the Android SMAPI fork, tracked as a Git submodule;
- `build/` contains toolchain, build, packaging, and verification entry points;
- `tests/` and `tools/` contain automated checks and development utilities;
- `docs/` contains architecture, maintenance, validation, and release records.

See the [documentation index](docs/README.md),
[startup chain](docs/startup-chain.md), and
[SMAPI architecture](docs/smapi-architecture.md) for the maintained design.

## License

Except for identified third-party material and repositories with their own
license, JunimoGate-authored material is licensed under
[GPL-3.0-only](LICENSE), with the narrow linking permission stated in
[LICENSE-EXCEPTION](LICENSE-EXCEPTION).

The `smapi/` submodule remains licensed under LGPL-3.0-only. MonoGame, OpenAL
Soft, the .NET runtime, and other dependencies retain their respective
licenses. See [third-party notices](THIRD-PARTY-NOTICES.md) and the
[open-source release checklist](docs/open-source-release.md).

JunimoGate is an independent, unofficial project. Stardew Valley and related
names and marks belong to their respective owners. This project is not
endorsed by or affiliated with ConcernedApe, Stardew Valley, or the SMAPI
project.

Third-party names, marks, and game-related elements visible in the product
screenshots remain the property of their respective owners.
