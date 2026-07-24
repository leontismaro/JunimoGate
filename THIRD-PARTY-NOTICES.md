# Third-party notices

JunimoGate's project-wide license has not yet been selected. The notices below apply only to the identified third-party material and dependencies; they do **not** place the whole JunimoGate repository under the MIT License.

## .NET for Android AssemblyStore format reference

The AssemblyStore v2 format semantics and naming used by `src/JunimoGate.Extraction/AssemblyStoreV2.cs` were adapted from the official `dotnet/android` `assembly-store-reader-mk2` source:

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
- **Mono.Cecil 0.11.6**
  - repository: <https://github.com/jbevain/cecil>
  - release tag: `0.11.6`
  - license: MIT
  - copyright: Copyright (c) 2008 - 2015 Jb Evain; Copyright (c) 2008 - 2011 Novell, Inc.

## JunimoGate ARM64 instruction-cache helper

`native/JunimoGate.CacheFlush/clear_cache.S` is original JunimoGate glue implementing the standard ARMv8-A cache-maintenance sequence required after patching Mono JIT instructions. It is built with the pinned Zig 0.14.1 compiler from `build/cacheflush-versions.sh` and has no libc or NDK dependency. Its conservative `dc cvau` / `ic ivau` sequence was informed by the public Android/compiler-rt AArch64 `__clear_cache` algorithm; no compiler-rt source file or binary is copied into the repository.

The Zig compiler is a build tool and is not redistributed by JunimoGate. Zig is available under the MIT License; bundled third-party components in the Zig distribution retain their own licenses. If JunimoGate later redistributes a toolchain bundle, its complete notices must be included separately.

The common MIT terms for the dependencies listed in this section are:

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the "Software"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
