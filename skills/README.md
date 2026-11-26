# BDTool Skills

本目录提供两个可安装的 Codex Skill，让代理通过 BDTool 处理项目工作，而不是调用底层编辑器命令。

## 两个 Skill 的用途

| Skill | 触发场景 | 主要职责 |
| --- | --- | --- |
| `bdtool-overview` | 用户提到 BDTool，或请求很可能通过 BDTool 完成 | 先执行 doctor 与 context，确认项目、能力和下一步操作；不猜 Provider |
| `bdtool-unity-work` | 用户明确要求通过 BDTool 检查或修改 Unity 项目、场景、Prefab、资源配置或验证 | 操作描述 → dry-run → 限定写范围 → 执行 → 回读 → 验证 → 报告 |

普通软件问答、未涉及 BDTool 的任务不加载这两个 Skill；普通 Unity 问答也不触发 `bdtool-unity-work`。

## 安装

在目标项目根目录执行：

```powershell
bdtool skills install --scope project
```

该命令由 BDTool `0.4.0`/`M4` 及后续版本提供。旧版本不支持时先升级，不要手工复制文件替代安装。安装完成后重新加载 Codex 会话，确认项目 Skill 已进入可用列表再使用。

## 作用域

- `--scope project`：仅当前项目可用，Skill 随项目目录分发，不写入用户级 Skill 目录。
- 安装与更新都通过 `bdtool skills install` 完成，不手工改写已安装副本。
- 当前落盘位置固定为 `<project>/.agents/skills`；M4 不支持其他作用域。
- 首次安装返回 `updated=false`，受管更新返回 `updated=true`。未受管同名目录会使整次安装失败，并保留用户内容。

## 发现检查

安装后在目标项目根目录，可由支持项目级 Skill 的 Codex 版本检查模型可见输入：

```text
codex debug prompt-input "通过 BDTool 检查这个项目"
```

输出中的可用 Skill 列表应包含两个带 `.agents/skills` 绝对路径的条目。该检查证明发现结构已加载；实际是否触发仍由用户请求和 Skill 描述决定。

## 使用规则

- 所有检查与操作命令必须以 `bdtool` 开头。
- 禁止直接 Provider 命令、底层编辑器命令或桥接命令；需要确认能力时使用 `bdtool operations list --json`。
- 写入前必须获得用户明确授权，并完成 dry-run；写入后必须回读与真实验证，报告 `changedAssets`、`proof` 和未验证项。
- 不把超时或 `EXECUTION_UNCERTAIN` 描述为成功；先回读目标状态再决定下一步。

## 内容结构

```text
skills/
  README.md
  bdtool-overview/
    SKILL.md
    references/troubleshooting.md
  bdtool-unity-work/
    SKILL.md
    references/operation-contract.md
    references/verification.md
```
