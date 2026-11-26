---
name: bdtool-overview
description: 仅在用户明确提到 BDTool，或当前请求很可能需要通过 BDTool 完成时使用；用于确认 BDTool 可用性、项目上下文、能力范围与下一步操作。普通软件问答、未涉及 BDTool 的任务不触发。
---

# BDTool 总览

用于判断当前项目能否使用 BDTool、现在能做什么，以及下一步应该调用哪个操作。本 Skill 只负责入口检查与路由；Unity 项目的检查、Prefab 或资源配置修改交给 `bdtool-unity-work`。

## 触发边界

- 触发：用户提到 BDTool、询问 BDTool 状态或能力、要求通过 BDTool 查看或处理项目。
- 不触发：用户只问通用编程或通用 Unity 用法，且没有 BDTool 参与。
- 不凭经验假设项目已连接、Editor 已就绪或某个操作一定可用；以命令返回为准。

## 进入顺序

在项目根目录依次执行，先读真实结果再决定下一步：

```powershell
bdtool doctor --json
bdtool context --json
```

需要确认具体操作是否存在、是否可用时，再执行：

```powershell
bdtool operations list --json
```

只读查询失败时按 `references/troubleshooting.md` 处理；不要改用其他后端或手工命令绕过 BDTool。

## 结果判定

| 观察结果 | 处理 |
| --- | --- |
| `bdtool doctor` 退出码 `0`，项目与 Editor 检查通过 | 继续执行 `bdtool context --json` |
| `context.projectExists=false` 或退出码 `4` | 停止，向用户确认项目路径；不要猜测或替换项目 |
| Editor 未就绪，但 workspace 能力可用 | 只执行 workspace 范围操作；Unity 检查或写入留到 Editor 就绪后 |
| 所需能力缺失，或操作 `availability` 不是 `available` | 停止并说明缺口，不尝试底层替代方案 |
| 退出码 `6`（超时）或 `8`（结果不确定） | 停止自动重试；先回读状态，再向用户报告 |
| `warnings` 含审计不可用等提示 | 可继续只读工作，但在报告中保留该限制 |

## 不猜 Provider

- 只依据 `bdtool doctor` 与 `bdtool context` 返回的 providers、capabilities 和 editor 状态判断可用性，不根据项目类型或历史经验推断。
- 不直接调用底层编辑器、桥接或 Provider 命令；所有动作都通过 `bdtool` 完成。
- 需要操作明细时读取目录中的权威描述：

```powershell
bdtool operations describe <operationId> --json
```

## 写入路由

任何会修改项目文件的请求，先确认用户已明确授权项目、目标资产与变更内容，再交给 `bdtool-unity-work`。写入前必须 dry-run，写入后必须回读并做真实验证；未验证的结果不能报告为成功。

## 参考

- [troubleshooting.md](references/troubleshooting.md)
