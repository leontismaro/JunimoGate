# Harmony Android patch 维护手册

本文是 `Lib.Harmony 2.4.2-junimogate.11` 下游补丁的操作与维护手册。它回答：为什么采用 patch、如何构建和验证、如何处理上游升级与冲突、脚本失败时如何定位，以及什么条件下可以缩小或删除补丁。

权威证据与边界见 [`runtime-probe-result.md`](runtime-probe-result.md)；通用 Android 工具链见 [`build-environment.md`](build-environment.md)。

## 1. 维护对象

JunimoGate 不提交 Harmony/MonoMod 的完整第三方源码，也不提交生成的 NuGet 包。仓库只维护：

```text
patches/harmony-android/harmony-2.4.2-android.patch   # 下游源码差异
build/harmony-android-versions.sh                     # 固定输入与成品哈希
build/build-harmony-android.sh                        # 下载、应用、构建、验证流水线
native/JunimoGate.CacheFlush/clear_cache.S            # ARM64 I-cache helper
build/cacheflush-versions.sh
build/build-cacheflush.sh
```

生成物位于 ignored 目录：

```text
artifacts/nuget/Lib.Harmony.2.4.2-junimogate.11.nupkg
artifacts/nuget/Lib.Harmony.2.4.2-junimogate.11.provenance.json
.toolchains/source-cache/harmony-android/
.toolchains/source-build/harmony-android/
```

真正被构建的是解包后的上游项目：

```text
Lib.Harmony/Lib.Harmony.csproj
```

最终 `0Harmony.dll` 通过 Harmony 的 ILRepack 流程内联 patched MonoMod.Core 等依赖。RuntimeProbe 通过根目录 [`../NuGet.Config`](../NuGet.Config) 从 ignored 本地 feed 引用该包。

这里有两个不同的 NuGet 作用域：

- **构建下游 Harmony 包时**，临时上游源码树由脚本显式使用 `https://api.nuget.org/v3/index.json` restore 其构建依赖，不使用 JunimoGate 根目录的 package source mapping；
- **JunimoGate 消费生成包时**，根 `NuGet.Config` 将 `Lib.Harmony` 映射到 `artifacts/nuget` 的 `junimogate-local` feed，防止同名下游版本被错误地从 nuget.org 解析。

## 2. 设计原则

### 2.1 固定上游，不追踪浮动分支

所有源码都使用 commit 和归档 SHA-256 固定：

- Harmony；
- pardeike/MonoMod；
- iced 子模块。

禁止把 `main`、`master`、`latest` 或未固定的预发布包作为生产构建输入。

### 2.2 Patch 是可审计差异，不是源码快照

保留 patch 而不复制完整上游源码，可以：

- 清楚看到 JunimoGate 修改了什么；
- 避免第三方源码与原创代码混杂；
- 让上游升级冲突显式失败；
- 保持许可证与 provenance 清晰；
- 从固定输入重建相同包。

### 2.3 修 library，不修改 stock runtime

M2 的最终结论是：stock .NET 9 Android Mono JIT 足够，但 Harmony/MonoMod 需要 Android library fix。当前不维护 custom `libmonosgen-2.0.so`，也不修改全局 .NET runtime pack。

### 2.4 版本不可变

同一个 NuGet 版本不得对应不同源码、patch 或二进制。任何会改变 `0Harmony.dll` 的修改都必须：

1. 使用新的 `-junimogate.N` 版本；
2. 更新 patch；
3. 重新构建并固定新哈希；
4. 重跑 host、Android Debug、Android Release；
5. 更新证据与文档。

`.1`～`.10` 是调查阶段候选；最终固化基线是 `.11`。禁止覆盖 `.11`。

### 2.5 上游优先

本地 patch 是“临时但一等的生产依赖”。若 Harmony/MonoMod 上游实现等价修复，应删除已上游部分，而不是永久保留重复代码。候选升级仍必须通过 JunimoGate 回归矩阵。

## 3. 当前 patch 解决的问题

`harmony-2.4.2-android.patch` 当前覆盖：

1. Android 映射到适配后的 Linux/POSIX system backend；
2. bionic `libc.so` / `libdl.so` 动态库解析；
3. bionic `__errno`，而非 glibc `__errno_location`；
4. Android `_SC_PAGESIZE` selector；
5. ARM64 tagged pointer 在 syscall 前去 tag；
6. `/proc/self/maps` 感知的内存保护处理；
7. Android Mono 通过 `mono_compile_method` 取得真实 JIT body；
8. detour source 和 destination 都走 runtime-native body 解析；
9. executable JIT body 写入后调用 JunimoGate ARM64 cache helper；
10. net9-only、local MonoMod 和 JunimoGate package identity。

