# 0011 BDTool M7.2/M7.3：原生 Agent 与独立 Session

- 状态：实现与真实宿主验证完成
- 日期：2026-11-27
- 产品版本：`1.2.0`，里程碑：`M7`

## 边界

BDTool 不是 Codex 的第二个调度器。应用只持久化 Formation 状态并生成结构化派发包；Codex Coordinator 调用宿主工具创建 Agent 或独立 Session，再把实际返回的 ID 回填给 BDTool。

以下事实只能由宿主提供，BDTool 不得自行生成：

- 原生子智能体 AgentId；
- 独立 SessionId；
- Agent 已运行；
- Agent/Session 完成结果。

`canSpawn=true`、`Dispatching` 或工具已排队都不等于 Running 或完成。

## 原生派发

```text
Ready
  → native-prepare
  → DispatchRequested / Dispatching
  → bind-agent(real AgentId)
  → Running
  → collect(host result reference)
  → Submitted
  → Passed
```

派发包包含：

```text
leafOnly=true
maxSubagents=0
maxDepth=1
agentProfile
prompt
workspace
acceptance
timeoutMinutes
```

提示词明确禁止叶子 Agent 创建子智能体、线程或继续委派。原生 Agent 预算来自 `formation.budget.subagentMax`，在创建 Attempt 前检查；Complete、Cancelled、Unsupported 或 DeliveryUnknown 后不再占用活动派发名额。

## 独立 Session

```text
Ready
  → session-prepare(userAuthorized=true)
  → DispatchRequested / Dispatching
  → bind-session(real SessionId)
  → Running
  → collect(host result reference)
  → Submitted
  → Passed
```

- API 与 CLI 必须显式提供 `UserAuthorized` / `--user-authorized` 和授权原话；缺失时返回 `PRECONDITION_FAILED`。
- 授权记录持久化 ID、授权人、时间、任务范围和原话，Attempt 引用该授权 ID；Bind 时再次校验。
- 创建前同时检查 `sessionMax` 与 `treeSessionMax`。
- SessionId 必须是宿主实际返回的真实线程标识。
- 独立 Session 仍按叶子任务运行，不得继续创建下一层会话。

## 绑定与提交

Bind 请求必须包含真实 ID 和结构化 `HostAttestation`：HostId、ToolCallId、CapturedAt、原始返回 SHA-256 与证据。AgentId/SessionId 在同一 Formation 内唯一。绑定成功后才把 Attempt 与 Task 转为 Running。

Running 任务只能通过 `collect` 回收，不能走人工 `submit`。回收时：

- Submission 记录 `ActorKind=Coordinator` 和 Coordinator ActorId；
- `AttributedAgentId` 记录绑定的 AgentId 或 SessionId；
- `HostResult` 记录 HostId、ThreadId、可选 turn/message ID、采集时间、内容 SHA-256 和声明者；
- 本地人工进程不能把 Submission 标成 Agent 自己产生；
- Attempt、Seat 和 Submission 共同形成可追踪历史。

审核通过后清空 Seat 的活动绑定，但 Attempt 保留真实 ID 与绑定证据。

## 取消

- 非 Running 任务收到 cancel 后可直接结束本地 Attempt。
- Running/Native/Session 任务先进入 `CancelRequested`，Attempt 同时进入 CancelRequested；不宣称远程已终止。
- `cancel-confirm` 只能接收宿主取消证据，并把任务与 Attempt 更新为 Cancelled。
- Unsupported 与 DeliveryUnknown 不允许被普通 cancel 覆盖；整个 Formation 只能取消收尾并保留原 Task 终态证据。

## 失败与未知

`unsupported`：

- 仅允许在 Dispatching 阶段记录；
- Attempt 进入 Unsupported；
- Task 进入 Unsupported；
- Formation 进入 Blocked，原因 `CAPABILITY_UNSUPPORTED`；
- CLI 返回 `CAPABILITY_UNSUPPORTED`/退出码 `3`。

`delivery-unknown`：

- Attempt 进入 DeliveryUnknown；
- Task 与 Formation 标记 Blocked，原因 `DELIVERY_UNKNOWN`；
- 保留活动写入范围和真实身份，避免结果未明时重派；
- CLI 返回 `EXECUTION_UNCERTAIN`/退出码 `8`。

两种状态均禁止自动重派。人工可通过整个 Formation 取消收尾，同时保留原 Task 终态证据。

## Agent Profile 安装

源目录 `agents/` 提供四个固定 Profile：

```text
bdtool-coordinator
bdtool-implementer
bdtool-reviewer
bdtool-session-worker
```

`bdtool agents install` 安装到 `<project>/.codex/agents`。所有目标先做冲突预检；只更新带 `# bdtool-managed: agent-profile-v1` 标记的文件。任何未受管同名文件都会使整次安装失败且零写入。安装不生成 Activity。

## 真实宿主验证

M7.2：

- 隔离 Formation：`E:\AI\BDTools\M7TestArea\host-native-v2`。
- Coordinator 使用宿主 `multi_agent_v1.spawn_agent` 创建叶子 Agent。
- 宿主真实返回 AgentId `01a218ef-52cf-7452-b3c2-bd1c0c7acb4b`。
- CLI `bind-agent` 回填 AgentId 与原始返回 SHA-256 的 HostAttestation 后进入 Running。
- Agent 只读返回 `Version=1.2.0`、`Milestone=M7`。
- CLI `collect` 记录 `ActorKind=Coordinator`、`AttributedAgentId` 与 HostResult；审核后 Formation Completed。

M7.3：

- 隔离 Formation：`E:\AI\BDTools\M7TestArea\host-session-v2`。
- 用户授权后使用宿主 `codex_app.create_thread` 创建独立 Session。
- 宿主真实返回 SessionId `01a218eb-b348-7e93-89ee-112060d1b0ce`。
- CLI `bind-session` 回填 SessionId、`create_thread` 证据哈希和持久化授权 ID 后进入 Running。
- 独立 Session 只读返回相同身份值。
- CLI `collect` 记录真实 SessionId 作为 `AttributedAgentId`，并记录实际 turnId、messageId 与内容 SHA-256；审核后 Formation Completed。

两个验证目录保留为结果证据；均不在 C 盘，也未修改 TTF2 或 BDTool 产品源码。

## 自动验证

- Native 派发包叶子约束和重放。
- 无真实 AgentId/SessionId 不得进入 Running。
- 结构化 HostAttestation、身份唯一性和 HostResult/绑定身份一致性。
- Running 结果只能由 Coordinator 通过 collect 回收，不能伪装成人工或 Agent 直接提交。
- Native/Session 预算在写状态前拒绝。
- Session 缺少或引用无效/过期用户授权时拒绝。
- Running 取消必须经过宿主证据确认；Unsupported/DeliveryUnknown 不会被 cancel 覆盖。
- Unsupported 退出码 `3`、DeliveryUnknown 退出码 `8`。
- 并发、损坏状态、CLI 回执投影与 Agent Profile 全量预检。

## 限制

- Codex 宿主工具不在 BDTool 进程内，应用无法单方面证明“宿主不存在工具”；Coordinator 必须用 `unsupported` 明确记录。
- BDTool 不能强制第三方宿主遵守 `leafOnly`；派发包、Profile 和 Coordinator 流程共同约束，审核仍需检查真实行为。
- Cancelling a host Agent/thread 依赖宿主提供的能力；BDTool 只记录本地任务取消，不宣称远程强制终止。
- M7.4 跨实例调度仍明确不支持。
- 不提供 Formation UI、MCP Formation Tool 或自动递归调度。
