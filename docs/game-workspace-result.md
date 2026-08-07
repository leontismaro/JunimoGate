# M4 游戏 workspace 验收结果

> **历史 M4 验收记录。** 本文记录当时的全量 CacheHit re-hash、重复安装身份验证和设备 verifier。当前启动与验证分层见 [`../AGENTS.md`](../AGENTS.md)、[`startup-chain.md`](startup-chain.md) 和 [`compatibility.md`](compatibility.md)。

## 1. 结论

M4 产品级 Content、Assembly 与 app-private workspace 管线已完成当前 Google Play 范围的实现与收口。它从 M3 的实时 `GameInstallationCandidate` 出发，在允许执行的已测试证书身份门槛之后：

- 对同一打开句柄上的全部 APK source 重新验证 size 与 SHA-256；
- 仅提取严格验证后的 `assets/Content/` 与所选 ABI 的 AssemblyStore v2 managed images；
- 在 app-private staging 中生成 payload 与三个 manifest；
- 对文件集合、大小、SHA-256、必需程序集、统计和 manifest identity 做完整验证；
- 以目录重命名提交 immutable workspace；
- 再次读取安装身份，确认 package/version/ABI/signer/source hash 未变化后才原子更新 active/previous state；
- 历史 CacheHit 路径仍逐文件重算完整 workspace payload SHA-256。

M4 **不执行 rewrite**，`rewrite-manifest.json` 的 recipe 为 `unrewritten:v1`、status 为 `not-applied`。M4 也**不加载或执行任何提取出的程序集**，不实现 Mono.Cecil。

## 2. Workspace 设计

固定 app-private runtime root 下使用以下布局：

```text
runtime/
  .workspace.lock
  workspace-state.json
  staging/<cache-key>-<nonce>/
  workspaces/<cache-key>/
  quarantine/<cache-key>-<timestamp>-<nonce>/
```

cache key 覆盖 package、longVersionCode、ABI、signer identity、全部 APK SHA-256、extractor schema、rewriter recipe 与 SMAPI build identity。workspace 一旦提交即视为 immutable；cache 验证失败时先隔离到 `quarantine/`，再从已安装源重建，不能在原目录就地修补。

每个已验证 workspace 的实际文件集合必须精确等于 extraction manifest 中的 payload，加上：

- `source-manifest.json`；
- `extraction-manifest.json`；
- `rewrite-manifest.json`。

三个 manifest 都从 payload file-set 中排除，并且 extraction manifest 的 payload 路径只能位于 `Content/` 或 `assemblies/`，因此 manifest 不能伪装为 payload。manifest 与 report 都只记录逻辑标签、相对路径、hash、size、schema 和状态，不记录 APK source path、workspace absolute path、设备 ID、adb serial 或 `/data/app` 路径。

`workspace-state.json` 使用 active/previous key。准备 B 时若最终安装重验证失败，已有 active A 和 previous 值保持不变。状态文件损坏时 activation 安全失败且不覆盖损坏内容，不猜测或激活不确定 workspace。

## 3. 安全边界

Content ZIP 预扫描和流式写入实施以下限制：

- traversal、absolute/drive/UNC、backslash、空段、控制字符、portable-invalid name、Windows reserved name 拒绝；
- Unicode NFC、大小写、file/directory collision 与跨 APK duplicate 拒绝；
- symlink 和特殊 Unix mode 拒绝；
- entry count、单文件 bytes、总 bytes、路径长度/深度与 compression ratio 上限；
- 实际流式输出 bytes 必须与 ZIP 声明长度一致。

AssemblyStore 只接受已支持的 v2/ELF64/XALZ 结构和所选 ABI；legacy-only 或 unsupported store 安全失败。整个 M4 不复制商业 payload 到仓库、host artifacts 或报告。设备 verifier 只把脱敏的 workspace report 复制到 ignored `artifacts/android/`；manifest、hash、listing、logcat 和中断恢复证据只存在于 `mktemp` 并由 trap 删除。

## 4. Report v2

