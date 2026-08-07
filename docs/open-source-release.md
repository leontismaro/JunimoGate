# Open-source release checklist

Publishing this repository and distributing an APK are separate licensing
events. Repository publication needs a project-wide license. APK distribution
also needs the notices and source materials required by every bundled native
or managed dependency.

## Project license

Except for identified third-party material, JunimoGate-authored material is
licensed under GPL-3.0-only. `LICENSE-EXCEPTION` grants a narrow additional
permission to link that code with the pinned Ms-PL MonoGame provider; it does
not relicense either component or permit closed-source JunimoGate derivatives.

For each binary release:

1. include the GPLv3 license, the linking exception, and all third-party
   notices;
2. publish the exact JunimoGate corresponding source and build scripts for the
   released APK or AAB;
3. identify the exact `smapi/` submodule commit and keep its modified
   LGPL-3.0-only source available;
4. provide any installation information required by GPLv3 for users to build,
   install, and run a modified JunimoGate package;
5. preserve permission to replace or debug the LGPL-covered portions.

The MonoGame exception is pinned to the reviewed source commit. A MonoGame
upgrade requires a fresh license review and an explicit exception update.

## OpenAL Soft

MonoGame uses OpenAL Soft as the native audio backend for music, sound effects,
streaming audio, and Android audio-device access. The packaged ARM64 library is
not copied from the game. It is the file from `MonoGame.Library.OpenAL 1.24.3.4`
and is built from OpenAL Soft 1.24.3.

OpenAL Soft is licensed under GNU Library GPL version 2 or later, with identified
BSD 3-Clause portions. For each APK release:

1. include the OpenAL license and BSD notice with the distributed notices;
2. identify that the application uses OpenAL Soft;
3. provide equivalent access to the complete corresponding source and build
   scripts from the same release location;
4. keep the application source and build path sufficient to rebuild with a
   modified OpenAL library;
5. do not impose terms that prohibit modification or reverse engineering for
   debugging those modifications.

Generate the source attachment and checksum with:

```bash
./build/package-openal-corresponding-source.sh
```

Attach both generated files from `artifacts/corresponding-source/` beside the
APK or AAB. The source bundle contains the pinned provider repository, OpenAL
Soft source, build scripts, commits, hashes, and Android build instructions.

This checklist records the project's engineering interpretation of the
license. It is not legal advice.
