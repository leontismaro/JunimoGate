# M3 Android 游戏发现验收记录

## 1. 结论范围

当前 Google Play 范围的 **M3 已完成**。

ARM64 test device（Android 16 / API 36 / ARM64）先验证了两个目标包均缺失时的 `package_not_found_or_not_visible` 分支，随后对已安装的 Play 包 `com.chucklefish.stardewvalley` 1.6.15.3/versionCode 245 完成 full-candidate 验收。JunimoGate 正确报告了一个当前 signer、base + ARM64 + Content 三个 APK 的完整 SHA-256、`modern-assembly-blob`/`game-content` 角色、`arm64-v8a` ABI，并通过结构和路径/设备标识脱敏检查。

Play app-signing certificate SHA-256 `c7b27f1faf2f350e3c117875bde2353cea837ebe1b3c2ce23513bb191d95852d` 已作为本项目的测试身份锚点。安装前，三个 APK 的 `apksigner` 结果均为该值；安装后，PackageManager 返回相同 current signer。真机 formatVersion 2 报告因此得到：

- `gameCertificateStatus: "KnownTested"`；
- `allowsCodeExecution: true`；
- `matchedKnownCertificateSha256` 等于上述测试锚点。

`KnownTested` 只表示“与本项目实际测试过的 Play 安装身份一致”，不是 Google、ConcernedApe 或发行方提供的独立认证。

## 2. 设计与数据边界

发现链固定为以下顺序：

1. `JunimoGate.App` 的最终 merged manifest 只声明两个 `<queries><package>`：
   - `com.chucklefish.stardewvalley`；
   - `com.chucklefish.stardewvalleysamsung`。
2. `AndroidPackageInstallationSnapshotProvider` 通过 application context 的 `PackageManager` 读取 package/version、base APK、全部 split APK 与签名信息：
   - API 28+ 使用 `SigningInfo`，区分多个当前 signer 与单 signer rotation history；
   - API 26–27 使用 `GET_SIGNATURES` / `PackageInfo.Signatures` 与旧 `VersionCode` 回退路径，代码和最低 API 声明保留，但尚未真机验收。
3. `GameInstallationDiscoveryCoordinator` 在扫描前后各取一次 package snapshot。package/version/signer/source identity 任一变化都会返回 `package_changed_during_scan`，不会混合更新前后的 APK。
4. `ApkSourceAnalyzer` 对每个 base/split APK 流式计算完整 SHA-256，只读取 ZIP entry 名称做角色与 ABI inventory；不复制 APK、DLL、Content 或 native payload。
5. split 按 PackageManager split name 排序后映射为稳定的 `base`、`split-N` 标签；产品报告不保存 source path 或 split path。
6. `KnownGameCertificate` 对 package + signing identity 做最小身份判断：
   - Play 包的单个 current signer 与测试锚点直接相同：`KnownTested`；
   - API 28+ Android 已验证的单 signer rotation history 在 current 之前包含测试锚点：`KnownTestedAfterRotation`；
   - Play 包的无关 signer 或任意多 current signer：`Unrecognized`；
   - 没有配置测试锚点的包：`NotConfigured`。
7. 只有 `KnownTested` 和 `KnownTestedAfterRotation` 的 `AllowsCodeExecution` 为 `true`。未知/未配置安装仍可生成 M3 candidate 和诊断，但 M4/M5 不得创建可激活 workspace 或执行其代码。
8. App 将 format version 2 JSON 原子写入 app-private `files/reports/game-discovery-latest.json`。报告只包含 package/version、证书判断、signer digest、APK hash/size、逻辑 source 标签、role、ABI 与结构化诊断；不包含 `sourcePath`、通用 `path`、设备 ID 或 adb serial。
9. `build/verify-game-discovery.sh` 使用项目本地 `android-env.sh`/adb 安装 JunimoGate App，通过 `run-as` 只拉取这份原始脱敏 JSON到 ignored `artifacts/android/game-discovery-<configuration>.json`。脚本根据 signer 与 rotation history 独立计算期望证书状态，不盲信 App 输出的 `allowsCodeExecution`。

最终 APK 静态验证还会检查：

- App artifacts 的 query package 集合精确等于上述两个包；
- 不含 `android.permission.QUERY_ALL_PACKAGES`；
- App 与 RuntimeProbe 都不含 `READ_EXTERNAL_STORAGE`、`WRITE_EXTERNAL_STORAGE` 或 `MANAGE_EXTERNAL_STORAGE`；
- query 判断依据 aapt xmltree 的 `manifest/queries/package` 层级，不会把根 `<manifest package="…">` 属性误判为 package query。

## 3. formatVersion 2 报告

顶层字段：

- `formatVersion`：当前固定为 `2`；
- `generatedAtUtc`：App 生成时间；
- `packageReports`：按 Play、Galaxy 顺序恰好两个结果；
- `candidates`：所有成功候选，不静默选择“首选商店”；
- `diagnostics`：`packageReports[*].diagnostics` 的扁平副本。

成功 candidate 包含：

- package、versionName、longVersionCode、selected ABI；
- `gameCertificateStatus`：`KnownTested`、`KnownTestedAfterRotation`、`Unrecognized` 或 `NotConfigured`；
- `allowsCodeExecution`：只有两个 KnownTested 状态为 `true`；
- `matchedKnownCertificateSha256`：匹配的测试锚点，未匹配时为 `null`；
- 当前 signer SHA-256 集合与 oldest-to-current rotation history；
- 每个逻辑 APK source 的 64 位小写十六进制 SHA-256、size、roles、native ABIs 与 AssemblyStore ABIs。

