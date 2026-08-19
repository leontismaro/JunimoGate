# Third-party notices

Except for identified third-party material and repositories with their own
license, JunimoGate-authored material is licensed under GPL-3.0-only with the
narrow MonoGame linking permission in `LICENSE-EXCEPTION`. The notices below
do not relicense the identified dependencies or place the whole repository
under any dependency's license.

## SMAPI and Android launcher lineage

At the project-goal level, JunimoGate continues the Android SMAPI launcher work
pursued by [NRTnarathip/SMAPILoader](https://github.com/NRTnarathip/SMAPILoader).
The JunimoGate launcher code is an independent implementation. We thank that
project and the related community work that preceded it.

The bundled JunimoGate-SMAPI fork is derived from:

- [Pathoschild/SMAPI](https://github.com/Pathoschild/SMAPI), maintained by
  Pathoschild and contributors;
- [NRTnarathip/SMAPI-Android-1.6](https://github.com/NRTnarathip/SMAPI-Android-1.6),
  the direct Android runtime lineage used by this fork.

The fork and JunimoGate's modifications to it remain under LGPL-3.0-only.
Exact source commits and import points are recorded in
[`smapi/docs/android/provenance.md`](smapi/docs/android/provenance.md). The
corresponding modified source is the `smapi/` Git submodule pinned by each
JunimoGate release.

## .NET Android Mono runtime input

The application-local `libmonosgen-2.0.so` is derived at build time from the project-local .NET Android ARM64 runtime pack. JunimoGate changes only the two Mono access-decision function bodies in that copy; the SDK/runtime pack is never modified in place. The .NET runtime license and attribution shipped with the selected SDK/runtime distribution remain authoritative.

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

## Bundled managed and Android dependencies

The release APK also contains the following runtime dependencies. Their source
repositories and package metadata remain authoritative; the complete common
license texts and package-specific notices are available through the
application's Licenses screen.

- DeepCloner 0.10.4: MIT;
- HtmlAgilityPack 1.12.1: MIT;
- Markdig 0.41.1: BSD-2-Clause, with the complete text in
  [`licenses/Markdig-BSD-2-Clause.txt`](licenses/Markdig-BSD-2-Clause.txt);
- Microsoft.Extensions.DependencyInjection.Abstractions 7.0.0: MIT;
- NVorbis 0.10.5: MIT;
- Newtonsoft.Json 13.0.3 and Newtonsoft.Json.Bson 1.0.2: MIT;
- Pintail 2.8.1: MIT;
- Platonymous.TMXTile 1.5.9: MIT;
- SkiaSharp and SkiaSharp.NativeAssets.Android 4.150.1: MIT with the upstream
  Skia and incorporated-component notices packaged in the APK;
- StbImageWriteSharp 1.16.7: public domain;
- TextCopy 6.2.1: MIT;
- Microsoft .NET/Mono Android runtime 9.0.17: MIT with the runtime's complete
  third-party notices packaged in the APK;
- Microsoft AndroidX, Kotlin, and Material bindings and their transitive
  runtime packages: MIT for the .NET bindings and Apache-2.0 or the
  package-identified upstream terms for the bound Android libraries. The
  binding and Apache-2.0 notices are packaged in the APK.

The Android runtime does not compile or distribute
`Pathoschild.Http.FluentClient`, `Microsoft.AspNet.WebApi.Client`, or
`System.Net.Http.Formatting.dll`. Those libraries only support SMAPI's own
background online update clients, which are disabled in the Android build.
JunimoGate's application update check is an independent `HttpClient`
implementation and remains available.

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

### MonoGame.Library.OpenAL / OpenAL Soft

MonoGame uses OpenAL Soft as its native Android audio backend. JunimoGate takes
the Android libraries directly from the pinned public NuGet package instead of
relying on the outdated `openal-soft/README.txt` in MonoGame.Dependencies:

- package: `MonoGame.Library.OpenAL 1.24.3.4`;
- package SHA-256: `91bcc6b4a559aa4edba10c10146ebdfe094950f4270a81f1b7e3d1c826d859d1`;
- provider repository: <https://github.com/MonoGame/MonoGame.Library.OpenAL>;
- provider commit: `4d08985956a3278adad0bd51486fc1b217b829d2`;
- OpenAL Soft repository: <https://github.com/kcat/openal-soft>;
- OpenAL Soft commit: `dc7d7054a5b4f3bec1dc23a42fd616a0847af948` (release 1.24.3);
- provider build-scripts commit: `43f13de8d1dd568f35eb9fe665fe4824e90a437d`;
- ARM64 binary SHA-256: `c972352d7f72966ad3b05be42a425925e8d28efd88a6d0f0e726f6b1cf4bb6d0`.

OpenAL Soft is licensed under GNU Library General Public License version 2 or
later, with separately identified BSD 3-Clause portions:

- GNU Library GPL text: [`licenses/OpenAL-Soft-1.24.3-COPYING.txt`](licenses/OpenAL-Soft-1.24.3-COPYING.txt);
- BSD notice: [`licenses/OpenAL-Soft-1.24.3-BSD-3-Clause.txt`](licenses/OpenAL-Soft-1.24.3-BSD-3-Clause.txt).

The exact corresponding source and build inputs are now identified. Every
binary release must still place the source archive and checksum generated by
[`build/package-openal-corresponding-source.sh`](build/package-openal-corresponding-source.sh)
beside the APK or AAB, carry these notices, and preserve the user's ability to
rebuild with a modified library. See
[`docs/open-source-release.md`](docs/open-source-release.md).

### StbImageSharp and StbImageWriteSharp

The public MonoGame source build compiles the following pinned StbSharp source trees:

- `StbSharp/StbImageSharp@8a8cbdb30cad1268a3b38fd80f15de8d95367c7c`
- `StbSharp/StbImageWriteSharp@3aede22e4d8456c4724c83eb72938ebf6ec77b8a`

Each pinned repository README states that the software is public domain. The tracked notice is [`licenses/StbSharp-PUBLIC-DOMAIN.txt`](licenses/StbSharp-PUBLIC-DOMAIN.txt).

## Harmony and dynamic-method dependencies

JunimoGate bundles the fixed Harmony build used by its Android SMAPI runtime.
RuntimeProbe and build tooling also use the listed MonoMod dependencies. Their
licenses apply only to the identified components and do not apply MIT to
JunimoGate as a whole.

- **Lib.Harmony 2.4.2-junimogate.64**
  - repository: <https://github.com/pardeike/Harmony>
  - upstream commit: `a264a1bf1ce689e4589e8dcc54b1e2818602a90a`
  - bundled pardeike/MonoMod commit: `dfc30a1506d37fb88a2c2be004f525205f46a24c`
  - bundled iced commit: `c50f29b7bc305696895c075f3fc7719751426b12`
  - tracked patch: `patches/harmony-android/harmony-2.4.2-android.patch`
  - generated package: ignored and rebuilt from pinned sources by `build/build-harmony-android.sh`; each build's hashes are recorded in its ignored provenance file
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
