# Android Mono 应用副本维护

JunimoGate 不维护 Mono 源码 fork。构建时以项目本地 .NET Android runtime pack
中的 ARM64 `libmonosgen-2.0.so` 为输入，生成 ignored 的应用私有副本；SDK
runtime pack 和原游戏 runtime 均不修改、不复制到仓库。

## 当前配方

配方实现在 `tools/JunimoGate.RuntimePack/Program.cs`，由
`build/build-mono-android.sh` 在 APK 构建前运行。

| ELF 符号 | 修改 | 已证实的必要性 |
| --- | --- | --- |
| `mono_method_can_access_field` | 对 Harmony 动态包装开放跨程序集非公开字段访问 | Android Mono 的访问检查会拒绝 SMAPI Mod 常见 Harmony 包装 |
| `mono_method_can_access_method` | 对 Harmony 动态包装开放跨程序集非公开方法访问 | 同上 |
| `mono_guid_to_string_minimal` | 仅在动态程序集没有 GUID heap 时使用零 GUID | Android ANR/线程栈诊断格式化动态方法时会解引用空 GUID |
| `mono_class_from_mono_type_internal` | 仅在 Reflection.Emit 产生零类型值的 fatal default arm 返回函数已有的 fallback class | SpaceCore serializer 生成后反射参数时触发 Mono assertion 和 `SIGABRT` |

最后一项来自旧 Android 启动器已经使用的 ARM64 修复语义，并针对当前 .NET
9.0.17 runtime 的符号大小和断言尾部重新定位。它不是 SpaceCore 专用 API：修复的
是 Mono 对 Reflection.Emit/DMD 生成方法执行 `GetParameters()` 时的原生崩溃。
SpaceCore + Ridgeside 只是暴露了这个通用 runtime 缺陷。

这些修改都发生在构建期。运行时没有额外扫描、包装或逐帧回调；正常 Mono
类型分支保持原样，只有原先会终止进程的 default arm 使用 fallback。

## 版本边界

当前 Reflection.Emit 配方严格要求：

- ELF64 little-endian ARM64；
- `mono_class_from_mono_type_internal` 大小为 `0x24c`；
- `+0x230` 到函数尾部仍是已审查的断言结构；
- 只在 `+0x23c` 写入四条 fallback 指令。

任何 .NET Android runtime 更新导致符号大小或指令结构变化时，构建必须失败。
不得仅修改偏移绕过失败；应重新反汇编该函数、确认寄存器含义和控制流，再建立
新配方。工具的 `--self-test` 覆盖受支持结构、精确输出以及结构变化时拒绝。

## 更新与验证

1. 更新项目本地 .NET Android runtime pack。
2. 运行 `build/build-mono-android.sh`；配方不匹配时先审查，不做模糊匹配。
3. 运行 `build/test-host.sh`，其中包含 RuntimePack 合成自检。
4. 构建 Debug/Release Android App。
5. 运行 Android artifact verification，确认应用私有 Mono 同时包含访问策略和
   Reflection.Emit fallback。

Harmony/MonoMod 的库级 Android 补丁是另一维护对象，见
[`harmony-android-maintenance.md`](harmony-android-maintenance.md)。