这些改动与 `native/JunimoGate.CacheFlush/clear_cache.S` 构成同一验收单元。只升级其中一部分没有意义。

## 4. 日常使用

### 4.1 前置环境

先安装项目本地工具链：

```bash
./build/bootstrap-android.sh
```

需要代理时：

```bash
JUNIMOGATE_PROXY_URL=http://127.0.0.1:10808 \
  nice -n 10 ionice -c2 -n7 \
  ./build/bootstrap-android.sh
```

### 4.2 快速验证现有包

```bash
./build/build-harmony-android.sh
```

若 ignored 本地包和 provenance 已存在，脚本只验证：

- tracked patch SHA-256；
- nupkg SHA-256；
- 包内 `lib/net9.0/0Harmony.dll` SHA-256；
- 不含意外的 netstandard 资产；
- provenance 与真实包字节一致；
- Harmony、MonoMod、iced commits 和归档哈希一致。

输出 `Patched Harmony package is current` 即表示当前本地包可用。

### 4.3 从固定源码强制重建

```bash
JUNIMOGATE_PROXY_URL=http://127.0.0.1:10808 \
  nice -n 10 ionice -c2 -n7 \
  ./build/build-harmony-android.sh --force
```

脚本会：

1. 验证 tracked patch 哈希；
2. 下载并验证三个固定源码归档；
3. 创建干净临时源码树；
4. 在源码根初始化临时嵌套 Git 仓库；
5. `git apply --check` 后应用 patch；
6. 要求至少一个源码文件发生变化；
7. 检查 Android 修复关键 marker；
8. restore/build/ILRepack/pack `Lib.Harmony.csproj`；
9. 生成 provenance；
10. 验证最终 nupkg 和内部 DLL 哈希。

固定哈希不匹配时，强制重建应该失败。这是供应链保护，不应通过修改脚本跳过。

### 4.4 构建消费方

```bash
./build/test-host.sh
./build/build-android.sh Debug probe
./build/build-android.sh Release probe
```

`test-host.sh` 和 probe Android 构建会先确保 patched Harmony 包存在。不要直接从 nuget.org 把 `Lib.Harmony` 换回官方 `2.4.2`。

### 4.5 真机验收

```bash
adb devices -l
./build/verify-runtime-probe.sh
```

必须同时通过 Debug 和 Release。权威成功结论为：

```text
stock-runtime-passed-with-harmony-monomod-fix
```

原始报告位于 ignored `artifacts/runtime-probe/`。最终 `.11` 成功报告和哈希见 [`runtime-probe-result.md`](runtime-probe-result.md)。

## 5. 构建流水线内部逻辑

简化伪代码：

```text
load pinned versions and hashes
verify tracked patch hash

if existing nupkg + provenance match every expected hash:
    return success

download and verify Harmony archive
download and verify MonoMod archive
download and verify iced archive
create clean source tree
initialize nested temporary git repository
check and apply tracked patch
assert source files actually changed
assert Android fix markers exist
restore/build/pack Lib.Harmony.csproj
write provenance
verify nupkg, internal DLL, assets and provenance
```

临时嵌套 Git 仓库不可删除。源码树位于父 JunimoGate 仓库的 ignored `.toolchains/` 下；若不初始化嵌套仓库，`git apply` 可能发现父仓库、忽略临时路径，并出现“退出成功但修改 0 个文件”的假成功。

## 6. 修改现有 patch

不要直接手工编辑 `.patch` 中的行号作为主要维护方式。推荐使用临时源码树生成差异。

### 6.1 建立干净基线

使用 `build/harmony-android-versions.sh` 中的 commits 和 archive hashes取得完全相同的：

```text
Harmony source/
Harmony source/LocalMonoMod/
Harmony source/LocalMonoMod/external/iced/
```

在该源码根初始化 Git，并提交未修改基线；身份只写入这个临时仓库：

```bash
git init
git config user.name "JunimoGate Patch Maintainer"
git config user.email "build@junimogate.invalid"
git add -A
git commit -m "Pinned upstream baseline"
```

该仓库仅用于生成 patch，不进入 JunimoGate 历史。

### 6.2 应用当前 patch

```bash
git apply /absolute/path/to/patches/harmony-android/harmony-2.4.2-android.patch
```

修改真实源码并在该临时树中完成构建测试。

### 6.3 使用新版本号

任何二进制变化都先把：

```xml
<HarmonyPrerelease>-junimogate.11</HarmonyPrerelease>
```

