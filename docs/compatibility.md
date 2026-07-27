# Compatibility

JunimoGate 的兼容目标是适应游戏小版本更新和未来 Android SMAPI，而不是只识别一份精确游戏二进制。当前方向以 [`../AGENTS.md`](../AGENTS.md) 为准。

## GameHost compatibility


产品兼容单位是语义 recipe family。分析器应检查：

- 目标 type/member signature；
- 局部 IL consumer pattern；
- stack、参数和返回类型；
- Activity/permission/storage/save/display bridge 能力；
- licensing use 分类与保持不变；
- 改写后 references、bridge calls 和 remaining unclassified uses。

以下变化本身不应导致“不支持”：

- MVID 改变；
- 无关日志或分支变化；
- instruction ordinal 改变；
- 无关 call-site 数量改变；
- native binary hash 改变但所需 ABI/API 仍兼容；
- 未命中已知 exact fingerprint。

未知构建应先运行一次语义兼容分析。只有真正无法分类的结构变化才进入 `Unsupported`/`Needs adaptation`，并输出最小差异报告。

## Launch compatibility

正常启动使用 Fast Launch，不重复运行 APK/workspace 全量 hash、metadata/native probe 或 Cecil rewrite。轻量链已经单次化为 Launcher snapshot 一次、点击时 PackageManager 一次、descriptor snapshot 零次、`:game` snapshot 一次、SMAPI 前 runtime inventory 一次；后续 load/open 不再额外校验。完整 Deep Prepare 只在首次导入、游戏更新、schema 变化、明确损坏、手动修复或显式验收时执行。

## SMAPI and Mod compatibility

当前主线以 Android fork 4.3.2.5 的明确提交 `6a34bbeb6e891536cdd948594094482ba0d8d264` 作为首个可运行移植基线，并在首个真实 Mod 闭环后迁移到更新的上游 SMAPI。JunimoGate 将它作为 solution 内部源码项目构建，保留独立的 `StardewModdingAPI` 程序集身份，但产品路径不依赖 `Program.Main`、SMAPILoader 或反射启动。

V1 必须保持上游 SMAPI：

- `StardewModdingAPI.dll` identity；
- public API 和 namespace；
- manifest semantics；
- dependency ordering；
- Mod lifecycle 和 event timing。

JunimoGate GameHost 直接创建并持有 SMAPI runtime/session，注入 Activity、Game/Content/Mods/internal/config/log/save 路径、主线程调度和统一程序集加载服务。Android-specific adaptation 集中在 fork 的平台层和有范围的 compatibility patch 中，不能为每个游戏 patch version 复制整套 GameHost recipe。

首个闭环要求游戏、SMAPI、SMAPI依赖和Mods在独立游戏进程的统一加载环境中共享类型身份；模组加载不得继续分散调用会创建不同加载环境的 `Assembly.Load(byte[])`、`LoadFrom` 和 `UnsafeLoadFrom`。

计划兼容状态：

- Compatible
- Needs adaptation
- Requires dependency
- Requires newer game/SMAPI
- Android limitation
- Known crash
- Untested

当前尚未实现完整 compatibility database 或 dependency resolver。

## Runtime compatibility

Phase 0 RuntimeProbe 的十个 hard cases 已在 ARM64 test device Android 16/API36 的 ARM64 Debug/Release 通过。当前运行方向仍是：

- stock .NET Android Mono；
- JIT；
- interpreter disabled；
- game AOT disabled；
- `Lib.Harmony 2.4.2-junimogate.11`；
- 不维护 custom Mono runtime。

RuntimeProbe 是 runtime 能力证据，不要求在每次开发或产品启动时重复运行。

No commercial game binaries are build inputs. Historical baseline is in [`../../ARCHITECTURE_PLAN.md`](../../ARCHITECTURE_PLAN.md) and [`../../TECHNICAL_FINDINGS.md`](../../TECHNICAL_FINDINGS.md); where those documents conflict with `AGENTS.md`, `AGENTS.md` is authoritative.
