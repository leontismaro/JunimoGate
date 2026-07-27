# Startup chain

> 当前产品入口是已实现的 Launch Alpha：首次/更新时 Deep Prepare，正常打开时 Fast Launch，用户点击后进入独立的 SMAPI 游戏进程。固定版本的 M5-PoC exact rewrite 仍只作为当前 1.6.15.3 基线，不得扩展为逐版本白名单。

## 1. 当前已验证链：M5-PoC

```text
MainActivity 诊断页
  -> 游戏发现
  -> source workspace
  -> exact compatibility probes
  -> applied workspace rewrite
  -> 启动 GameHostActivity
  -> 再次发现和准备
  -> GameHostBridge / ContentBridge
  -> 反射创建原版 GameRunner
  -> MonoGame View / Game.Run
```


它仍有明确限制：

- 无 SMAPI；
- 无 Mods；
- exact recipe 与单版本 fingerprint 过拟合；
- Launcher 与 GameHost 重复准备；
- 正常启动包含多轮全量验证。

这些限制不再作为继续研究的理由。当前冻结它，直接复用已工作的游戏宿主能力接入 SMAPI。

## 2. 当前产品链：Launch Alpha

JunimoGate 保持单 APK，但游戏运行在独立 `:game` 进程，以便退出后清理 SMAPI、Harmony、Mods 和游戏的静态状态。

```text
MainActivity / LauncherCoordinator
  -> 读取 active prepared snapshot
  -> 只验证 snapshot envelope，不读取 PackageManager，不遍历程序集/Content
  -> 有效：保留 PreparedGameHandle 并显示 Ready
  -> 缺失/失效：一个事务内完成 discovery / extraction / exact compatibility / rewrite（Deep Prepare）
  -> Ready，等待用户点击 Launch game
  -> 复用状态在点击时读取一次 PackageManager marker
  -> 更新则完成一次 Deep Prepare 并自动继续同一次启动请求
  -> 使用内存 handle 创建一次性 GameLaunchDescriptor，不重读 snapshot
  -> 传递随机 session key
  -> SmapiGameActivity (:game)
  -> 读取受控 descriptor 与 snapshot 各一次
  -> 一次性建立并验证 PreparedRuntimeFiles inventory
  -> 注册统一程序集解析
  -> 在 Default AssemblyLoadContext 加载提取的游戏程序集
  -> 安装 GameHostBridge / ContentBridge / audio bridge
  -> new SmapiRuntime(options)
  -> SmapiSession.Start()
  -> SCore
  -> SGameRunner : StardewValley.GameRunner
  -> SGame : StardewValley.Game1
  -> MonoGame View / Game.Run
  -> 首次 Content 加载
  -> ModResolver / AssemblyLoader
  -> Mod.Entry
  -> 游戏
```


当前 Deep Prepare 的冷构建预算仍是：PackageManager snapshot 2 次；每个 APK 打开 1 次、完整 SHA-256 1 次；新写 source workspace payload 全量重读 0 次；managed probe、native inventory、recipe evaluation、rewrite 各 1 次。正常复用启动的目标预算是：Launcher snapshot 读取 1 次、PackageManager snapshot 1 次、descriptor snapshot 读取 0 次、`:game` snapshot 读取 1 次、runtime inventory 1 次；所有 APK/workspace hash、probe 和 rewrite 均为 0。runtime inventory 只读取文件元数据，不读取或 hash Content 内容，并且后续每次 Content 打开或程序集加载不再重复校验。

产品路径不使用：

- `StardewModdingAPI.Program.Main`；
- SMAPILoader；
- 反射调用 SMAPI 入口；
- GameHost 反射创建原版 `GameRunner`；
- 多个互相隔离的游戏/SMAPI/Mod 程序集加载环境。

## 3. 模块职责

### `JunimoGate.App`

- 启动器状态、首次准备和启动按钮；
- 选择 active workspace/Profile；
- 创建 `GameLaunchDescriptor`；
- 启动 `SmapiGameActivity`；
- 当前展示准备/不支持/失败的最小状态；SMAPI/Mod 日志产品界面后置。

### `JunimoGate.Android`

- PackageManager 游戏发现；
- app-private storage；
- Android Activity、进程和路径边界；
- 首次导入或实际更新时进入 workspace 准备。

### `JunimoGate.Extraction` / `JunimoGate.Rewriter`

- 复用现有 Play 1.6.15.3 source/applied workspace；
- 不为 SMAPI 新增 support key、probe、完整方法 SHA 或重复全量验证；
- 首次/更新 Deep Prepare 与正常 Fast Launch 已分开；同一 Deep Prepare 复用一个 APK session。

### `JunimoGate.GameHost`

