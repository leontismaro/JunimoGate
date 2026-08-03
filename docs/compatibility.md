# Compatibility

JunimoGate 的兼容目标是适应游戏小版本更新和未来 Android SMAPI，而不是只识别一份精确游戏二进制。当前方向以 [`../AGENTS.md`](../AGENTS.md) 为准。

## GameHost compatibility


产品兼容单位是 `stardew-android-mainactivity-bridge/v1`。当前分析器检查：

- 目标 type/member signature；
- 局部 IL consumer pattern；
- stack、参数和返回类型；
- Activity/permission/storage/save/display bridge 能力；
- 改写目标集合不包含 licensing callbacks。M5-PoC 已确认当前规则不会把这些方法作为改写目标，也不会修改它们，后续兼容分析不再扫描、hash、分类或重复验证它们；只有未来新增明确触及该区域的改写规则时才重新评估；
- 改写后只检查本次目标及其直接依赖、宿主桥接调用和未完成的目标替换，不把检查范围扩张到 licensing callbacks。

以下变化本身不应导致“不支持”：

- MVID 改变；
- 无关日志或分支变化；
- instruction ordinal 改变；
- 无关 call-site 数量改变；
- native binary hash 改变但所需 ABI/API 仍兼容；
- 未命中已知 exact fingerprint。

未知构建在 Deep Prepare 的 applied cache 未命中后直接运行一次局部兼容分析；全部规则通过则自动改写并缓存。只有目标签名、唯一局部来源、类型/stack 或 postcondition 失败时才进入 `Unsupported`。


## Launch compatibility

正常启动使用 Fast Launch，不重复运行 APK/workspace 全量 hash、metadata/native probe 或 Cecil rewrite。轻量链已经单次化为 Launcher snapshot 一次、点击时 PackageManager 一次、descriptor snapshot 零次、`:game` snapshot 一次、SMAPI 前 runtime inventory 一次；后续 load/open 不再额外校验。完整 Deep Prepare 只在首次导入、游戏更新、schema 变化、自动恢复检测到明确损坏或显式验收时执行。

2026-07-30 已真机执行 237 -> 239 -> 245 的版本更新链。每次点击都先检测 `PackageChanged`，随后只执行一次 Deep Prepare，新建 source/applied workspace、运行一次局部兼容分析和一次 rewrite，并在同一次点击继续启动；下一次启动恢复 Fast Launch。新版本达到 Running 后，Launcher 自动删除上一版 source/applied cache，最终私有目录只保留一套 active workspace 和一份 snapshot。

2026-08-02 的 `.73` -> `.74` SMAPI-only 更新曾暴露约 6.95 秒的错误 Deep Prepare：`GameLaunchSchema.BuildId` 同时参与 snapshot 与 SMAPI bundle 身份，使已有 source/applied workspace 虽然 CacheHit，仍重复读取约 420 MB APK 与约 378 MB workspace。随后 snapshot schema、game workspace/rewrite 和 SMAPI bundle deployment 身份已拆分；新版本继续读取旧 `v7` snapshot，纯 SMAPI 实现更新只部署当前 bundle 并复用已准备的游戏 workspace。保留数据覆盖安装最终 bundle `.30` 后，Launcher 在约 38 ms 内从 Checking 到 `fast_launch_ready`，该次干净日志没有 `JunimoGate.DeepPrepare`；点击到 `titleScreenGameMode (0)` 约 8.8 秒，确认 SMAPI-only 更新不再重准备游戏 workspace。

## SMAPI and Mod compatibility

当前主线以 Android fork 4.3.2.5 commit `6a34bbeb6e891536cdd948594094482ba0d8d264` 作为首个可运行移植基线，必要 Android patch series 已迁移到上游 SMAPI 4.5.2 commit `821167e5c511bf3a2d98f604e5e838561c469219`。JunimoGate 将它作为 solution 内部源码项目构建，保留独立的 `StardewModdingAPI` 程序集身份，但产品路径不依赖 `Program.Main`、SMAPILoader 或反射启动。

V1 必须保持上游 SMAPI：

- `StardewModdingAPI.dll` identity；
- public API 和 namespace；
- manifest semantics；
- dependency ordering；
- Mod lifecycle 和 event timing。

JunimoGate GameHost 直接创建并持有 SMAPI runtime/session，注入 Activity、Game/Content/Mods/internal/config/log/save 路径、主线程调度和统一程序集加载服务。Android-specific adaptation 集中在 fork 的平台层和有范围的 compatibility patch 中，不能为每个游戏 patch version 复制整套 GameHost recipe。

首个闭环要求游戏、SMAPI、SMAPI依赖和Mods在独立游戏进程的统一加载环境中共享类型身份；模组加载不得继续分散调用会创建不同加载环境的 `Assembly.Load(byte[])`、`LoadFrom` 和 `UnsafeLoadFrom`。




2026-08-03 的 mobile runtime cleanup 移除了 Android fork 中按具体 Mod 选择的全局 rewrite、render、菜单和 Entry 后修复层。当前所有 Mod 统一经过标准 `AssemblyLoader`、通用依赖绑定与 rewrite cache，并在 Android 主线程队列中执行 `Mod.Entry`；启动器不再根据 Mod ID、精确版本或程序集名称改写 SpaceCore、Unlockable Bundles 等第三方 Mod。具体 Mod 的 Android 缺陷不再进入启动器核心，而应在通用 runtime/SMAPI 边界或 Mod 自身源码中解决。


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

- application-local copy derived from the project-local .NET Android Mono pack；
- JIT；
- interpreter disabled；
- game AOT disabled；
- `Lib.Harmony 2.4.2-junimogate.11`；
- 不维护完整 Mono fork；只在构建阶段改写应用副本中的两个访问判定函数。

RuntimeProbe 是 runtime 能力证据，不要求在每次开发或产品启动时重复运行。

No commercial game binaries are build inputs. Historical baseline is in [`../../ARCHITECTURE_PLAN.md`](../../ARCHITECTURE_PLAN.md) and [`../../TECHNICAL_FINDINGS.md`](../../TECHNICAL_FINDINGS.md); where those documents conflict with `AGENTS.md`, `AGENTS.md` is authoritative.
