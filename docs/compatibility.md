# Compatibility

V1 compatibility depends on preserving the upstream SMAPI assembly name, public API, manifest semantics, dependency ordering, and Mod lifecycle while isolating Android-specific adaptation in the future fork.

Planned compatibility states are: Compatible, Requires dependency, Requires newer game/SMAPI, Android limitation, Known crash, and Untested. This scaffold does not yet implement a compatibility database or dependency resolver.

The Phase 0 RuntimeProbe implements ten hard cases, including realistic cross-assembly Harmony Prefix/Postfix, private field injection, transpiler private access, an SMAPI-style prefix, Mono JIT entry validation, ARM64 instruction-cache validation, and MonoMod DynamicMethod/Cecil DMD. Reflection alone is not treated as proof. Final ARM64 Debug and Release reports both pass on a ARM64 test device running Android 16/API 36 in explicit stock Mono JIT/no-interpreter/no-AOT mode. The selected outcome is stock runtime plus the reproducible `Lib.Harmony 2.4.2-junimogate.11` library fix; no custom Mono runtime is maintained. See [`runtime-probe-result.md`](runtime-probe-result.md).

No commercial game binaries are build inputs. See [`../../ARCHITECTURE_PLAN.md`](../../ARCHITECTURE_PLAN.md) and [`../../TECHNICAL_FINDINGS.md`](../../TECHNICAL_FINDINGS.md) for the external baseline.