改成新的、从未使用过的版本，例如 `.12`。同时更新 RuntimeProbe 的 PackageReference 候选版本。

### 6.4 重新生成 patch

新文件需先让 Git 纳入 diff：

```bash
git add -N path/to/new-file.cs
git diff --binary --no-ext-diff > /tmp/harmony-android.patch
```

检查 patch 只包含预期文件：

```bash
git diff --stat
git diff --check
grep '^diff --git ' /tmp/harmony-android.patch
```

确认后再覆盖 tracked patch：

```bash
cp /tmp/harmony-android.patch \
  patches/harmony-android/harmony-2.4.2-android.patch
```

不得把完整 iced、Harmony 或 MonoMod 源码意外纳入 patch。

### 6.5 更新固定身份

更新：

```text
build/harmony-android-versions.sh
```

至少包括：

- `HARMONY_ANDROID_PACKAGE_VERSION`；
- patch SHA-256；
- 新 nupkg SHA-256；
- 新内部 `0Harmony.dll` SHA-256；
- 若上游变化，则更新对应 commit、URL 与归档 SHA-256。

生成最终哈希前可以在候选分支使用临时预期值，但不得把无法验证的占位值合入主线。

## 7. 升级 Harmony / MonoMod 上游

### 7.1 不直接替换 known-good

新版本首先作为候选：

```text
known-good: 2.4.2-junimogate.11
candidate:  新Harmony/MonoMod + candidate patch
```

保留 `.11` 本地包和成功报告，直到候选完成全部验收。

### 7.2 记录新上游身份

对每个更新组件固定：

- tag/commit；
- archive URL；
- archive SHA-256；
- LICENSE 和 notices；
- MonoMod/iced 子模块 gitlink commit；
- Harmony 实际内联的 MonoMod 来源。

禁止只改 NuGet 版本而不核对 Harmony 内联 backend。

### 7.3 在新基线上试应用旧 patch

```bash
git apply --check harmony-2.4.2-android.patch
```

可能结果：

1. **干净应用**：仍需逐项检查上游是否已实现重复修复；
2. **冲突**：按第 8 节处理；
3. **应用成功但逻辑已过时**：最危险，必须逐个审查 patch hunk 与上游实现。

### 7.4 删除已经上游的部分

若上游已经正确实现某项 Android 支持：

1. 从本地 patch 删除对应 hunk；
2. 增加或保留相关 probe case；
3. 验证上游实现通过相同设备测试；
4. 在结果文档记录“由本地 patch 转为上游实现”。

不要为了维持 patch 行数而保留重复修复。

### 7.5 候选验收顺序

```text
patch apply/check
→ source build + package identity
→ host 68/68（或更新后的总数）
→ Android Debug 全部 hard cases
→ Android Release 全部 hard cases
→ APK static verification
→ M6 SMAPI lifecycle
→ M7 代表性 Mods
→ 多设备矩阵
```

仅因为新版能编译，不能替换 `.11`。

## 8. Patch 冲突处理

### 8.1 先分类冲突

| 冲突类型 | 处理方式 |
|---|---|
| 文件移动/重命名 | 找到同一职责的新位置，重新生成 hunk |
| 上游已实现等价修复 | 删除本地 hunk，用 probe 验证上游实现 |
| API/抽象重构 | 按新抽象重写，禁止机械改行号 |
| Mono runtime entry 逻辑变化 | 重新执行只读 entry diagnostics，禁止盲写地址 |
| 内存保护/cache API变化 | 先验证 mapping 与helper自测，再改 PatchData |
| package/ILRepack变化 | 检查最终包依赖和内部 assembly，不假定仍是单DLL |

### 8.2 冲突解决原则

- 先理解上游新代码，再决定保留、改写或删除本地逻辑；
- 不使用 `git apply --reject` 后批量接受 `.rej`；
- 不用“只要编译成功”作为冲突解决标准；
- 不扩大为游戏或 Mod 特定 hack；
- Android 特殊分支应尽量不改变桌面 Harmony 行为；
- 每次冲突解决都用新 package version。

### 8.3 需要停止升级的情况

候选出现以下任一情况时停止升级并继续使用 known-good：

- 无法安全定位真实 Mono JIT entry；
- 需要写入未知/非 executable descriptor；
- native crash 或内存损坏；
- hard case 行为不再可观察；
- Release trimming only failure 未解释；
- 需要修改 stock Mono runtime 才能继续；
- 上游许可证、分发或 provenance 不清楚。

## 9. 常见脚本错误与排查

### 9.1 `Tracked Harmony patch SHA-256 mismatch`

原因：tracked patch 改了，但版本脚本未同步。