- 薄 `SmapiGameActivity : AndroidGameActivity`；
- 创建并持有 `SmapiSession`；
- Android View、OnPause/Resume/Destroy、Back、音频和错误处理；
- GameHostBridge、ContentBridge 和 MonoGame 生命周期；
- 不再直接实例化原版 GameRunner。

### `StardewModdingAPI`

- 作为 JunimoGate solution 内部源码项目构建；
- 保留独立程序集名和公共 API，兼容现有 Mods；
- 提供可实例化 `SmapiRuntime` / `SmapiSession`；
- 保留 SCore、SGameRunner、SGame、ModResolver、AssemblyLoader、events 和 Content 系统；
- 接受 JunimoGate 注入的 Activity、路径、主线程调度和程序集加载服务。

## 4. 统一程序集加载

首个闭环要求以下程序集共享同一类型世界：

```text
StardewValley
StardewModdingAPI
SMAPI runtime dependencies
Mods
```

游戏进程先注册 Default AssemblyLoadContext 解析，再加载提取的游戏程序集和 SMAPI。SMAPI 的 Mod AssemblyLoader 统一调用 JunimoGate 提供的加载服务，不再自行混用 `Assembly.Load(byte[])`、`LoadFrom` 和 `UnsafeLoadFrom`。

必须验证：

- SMAPI 看到的 `StardewValley.Game1` 与 GameHost 加载的是同一类型；
- Mod 继承的 `StardewModdingAPI.Mod` 与运行中的 SMAPI 类型相同；
- Harmony 只看到一份目标游戏程序集；
- 退出 `:game` 进程后不保留旧 Mod/Harmony/游戏静态状态。

## 5. SMAPI 宿主参数

`SmapiRuntimeOptions` 至少由 JunimoGate 注入：

- 当前 Android Activity；
- 游戏程序集目录；
- 游戏 Content 目录；
- Mods 目录；
- `smapi-internal` 目录；
- config、log、live save、save backup 目录；
- 主线程 dispatcher；
- 统一 managed assembly loader；
- View 挂载回调；
- 结构化错误和退出回调。

Android fork 中通过 `MainActivity.instance` 查 Activity、硬编码 `ExternalFilesDir/Mods`、根据 `Assembly.Location` 猜 GamePath、自行 `SetContentView`/`Finish` 和依赖 Loader 目录结构的逻辑，应改成使用这些宿主参数。

当前存档与备份链路是：

```text
Stardew Game1
  -> /storage/emulated/0/Android/data/org.junimogate.app/files/Saves
  -> SMAPI Constants.SavesPath（同一目录）
  -> SMAPI SaveBackupZip 读取实时存档
  -> no_backup/junimogate/user-data/save-backups（最多 20 份每日备份）
```

workspace、程序集改写结果、SMAPI internal 和 Mod rewrite cache 位于 `runtime`，可以重建；Mods、config、logs 和 save-backups 位于 `user-data`，Deep Prepare 与 runtime 重建不得删除。旧 `runtime/smapi` 用户目录由 storage layout v2 一次性迁移；旧实时 saves 跨 Android 内外存储时采用复制后删除，且不会覆盖非空的新目录。


## 6. 当前验收顺序

```text
1. solution 内构建 StardewModdingAPI.dll
2. APK 包含 SMAPI 与开源依赖，但不包含商业游戏 DLL/Content/native/AOT
3. SmapiGameActivity 创建 SmapiSession
5. SMAPI 发现一个真实 Mod 的 manifest
6. 加载 Mod assembly 并调用 Mod.Entry
8. 增加最小 Mods 导入、列表、启停和日志
```


当前真机范围是 Play 1.6.15.3/versionCode 245、ARM64、ARM64 test device、Android 16/API36。正常 Fast Launch 不执行 APK/workspace 全量 hash、metadata/native probe、Cecil workspace rewrite 或 applied workspace 重建；只有首次导入、检测到游戏更新、workspace 缺失/损坏或显式修复才进入 Deep Prepare。默认 Profile 当前保持零 Mod，Smoke Mod 仅作为可重复验收 fixture 存放在 `disabled`。

## 7. 保留边界

- 不提交或打包商业游戏 APK、DLL、Content、native library、AOT 或反编译源码；
- 游戏商业程序集只能作为本地编译引用，必须 `Private=false`，并由 APK 静态检查拦截误打包；
- source workspace 与 rewritten overlay 分离；
- staging 后原子提交；
- 调用方不能提供任意 workspace/assembly/Content 路径；
- licensing callbacks 不修改；
- 使用 stock .NET Android Mono、JIT、no interpreter、no game AOT/runtime copy；
- 保留 `StardewModdingAPI` 程序集身份和 LGPL 源码/notice 义务；
- 不复制 GPLv3 SMAPILoader 实现。

