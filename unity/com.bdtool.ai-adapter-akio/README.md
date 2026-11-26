# BDTool AI Adapter for Akio Unity CLI

`com.bdtool.ai-adapter-akio` 是 Editor-only 的薄适配包，把 `com.bdtool.ai-core`
的同步服务注册为 unity-cli bridge 的 project-local custom tools。

## 安装

包依赖：

- `com.bdtool.ai-core` `0.3.0`
- `com.akiojin.unity-cli-bridge` `0.19.1`

将两个 BDTool 包目录放入 Unity 项目，或在 `Packages/manifest.json` 中使用本地路径：

```json
{
  "dependencies": {
    "com.bdtool.ai-core": "file:E:/AI/BDTools/BDTool/unity/com.bdtool.ai-core",
    "com.bdtool.ai-adapter-akio": "file:E:/AI/BDTools/BDTool/unity/com.bdtool.ai-adapter-akio",
    "com.akiojin.unity-cli-bridge": "file:E:/path/to/com.akiojin.unity-cli-bridge"
  }
}
```

bridge 也可按项目既有方式嵌入或从 Git 安装；UPM 会按 `package.json` 校验
`0.19.1` 版本约束。安装后等待 Editor 完成导入与脚本编译，bridge 会在域重载后
自动发现本包的两个工具。

## 工具

| 工具 | Mutating | 参数 | 结果 |
| --- | --- | --- | --- |
| `bdtool_core_read` | `false` | `assetPath` | camelCase 资产快照 |
| `bdtool_core_prefab_set_active` | `true` | `assetPath`、`expectedSha256`、`isActive`、`dryRun` | camelCase 激活状态写入结果 |

`bdtool_core_read` 返回 `assetPath`、`exists`、`sha256`、`rootObjectName`、
`childCount`、`unityVersion`。`bdtool_core_prefab_set_active` 返回 `assetPath`、
`previousSha256`、`newSha256`、`wasActive`、`isActive`、`changed`、`saved`、
`dryRun`、`undoGroup`、`verified`。

示例：

```text
unity-cli tool list --category custom --output json
unity-cli tool call bdtool_core_read --json '{"assetPath":"Assets/Prefabs/Example.prefab"}'
unity-cli tool call bdtool_core_prefab_set_active --json '{"assetPath":"Assets/Prefabs/Example.prefab","expectedSha256":"<sha256>","isActive":false,"dryRun":true}'
```

`bdtool_core_prefab_set_active` 声明为 mutating，因此 `unity-cli --dry-run` 会跳过该
工具；只预览 Core 校验结果时，应实际调用并传入 `"dryRun": true`。

## 薄适配责任

Adapter 只检查参数引用非 null，然后把参数原样交给 Core，并把 Core 返回的 typed
DTO 转成匿名 camelCase 对象。以下规则全部由 `com.bdtool.ai-core` 负责，Adapter
不重复实现：

- `Assets/` 路径约束、`..`/越界拒绝与 `.prefab` 类型限制；
- `expectedSha256` 必填、大小写不敏感比较与 stale CAS 零副作用；
- Prefab 根对象 `activeSelf` 的读取、目标状态判断与写入；
- Undo 组、修改、保存、强制同步导入、重新加载回读与结果复核。

Core 抛出的异常不在 Adapter 内捕获或改写，由 bridge 统一映射为
`CUSTOM_TOOL_FAILED`（含异常消息与 `exceptionType`）。

## 程序集

`Editor/BDTool.UnityAdapter.Akio.Editor.asmdef` 仅启用 Editor 平台，引用
`BDTool.UnityCore.Editor` 与 `UnityCliBridge.Editor`；包不包含 Runtime 程序集，
不会进入播放器构建。