处理：

1. 审查 patch diff；
2. 使用新 package version；
3. 重新构建和真机验收；
4. 更新固定 patch/package/DLL hashes。

不要只把新 patch hash 写入版本脚本后继续使用旧 nupkg。

### 9.2 `git apply --check` 失败

原因可能是：

- 源码 commit 错；
- 归档内容不符；
- patch 基线与版本脚本不一致；
- 上游升级造成冲突；
- line-ending 或手工 patch 损坏。

处理：核对三个归档 hashes，确认 patch 文件完整，再在临时 Git 基线上分析冲突。不要使用模糊文本替换绕过。

### 9.3 `patch reported success but changed no source files`

说明临时嵌套 Git 仓库缺失或 Git 发现了错误的父仓库。保持脚本中的：

```bash
git init --quiet
git apply --check
git apply
git status --short
```

不要改回对 ignored child tree 直接执行 `git -C ... apply`。

### 9.4 缺少 MonoMod 或 iced 文件

GitHub source archive不自动包含 submodule。脚本必须分别下载：

- MonoMod fixed archive；
- iced fixed archive；

并放入 Harmony 预期路径。不要用本机随机 clone 或浮动 submodule HEAD 填充。

### 9.5 SDK 版本错误

patch 会把临时 Harmony `global.json` 固定到兼容的 .NET 9 SDK。若构建仍请求 SDK 10，说明 patch 未应用，不能通过安装系统 SDK 10 来掩盖问题。

### 9.6 构建成功但最终 hash 不匹配

可能原因：

- package version/source/patch变了；
- SDK、NuGet包或构建确定性发生变化；
- ILRepack输出变化；
- 生成包含时间戳或额外资产；
- 使用了错误的本地缓存包。

先比较 provenance 和 nupkg contents。若变化是预期升级，使用新版本并重跑验收；若输入声明相同却无法重现，不能发布该包。

### 9.7 `PackageReference` restore 找不到 patched Harmony

检查：

```text
NuGet.Config
artifacts/nuget/Lib.Harmony.<version>.nupkg
RuntimeProbe.Core PackageReference版本
```

运行：

```bash
./build/build-harmony-android.sh
```

`Lib.Harmony` 被 package source mapping 限定到 `junimogate-local`，不会从 nuget.org 获取同名下游版本。

### 9.8 Android `NotImplementedException`

若 stack 位于 `PlatformTriple.CreateCurrentSystem` 或 detour backend：

- 确认实际加载版本含 `junimogate.N`；
- 核对 RuntimeProbe report 的 Harmony informational version/MVID；
- 核对 package 内部 DLL hash；
- 确认 Android platform support case 先于 Harmony cases 通过。

不要直接得出 custom runtime 结论。

### 9.9 `DllNotFoundException` / `EntryPointNotFoundException`

核对 bionic resolver、`libc.so`/`libdl.so`、`__errno`、page-size selector。Android 不是普通 glibc Linux。完整异常链应保留在 RuntimeProbe JSON。

### 9.10 `mprotect EINVAL`

依次检查：

1. page size 是否为真实值（不是 1）；
2. syscall 地址是否去除 ARM64 top-byte tag；
3. `/proc/self/maps` 原 protection；
4. 是否对已经可写映射做了不必要的 mprotect；
5. 目标是否真实 executable JIT body，而非 scudo descriptor。

禁止在未确认目标地址前继续写入。

### 9.11 JIT bytes变化但行为不变

检查：

- 是否误用 Interpreter；
- `UseInterpreter=false` 与 `AndroidUseInterpreter=false` 是否都生效；
- AOT是否关闭；
- Mono entry是否来自`mono_compile_method`；
- native cache helper self-test是否通过；
- helper是否在patch写入后被调用。

### 9.12 Debug通过、Release失败

优先检查 trimming/linker。RuntimeProbe Release 需要保留：

- `0Harmony`；
- `MonoMod.Utils`；
- `MonoMod.Backports`；
- `MonoMod.ILHelpers`；
- `Mono.Cecil`；
- `mscorlib` facade。

不得用“关闭全部 trimming”替代精确根因分析，除非有独立设计决策。

### 9.13 Native helper alignment warning

当前 .NET Android SDK 的 XA0141 对大于16KiB但合法的幂次对齐有已知误判。构建脚本将 helper PT_LOAD 固定为恰好16KiB：

```text
-z max-page-size=16384
-z common-page-size=16384
```

不要简单 suppress warning；应验证 ELF program headers、APK ZIP_STORED 和 `zipalign -P 16`。

## 10. 验收门槛