稳定 role 名称为：

- `game-content`；
- `legacy-assembly-blob`；
- `modern-assembly-blob`。

## 4. 实际错误码

以下名称直接来自 `GameDiscoveryErrorCodes`，文档不另造别名：

| 错误码 | 含义 |
|---|---|
| `package_not_found_or_not_visible` | 精确目标包未安装，或受 package visibility 限制不可见 |
| `metadata_invalid` | package/version 或 PackageManager snapshot 无法安全读取/验证 |
| `signing_info_missing` | PackageManager 没有提供可用签名信息 |
| `apk_source_missing` | 某个已报告 APK source 在扫描时缺失 |
| `apk_source_unreadable` | APK source 路径无效、无权读取或无法打开 |
| `apk_source_hash_failed` | 流式 SHA-256 计算失败 |
| `apk_source_invalid_zip` | APK 不是有效 ZIP，或 ZIP inventory 无法读取 |
| `content_source_missing` | 全部 source 都没有 `game-content` 角色 |
| `assembly_source_missing` | 全部 source 都没有受支持的 assembly 角色 |
| `abi_unsupported` | 没有可用的 ARM64 assembly source |
| `abi_conflict` | native 与 AssemblyStore ABI 证据冲突 |
| `split_identity_mismatch` | base/split path、split name、唯一性或 base identity 不一致 |
| `package_changed_during_scan` | 两次 snapshot 不一致，安装包可能在扫描中更新 |
| `game_certificate_unrecognized` | Play package signer 与测试身份无关；candidate 可诊断但不得执行代码 |
| `game_certificate_policy_not_configured` | 该 package 没有测试证书锚点；candidate 可诊断但不得执行代码 |
| `cancelled` | 用户/Activity 生命周期取消发现或扫描 |

## 5. 验收命令

```bash
# 68/68 host suite；五个 RuntimeProbe host tests 中有一个会执行十个 hard cases。
./build/test-host.sh

# App Debug/Release 构建。
./build/build-android.sh Debug app
./build/build-android.sh Release app

# 一次验证 RuntimeProbe/App 的 Debug/Release 四个 signed APK。
./build/verify-android-artifacts.sh

# 默认 Debug；也接受 Release 参数，但 app-private run-as 采集要求安装包允许 run-as。
./build/verify-game-discovery.sh Debug
```

`verify-game-discovery.sh` 要求恰好一台 online device；有多台设备时可设置 `ANDROID_SERIAL`。脚本只打印 model/API/ABI，不把 serial 或 device ID 写入报告。

脚本有两个成功分支：

- **missing-package branch only**：两个 package report 都只包含 `package_not_found_or_not_visible`。脚本成功，但这只证明缺包路径；
- **full-candidate branch**：至少一个 candidate 存在，并通过 certificate identity、signer/history、hash、source role/ABI、候选数量、报告结构与脱敏检查。

## 6. 当前已验证

截至 2026-07-24：

- host build/tests：五个 suite 共 68/68（12 + 40 + 5 + 6 + 5）；
- 证书规则 host tests：直接匹配、Android-verified 单 signer rotation、无关 signer、包含锚点的多 signer 拒绝、未配置 package；
- package/version/path/signer 两次 snapshot 竞态的确定性 host tests；
- JunimoGate App Debug/Release ARM64 build，0 warnings/errors；
- 四个 Android artifacts 的 package/API/ABI/signature/commercial-payload 静态检查；
- App merged manifest 的精确两个 query packages、无 `QUERY_ALL_PACKAGES`、无 broad storage 权限；
- ARM64 test device、Android 16/API 36、ARM64 上的 missing-package branch；
- 同一设备上的 Play full-candidate：`com.chucklefish.stardewvalley` 1.6.15.3/versionCode 245，selected ABI 为 `arm64-v8a`；
- PackageManager signer SHA-256 为 `c7b27f1faf2f350e3c117875bde2353cea837ebe1b3c2ce23513bb191d95852d`，与安装前三个 APK 的 `apksigner` 结果一致；
- 三个逻辑 source 的 SHA-256 分别为：
  - `base`：`763c2dc5681ec8f079d0b8be590b77d12c76027769ccef957f3ae4357c120837`；
  - `split-1`：`efa488b4ba5d5bead3924276a565eb992f022e11e03c7688b6921a484612eeb3`，识别为 ARM64 modern AssemblyStore；
  - `split-2`：`a90797b428249e040ecfc724168ba1310858855990136b5f813da6d456872d25`，识别为 game Content；
- formatVersion 2 真机报告得到 `KnownTested`、`allowsCodeExecution: true` 与匹配的测试锚点；
- `verify-game-discovery.sh Debug` 独立验证证书状态、signer、hash、source role、ABI、结构与脱敏并通过；
- 原始脱敏报告写入 ignored `artifacts/android/game-discovery-debug.json`，商业 APKS/APK 未复制进仓库或 artifacts。

十个 RuntimeProbe hard cases 是一个 RuntimeProbe host test case 内部的输出，不应与 host suite 的 53 个 test case 相加。M2 的 Android Debug/Release 真机结果仍见 [`runtime-probe-result.md`](runtime-probe-result.md)。
