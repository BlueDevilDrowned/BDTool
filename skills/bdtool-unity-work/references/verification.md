# 写入验证清单

写入成功必须有可复核的证据，而不是只看退出码。以下检查全部通过 `bdtool` 完成。

## 1. 写前基线

```powershell
bdtool run project.asset-snapshot --input-json '{"assetPath":"Assets/Prefabs/Example.prefab"}'
bdtool run workspace.fingerprint --input-json '{"relativePaths":["Assets/Prefabs/Example.prefab"]}'
```

两次读取的 SHA 必须一致，且 `exists=true`。将快照 SHA 作为 `expectedSha256`。

## 2. dry-run 证据

- 返回 `dryRun=true`、`saved=false`、`verified=false`；`changed=true` 表示预览出待执行差异，不是磁盘已修改。
- 写前快照的 SHA 与写后立即回读的 SHA 相同。
- `proof.inputFingerprint` 与 `proof.outputFingerprint` 都等于写前 SHA。
- dry-run 不创建 Undo 组，不保存，不修改资产。

## 3. 真实写入后的必需证据

- 命令退出码为 `0`。
- `data.changed=true`、`data.saved=true`、`data.verified=true`。
- `data.isActive` 等于请求的 `isActive`。
- `data.newSha256` 不等于 `data.previousSha256`。
- `changedAssets` 恰好包含授权的资产路径；为空或包含其他路径时视为未完成。
- `proof.inputFingerprint` 等于写前 SHA，`proof.outputFingerprint` 等于 `data.newSha256`。

## 4. 独立回读

```powershell
bdtool run project.asset-snapshot --input-json '{"assetPath":"Assets/Prefabs/Example.prefab"}'
bdtool run workspace.fingerprint --input-json '{"relativePaths":["Assets/Prefabs/Example.prefab"]}'
```

- 快照的 `sha256` 与磁盘指纹都必须等于 `data.newSha256`。
- 快照不返回 activeSelf；activeSelf 的回读证据来自写入结果的 `verified` 与 `isActive` 字段。需要 Editor 内视觉确认时，标记为 BDTool 未验证并由用户检查。
- 场景表现、运行时行为、批量资产和其他被引用资源默认未验证，报告时逐项列出。

## 5. 失败处理

| 退出码 | 含义 | 处理 |
| ---: | --- | --- |
| `5` | 前置条件失败 | 重新读取快照与指纹；不要沿用旧 `expectedSha256` 重试 |
| `6` | 超时 | 立即回读目标；未确认前不报告成功，也不盲目重试 |
| `7` | 调用方取消 | 回读目标状态，按实际结果说明 |
| `8` | 结果不确定 | 停止自动重试；回读并请用户确认下一步 |

## 6. 报告模板

```text
结论：完成 / 未完成 / 结果不确定
changedAssets：<实际变化资产>
proof：inputFingerprint=<sha>，outputFingerprint=<sha>
验证：dry-run、真实写入、快照回读、磁盘指纹的逐项结果
未验证项：<场景表现、批量资产、Editor 内视觉确认等>
```

没有 `changedAssets`、`proof` 和回读结果时，不得声称写入成功。