候选 Harmony 包成为新基线前必须满足：

### Source/provenance

- 固定所有 commits、URLs 和 archive hashes；
- patch 可在干净源码上应用；
- patch diff 只含预期文件；
- LICENSE/THIRD-PARTY-NOTICES 更新；
- 新版本不可覆盖旧版本。

### Build

- package仅含预期TFM/资产；
- nupkg、内部DLL、patch hashes固定；
- provenance与真实字节一致；
- host build/test 0 warnings/errors；
- Android Debug/Release 0 warnings/errors；
- APK verifier通过。

### RuntimeProbe

- 明确JIT/no-interpreter/no-AOT；
- Debug全部hard cases通过；
- Release全部hard cases通过；
- JSON、logcat、设备/runtime/Harmony/MVID身份记录完整；
- 无native crash、半安装patch或静默fallback。

### Product integration

M2通过只允许进入后续阶段。正式升级还应经过：

- SMAPI无Mod启动；
- Content Pack、普通EntryDll和依赖型Mod；
- 至少首轮设备矩阵。

## 11. 回滚

始终保留 known-good 版本身份和证据。候选失败时：

1. 将消费项目 `PackageReference` 恢复到 known-good；
2. 恢复匹配的 patch/version constants；
3. 运行 `build/build-harmony-android.sh` 验证 known-good 包；
4. 运行host和至少一次设备Probe确认环境未变化；
5. 将候选失败原因写入技术记录，不覆盖旧包。

当前 known-good：

```text
Lib.Harmony 2.4.2-junimogate.11
nupkg SHA-256: a476d0a4d1b2cdfe47414225ea1e547ecb21ac0dddaa8a1e412a1673ffb66ac4
0Harmony.dll SHA-256: 240ec869c07564ec12fc212103ccbf642ee547c49d55f3db21e71bcdc9cf07a3
patch SHA-256: cfee9e3088008a2f434ae2b01a9f695668ba05c7df61a4ed5cba796aff5f95f6
```

## 12. 删除本地 patch 的条件

只有候选上游包在不应用本地 Android patch 时满足以下条件，才可删除 patch：

1. stock Mono JIT/no-interpreter/no-AOT；
2. RuntimeProbe Debug/Release 全部hard cases；
3. Android ARM64 cache维护有经过自测的等价实现；
4. bionic、page-size、tagged-pointer和Mono JIT entry均由上游覆盖；
5. GameHost和代表性Mods通过；
6. 多设备矩阵无回归；
7. 新包的许可证和provenance完整。

删除后仍应保留历史结果文档和旧版本身份，便于回归与审计。

## 13. 维护检查表

### 日常构建

- [ ] `./build/build-harmony-android.sh`
- [ ] package/provenance快速验证通过
- [ ] `./build/test-host.sh`
- [ ] Android构建按需执行

### 修改 patch

- [ ] 新 `-junimogate.N` 版本
- [ ] 干净基线生成diff
- [ ] patch只含预期文件
- [ ] 更新patch/package/DLL hashes
- [ ] 更新provenance/notices/docs
- [ ] Debug与Release真机重跑

### 上游升级

- [ ] 固定新commits与归档hashes
- [ ] 核对submodule gitlinks
- [ ] 标注哪些hunks已上游
- [ ] 解决冲突并生成候选版本
- [ ] 通过source/build/runtime/product验收顺序
- [ ] known-good保持可回滚

## 14. 文档索引

| 问题 | 文档/文件 |
|---|---|
| 最终M2结论和报告 | [`runtime-probe-result.md`](runtime-probe-result.md) |
| Android工具链与命令 | [`build-environment.md`](build-environment.md) |
| RuntimeProbe测试设计 | [`../tools/JunimoGate.RuntimeProbe/README.md`](../tools/JunimoGate.RuntimeProbe/README.md) |
| 实施阶段 | [`implementation-milestones.md`](implementation-milestones.md) |
| Patch本体 | [`../patches/harmony-android/harmony-2.4.2-android.patch`](../patches/harmony-android/harmony-2.4.2-android.patch) |
| 固定输入/成品身份 | [`../build/harmony-android-versions.sh`](../build/harmony-android-versions.sh) |
| 可重放构建实现 | [`../build/build-harmony-android.sh`](../build/build-harmony-android.sh) |
| ARM64 helper源码 | [`../native/JunimoGate.CacheFlush/clear_cache.S`](../native/JunimoGate.CacheFlush/clear_cache.S) |
| 第三方许可证 | [`../THIRD-PARTY-NOTICES.md`](../THIRD-PARTY-NOTICES.md) |
