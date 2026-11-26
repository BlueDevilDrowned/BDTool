# 0010 BDTool M7.1：人工 Formation 持久化与审核流程

- 状态：实现与自动验证完成
- 日期：2026-11-26
- 产品版本：`1.2.0`，里程碑：`M7.1`

## 目标与范围

M7.1 提供 Formation、Seat、Task、Attempt、Submission、Review 与 Artifact 的本地持久化状态，并由人工或 Coordinator 顺序推进。该阶段不创建 Codex 子智能体，不绑定真实 AgentId/SessionId，也不实现 M7.3 独立 Session 或 M7.4 跨实例调度。

## 状态目录与并发

每个 Formation 使用两个文件：

```text
<project>/.bdtool/state/formations/<formationId>.json
<project>/.bdtool/state/formations/<formationId>.lock
```

- 所有 mutation 在跨进程独占锁内完成读取、校验和写入。
- 状态通过 `AtomicFile` 在同目录创建临时文件、刷盘并替换目标。
- 无锁查询使用 `FileShare.ReadWrite | FileShare.Delete`；针对 Windows 替换窗口的短暂拒绝访问，Formation 写入口最多退避重试 20 次，每次 20 ms。
- 锁超过 5 秒返回 `PRECONDITION_FAILED` 且 `retryable=true`，不会伪装成普通执行失败。
- JSON 损坏返回 `FORMATION_STATE_CORRUPT`；`schemaVersion` 不等于 1 返回 `FORMATION_STATE_UNSUPPORTED`。两种失败均不覆盖原文件。

## 人工状态机

```text
Ready
  └─ DispatchRequested
       └─ ManualInProgress
            └─ Submitted
                 ├─ Passed
                 └─ NeedsRework
                      └─ Ready
```

- 人工路径不进入 `Running`。`Running` 只允许后续 M7.2 在宿主返回真实 AgentId 或 SessionId 后使用。
- Attempt、Submission 与 Review 记录 `ActorKind=Human` 和 `ActorId`。
- 失败审核必须提供 `reworkInstruction`；Passed review 不能包含 error 级 issue。
- 只能审核最新 Submission；历史 Submission、重复审核均返回 `PRECONDITION_FAILED`。
- 失败次数超过 `reviewRetryMax` 后，任务与 Formation 进入 `Blocked`，原因固定为 `REVIEW_RETRY_EXCEEDED`。
- 全部任务 Passed 后 Formation 进入 `Completed`。Completed/Cancelled 可 Archive；InProgress/Blocked 可 Cancel。

## requestId 幂等

每次 mutation 都保存回执：

```text
requestId
command
entityId
payloadSha256
recordedAt
```

`payloadSha256` 对排除顶层 `requestId` 的规范化请求 JSON 计算 SHA-256；属性按 ordinal 排序且不写空白。同一 requestId 的命令、实体或载荷不同均返回 `PRECONDITION_FAILED`，不会把旧结果伪装成新请求成功。重放返回当前状态，不返回历史快照。

## 写入范围与证据

- 席位和任务的 writePaths 使用 POSIX 项目相对路径；Windows 比较忽略大小写。
- 席位写入范围不能重叠；活动任务写入范围不能重叠；Blocked 任务仍占用范围。
- 任务范围必须位于 Executor 席位范围内；changedPaths 必须位于任务范围内。
- 拒绝项目外绝对路径、`..`、ADS、Windows 保留设备名、reparse point 与 `.bdtool` 内部目录作为写范围。
- Artifact 路径必须是项目相对路径，SHA-256 必须是 64 位十六进制。Artifact 可引用 `.bdtool/artifacts` 证据。

## CLI

```text
bdtool formation create
bdtool formation seat add
bdtool formation task create
bdtool formation task dispatch|start|submit|review|rework|cancel
bdtool formation cancel|archive
bdtool formation show|list
```

所有 mutation 要求 `--request-id`，支持 `--request-id auto` 并在 `data.requestId` 回显。`show` 默认隐藏 `requestReceipts`，只有 `--include-receipts` 显式返回。

CLI 默认值规则：席位或任务声明了 `--write-path` 而未显式给出 `--access` 时按 `write` 处理；`--role` 接受大小写不敏感的 Executor/Reviewer/Coordinator 并归一化为契约常量。

Formation 路由直接调用 Application Port，不经过 Gateway，因此不创建 Activity。Activity 继续只表示 Provider/AI 调用与 Job 审计。

## 自动验证

- 真实文件创建后由新服务实例重新读取。
- create 与生命周期 mutation 的同 requestId 重放、载荷冲突和非法 requestId 不落盘。
- 人工 dispatch → start → submit → review → rework → 第二轮 review 全流程。
- 历史 Submission 审核拒绝、retry 上限 Blocked、全部 Passed 后 Completed。
- 人工路径无 Running、无 AgentId/SessionId；Seat activeTaskId 正确占用与释放。
- 席位大小写重叠、活动任务范围、changedPaths 越界、traversal/ADS/保留名/项目外路径。
- 损坏 JSON、未知 Schema、并发读取与原子替换。
- CLI `0/2/5` 退出码、`--request-id auto`、`--human`、回执投影和零 Activity。

## 未实现

- M7.2 原生子智能体 spawn request、真实 AgentId 绑定、宿主结果回收。
- M7.3 用户明确授权后的独立 Session 创建、SessionId 绑定与回收。
- M7.4 跨实例调度。
- 自动递归子智能体、UI、MCP Formation 工具与需求版本提升命令。