App 将 `files/reports/game-workspace-latest.json` 原子写为 format version 2。根字段严格为：

- `formatVersion`；
- `generatedAtUtc`；
- `packageName`；
- `status`；
- `workspaceKey`；
- `statistics`；
- `metrics`；
- `progressStages`；
- `diagnostics`。

成功结果保留 extraction statistics，并增加：

- `durationMilliseconds`：`Stopwatch` 覆盖本次 prepare；成功时必须大于 0；
- `peakTemporaryBytes`：Built 为 commit 前 staging 中全部 payload 与三个 manifest 的总 bytes；CacheHit 为 0；
- `finalWorkspaceBytes`：已验证 workspace 中所有文件总 bytes。

`progressStages` 是 MainActivity 对本次实际收到事件按发生顺序去重后的 stage 名称。Built 必须真实包含证书/source 验证、Content/assembly 提取、manifest 写入、output 验证、commit、安装重验证、activation 与 completed；CacheHit 必须包含 cache validation、安装重验证、activation 与 completed，且不得声称重新提取或 commit。

失败结果允许 metrics 为空；diagnostics 继续使用稳定 error code 与脱敏消息。

## 5. 可重复命令

```bash
# 五个 host suites，共 68/68。
./build/test-host.sh

# 构建 Android App。
./build/build-android.sh Debug app

# 一台在线 ARM64、API 26+ 真机；默认总等待 1200 秒、
# 中断 staging 等待 300 秒，可通过对应 JUNIMOGATE_* 环境变量提高。
./build/verify-game-workspace.sh

# 提交前 whitespace 检查。
git diff --check
```

设备脚本先 clear JunimoGate App 自身数据，启动后轮询 app-private staging；观察到至少一个 payload 文件后，以应用自身 UID 对 JunimoGate PID 执行 `kill -9`，绕过 Activity 正常取消路径来模拟突然进程死亡，并通过设备端明确的 `exists/missing` 标记断言尚无 active state。重新启动必须得到 Built，且 interrupted stale staging 已清理；随后第二次启动必须得到 CacheHit。脚本还严格验证 report v2、三个 manifests、device-side payload hash、精确文件集合、metrics/progress、state，并只检查 JunimoGate App PID 对应的 logcat。

## 6. Host 与设备证据

本轮收口后的 host suite：

- Core：12/12；
- Extraction：40/40；
- Rewriter：5/5；
- Mods：6/6；
- RuntimeProbe：5/5；
- 总计：68/68，build 0 warnings / 0 errors。

Extraction tests 明确覆盖 compression-ratio 拒绝、rewrite manifest 严格字段/值与 cache 重验证、Built/CacheHit metrics、真实 progress 顺序、A 激活后 B 重验证失败不污染 state、损坏 state 不被覆盖、cache payload 全量 re-hash、staging 清理和 quarantine rebuild。

最终真机基线来自 ARM64 test device、Android 16/API 36、Google Play Stardew Valley 1.6.15.3/versionCode 245：

- 突然进程死亡：staging 已产生 payload 后由应用 UID 执行 `kill -9`；未生成 active state，下一次启动成功清理并恢复；
- 恢复后的 Built：约 40 秒端到端，`PrepareAsync` 内部 32675 ms；
- CacheHit：约 25 秒端到端，`PrepareAsync` 内部 16241 ms；
- Built peak temporary bytes：379260633；
- CacheHit peak temporary bytes：0；
- final workspace bytes：379260633；
- workspace key：`7faca811…9e24`；
- Content：3556 files / 360359752 bytes；
- assemblies：65 files / 17784880 bytes。

端到端耗时包括 App 启动、M3 发现、M4 prepare、报告写入和主机轮询；内部指标只覆盖 `GameWorkspacePreparer.PrepareAsync`。CacheHit 不重新复制 payload，但按安全约束重新读取并计算所有 workspace payload SHA-256，因此不是零成本。以上耗时和 key 是当前设备/输入基线，不是跨设备或跨版本固定常量；商业 payload 和设备路径均未写入文档或 artifacts。
