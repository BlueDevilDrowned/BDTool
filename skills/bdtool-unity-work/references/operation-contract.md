# Unity 操作契约摘要

本文件只覆盖当前已可用且与本 Skill 相关的操作。字段、能力与可用性以目录描述为准；升级后重新读取并覆盖本摘要。

```powershell
bdtool operations describe project.asset-snapshot --json
bdtool operations describe project.prefab-set-active --json
```

## 操作概览

| 操作 | effect | 环境 | 必需能力 | 用途 |
| --- | --- | --- | --- | --- |
| `project.inspect` | read | workspace + editor | `editor.status`、`editor.tool.read` | 读取当前层级，可限制深度与对象数 |
| `project.probe` | read | editor | `editor.status`、`editor.tool.read` | 读取 Unity 版本、平台、场景与脏状态快照 |
| `project.asset-snapshot` | read | editor | `editor.status`、`editor.tool.read` | 读取单个资产的类型化快照与 SHA |
| `project.prefab-set-active` | editor-write | editor | `editor.status`、`editor.tool.write` | 修改单个 Prefab 根对象 activeSelf |
| `workspace.fingerprint` | read | workspace | `workspace.fingerprint` | 读取磁盘文件存在性与原始字节 SHA |

## 输入契约

`project.asset-snapshot` 只接受一个字段：

```json
{"assetPath":"Assets/Prefabs/Example.prefab"}
```

`project.prefab-set-active` 输入严格，不接受额外字段：

```json
{"assetPath":"Assets/Prefabs/Example.prefab","expectedSha256":"<64 位 SHA>","isActive":false,"dryRun":true}
```

- `assetPath` 必填，必须是项目内单个资产路径。
- `expectedSha256` 必填，必须与写前磁盘指纹一致。
- `isActive` 必填布尔值，表示目标 activeSelf。
- `dryRun` 可选；省略即真实写入。

`workspace.fingerprint` 输入：

```json
{"relativePaths":["Assets/Prefabs/Example.prefab"]}
```

## 输出契约

所有操作返回统一信封：`status`、`data`、`changedAssets`、`warnings`、`errors`、`artifacts`、`proof`、`next`。

- `project.asset-snapshot.data`：`projectPath`、`assetPath`、`exists`、`sha256`、`rootObjectName`、`childCount`、`unityVersion`。
- `project.prefab-set-active.data`：`projectPath`、`assetPath`、`previousSha256`、`newSha256`、`wasActive`、`isActive`、`changed`、`saved`、`dryRun`、`undoGroup`、`verified`。
- `workspace.fingerprint.data.files[]`：`relativePath`、`sha256`、`length`、`exists`。

## dry-run 与 CAS

- dry-run 不创建 Undo 组、不保存、不修改资产；`Proof` 的输入与输出指纹都应等于写前 SHA。结果通常为 `changed=true`、`saved=false`、`verified=false`，其中 `changed` 只表示发现待执行差异。
- 真实写入前先校验 `expectedSha256`；不匹配返回 `PRECONDITION_FAILED`，且不会创建写入任务。
- 真实写入要求 `changed=true`、`saved=true`、`verified=true`，且 `newSha256` 与 `previousSha256` 不同。
- 提交后超时、取消或无法确认结果返回 `EXECUTION_UNCERTAIN`；先回读，禁止盲目重试。

## 明确的非范围

- 场景、批量或多资产写入。
- 播放模式控制。
- 任意组件或序列化字段修改。
- 通过底层编辑器命令绕过操作目录。
