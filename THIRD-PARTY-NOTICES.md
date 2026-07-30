# Third-party notices

JunimoGate's project-wide license has not yet been selected. The notices below apply only to the identified third-party material and dependencies; they do **not** place the whole JunimoGate repository under the MIT License.

## .NET for Android AssemblyStore format reference

The AssemblyStore v1/v2 format semantics and naming used by `src/JunimoGate.Extraction/LegacyAssemblyStoreV1.cs` and `src/JunimoGate.Extraction/AssemblyStoreV2.cs` were adapted from the official `dotnet/android` `assembly-store-reader-mk2` source:

- repository: <https://github.com/dotnet/android>
- baseline commit: `1361e50584b56e690e2b8b5f6db6a04a1d2b7b38`
- reference directory: `tools/assembly-store-reader-mk2/AssemblyStore/`
- upstream license: MIT

JunimoGate's implementation is a bounded, BCL-based adaptation. It adds explicit full-version validation, a purpose-built ELF64 little-endian section reader, range/count/name limits, XALZ decompression, APK `ZipArchive` discovery, and staged per-file extraction. It does not use upstream's ELFSharp or Xamarin.LibZipSharp dependencies and does not copy an Android loader's reader implementation.

Copyright (c) .NET Foundation and Contributors
All rights reserved.

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the "Software"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

## K4os.Compression.LZ4

`src/JunimoGate.Extraction` references NuGet package `K4os.Compression.LZ4` version `1.3.8` to decode XALZ LZ4 blocks.

- project: <https://github.com/MiloszKrajewski/K4os.Compression.LZ4>
- author/copyright: Milosz Krajewski
- upstream license: MIT

The package's license and source repository remain authoritative for its complete notice text and attribution.

## Mono.Cecil

`src/JunimoGate.Rewriter` and `tests/JunimoGate.Rewriter.Tests` reference the fixed NuGet package **Mono.Cecil 0.11.6** for bounded, metadata-only compatibility inspection and synthetic assembly generation.

- repository: <https://github.com/jbevain/cecil>
- release tag: `0.11.6`
- license: MIT
- copyright: Copyright (c) 2008 - 2015 Jb Evain; Copyright (c) 2008 - 2011 Novell, Inc.

## Public MonoGame Android runtime provider

`JunimoGate.GameHost` references the ignored, reproducibly generated local package `MonoGame.Framework.Android` version `1.0.0-junimogate.f5d8bf.4`. The package is built by [`build/build-monogame-android.sh`](build/build-monogame-android.sh) from public source only; it does not copy the game-carried MonoGame assembly or OpenAL binary from a commercial APK.

- repository: <https://github.com/MonoGame/MonoGame>
- exact source commit: `f5d8bfbb4ac9847540b3c898e6237104ee98c149`
- target: `net9.0-android35.0`
- assembly identity: `MonoGame.Framework, Version=1.0.0.0`
- source license: Microsoft Public License (Ms-PL), with separately identified MIT terms for Mono.Xna-derived portions in the upstream license
- tracked complete source license: [`licenses/MonoGame-f5d8bf.txt`](licenses/MonoGame-f5d8bf.txt)
- generated package and provenance: ignored `artifacts/nuget/` and `artifacts/build-environment/monogame-android-build.json`

The exact public commit was identified from the installed game's assembly informational-version metadata. JunimoGate independently rebuilds it and verifies that its bounded public/protected API surface and every MonoGame type/member actually referenced by the validated game payload are compatible. The public provider is packaged intentionally; commercial `StardewValley.dll`, game Content, game AOT images, and game runtime libraries remain prohibited.

### MonoGame.Dependencies / OpenAL Soft

The pinned public dependency archive is:

- repository: <https://github.com/ConcernedApe/MonoGame.Dependencies>
- commit: `417b05a7529882ef90304e91ad0ac55c7f78cf94`

