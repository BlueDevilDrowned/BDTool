---
name: bdtool-unity-work
description: 用于用户明确要求通过 BDTool 检查或修改 Unity 项目、场景、Prefab、资源配置或验证结果时；所有读写都经 BDTool 执行。普通 Unity 问答，或未要求 BDTool 的编辑器操作不触发。
---

# 通过 BDTool 处理 Unity 工作

适用于用户明确把 Unity 项目检查、Prefab 或资源配置修改、结果验证交给 BDTool 的场景。未获得写入授权时只做只读检查。

## 前置条件

- 用户已给出项目路径与目标，例如场景、Prefab 或资源配置。
- 写入前必须获得用户对“项目 + 目标资产 + 预期变更”的明确授权；一次授权只覆盖当次目标。
- 先读后写。当前只支持单资产 Prefab 根对象 activeSelf 写入；场景写入、批量或多资产修改不在能力范围内。

## 工作流

### 1. 检查环境

```powershell
bdtool doctor --json
bdtool context --json
```

只有项目匹配、Editor 就绪且所需能力可用时继续；否则停止并报告缺口，不要猜测后端。

### 2. 读取操作契约

```powershell
bdtool operations describe project.asset-snapshot --json
bdtool operations describe project.prefab-set-active --json
```

以返回的 `inputSchemaJson` 和 `resultSchemaJson` 为准，不凭记忆拼字段。

### 3. 写前快照

```powershell
bdtool run project.asset-snapshot --input-json '{"assetPath":"Assets/Prefabs/Example.prefab"}'
```

记录 `sha256`、`rootObjectName`、`childCount` 和 `unityVersion`。资产不存在或不是目标资产时停止。

### 4. dry-run

在真实写入前必须执行，并核对结果：

```powershell
bdtool run project.prefab-set-active --input-json '{"assetPath":"Assets/Prefabs/Example.prefab","expectedSha256":"<写前 sha256>","isActive":false,"dryRun":true}'
```

确认 `dryRun=true`、`saved=false`、`verified=false`，允许 `changed=true`，但目标文件 SHA 必须未变化。dry-run 失败或前置条件不满足时停止，不进入真实写入。

### 5. 限定写范围并执行

- `assetPath` 只能是用户授权的单个资产；禁止通配符、资产列表和跨资产操作。
- `expectedSha256` 必须来自写前快照；出现 `PRECONDITION_FAILED` 时重新读取，不绕过 CAS。
- 仅在用户授权且 dry-run 通过后执行真实写入：

```powershell
bdtool run project.prefab-set-active --input-json '{"assetPath":"Assets/Prefabs/Example.prefab","expectedSha256":"<写前 sha256>","isActive":false}'
```

### 6. 回读与验证

```powershell
bdtool run project.asset-snapshot --input-json '{"assetPath":"Assets/Prefabs/Example.prefab"}'
bdtool run workspace.fingerprint --input-json '{"relativePaths":["Assets/Prefabs/Example.prefab"]}'
```

对照写入结果与回读结果：`changed=true`、`saved=true`、`verified=true`、写入结果中的 `isActive` 等于请求值、`newSha256` 不等于 `previousSha256`，且快照 SHA 与磁盘指纹都等于 `newSha256`。快照不回传 activeSelf，不能把 SHA 变化单独当作状态正确的证据；任一项不满足都按未完成处理。

### 7. 报告

报告必须包含：

- `changedAssets`：实际变化的资产路径；为空或与授权目标不符时视为未完成。
- `proof`：输入指纹、输出指纹和关键证据字段。
- 未验证项：未能检查的场景、批量资产、Editor 内实际表现或其他环境限制。
- 失败时附退出码与 `errors[].code`；`EXECUTION_UNCERTAIN` 不能描述为成功或普通失败。

## 边界

- 不直接调用底层编辑器、桥接或 Provider 命令；所有动作只经 `bdtool` 完成。
- 不支持场景写入、批量或多资产修改、播放模式控制；用户要求这些内容时说明当前能力缺口。
- 超时、取消或结果不确定时先回读目标状态，不盲目重试。

## 参考

- [operation-contract.md](references/operation-contract.md)
- [verification.md](references/verification.md)