Its `openal-soft/README.txt` states that the included OpenAL library was built from `https://github.com/KonajuGames/openal-soft` and is based on official OpenAL Soft 1.16.0. The dependency archive does not identify the exact fork source commit used for that binary. The official OpenAL Soft 1.16.0 `COPYING` text is the GNU Library General Public License Version 2, June 1991:

- authoritative tag: <https://github.com/kcat/openal-soft/tree/openal-soft-1.16.0>
- tracked license text: [`licenses/OpenAL-Soft-1.16.0-COPYING.txt`](licenses/OpenAL-Soft-1.16.0-COPYING.txt)

The exact public ARM64 OpenAL binary is hash-pinned by the build and Android artifact verifiers. Before public distribution, JunimoGate must additionally establish and retain the exact corresponding OpenAL fork source commit/source offer and complete GNU Library GPL v2 compliance. This remains an M10 release blocker; the current build is for internal/personal sideload validation.

### StbImageSharp and StbImageWriteSharp

The public MonoGame source build compiles the following pinned StbSharp source trees:

- `StbSharp/StbImageSharp@8a8cbdb30cad1268a3b38fd80f15de8d95367c7c`
- `StbSharp/StbImageWriteSharp@3aede22e4d8456c4724c83eb72938ebf6ec77b8a`

Each pinned repository README states that the software is public domain. The tracked notice is [`licenses/StbSharp-PUBLIC-DOMAIN.txt`](licenses/StbSharp-PUBLIC-DOMAIN.txt).

## RuntimeProbe patching and dynamic-method dependencies

`tools/JunimoGate.RuntimeProbe.Core` uses the following fixed NuGet dependencies to test the stock Android runtime. These dependencies are test/probe infrastructure; their licenses do not apply MIT to JunimoGate as a whole.

- **Lib.Harmony 2.4.2-junimogate.11**
  - repository: <https://github.com/pardeike/Harmony>
  - upstream commit: `a264a1bf1ce689e4589e8dcc54b1e2818602a90a`
  - bundled pardeike/MonoMod commit: `dfc30a1506d37fb88a2c2be004f525205f46a24c`
  - bundled iced commit: `c50f29b7bc305696895c075f3fc7719751426b12`
  - tracked patch: `patches/harmony-android/harmony-2.4.2-android.patch`
  - generated package: ignored and reproducibly rebuilt by `build/build-harmony-android.sh`
  - license: MIT
  - Harmony copyright: Copyright (c) 2017 Andreas Pardeike
  - bundled MonoMod copyright: Copyright (c) 2015 - 2020 0x0ade
  - bundled iced copyright: Copyright (C) 2018-present iced project and contributors
- **MonoMod.Utils 25.0.9**
  - repository: <https://github.com/MonoMod/MonoMod>
  - package repository commit: `7df1f44bf28bb1c2a889a93559520398f4f82270`
  - license: MIT
  - copyright: Copyright (c) 2025 0x0ade, DaNike
- **MonoMod.Backports 1.1.2** and **MonoMod.ILHelpers 1.1.0**
  - repository: <https://github.com/MonoMod/MonoMod>
  - package repository commit: `a1b82852b2574742776af08818487b90b0bfab93`
  - license: MIT
  - copyright: Copyright (c) 2024 0x0ade, DaNike
## JunimoGate ARM64 instruction-cache helper

`native/JunimoGate.CacheFlush/clear_cache.S` is original JunimoGate glue implementing the standard ARMv8-A cache-maintenance sequence required after patching Mono JIT instructions. It is built with the pinned Zig 0.14.1 compiler from `build/cacheflush-versions.sh` and has no libc or NDK dependency. Its conservative `dc cvau` / `ic ivau` sequence was informed by the public Android/compiler-rt AArch64 `__clear_cache` algorithm; no compiler-rt source file or binary is copied into the repository.

The Zig compiler is a build tool and is not redistributed by JunimoGate. Zig is available under the MIT License; bundled third-party components in the Zig distribution retain their own licenses. If JunimoGate later redistributes a toolchain bundle, its complete notices must be included separately.

The common MIT terms for the dependencies listed in this section are:

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the "Software"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
