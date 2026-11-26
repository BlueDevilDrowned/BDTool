# 0004 BDTool M1 运行时：Workspace 接线、能力、超时与 CLI

日期：2026-11-26

状态：M1 实现已接入

## 背景

[0003](./0003-bdtool-m1-workspace-contract.md) 冻结了 M1 的 Workspace 契约、路径策略、原子写入与 CLI 边界。本决策记录把这些契约接成**可实际运行**的 M1 运行时：真实 Handler、Provider 快照、能力检查、超时/取消、Activity 终态与 CLI 路由。

本决策不改变 0003 的文本编码、1 MiB 上限、CAS 与提交点语义；与旧设计文档冲突时仍以 0003 为准。

## 1. 可运行范围

M1 注册并实现八个操作：

| 操作 | 数据载荷 | 所需能力 | 说明 |
| --- | --- | --- | --- |
| `system.doctor` | `DoctorReport` | 无 | 检查应用/契约版本、项目目录、`.bdtool` 与 Activity 可写性、目录加载和真实 Provider 快照 |
| `operations.list` | `OperationsListData` | 无 | 返回目录中全部操作的 typed 摘要；输入只允许空对象 |
| `operations.describe` | `OperationsDescribeData` | 无 | 输入严格为 `{operationId}`；未知 ID `NOT_FOUND`，多余字段 `INVALID_ARGUMENT` |
| `project.context` | `ProjectContextData` | 无 | 离线项目事实；无 Unity 也成功，Editor 固定 `connected=false` |
| `workspace.read` | `WorkspaceReadResult` | `workspace.read` | UTF-8 文本读取，接受并剥离 BOM |
| `workspace.search` | `WorkspaceSearchResult` | `workspace.search` | 字面量搜索，排除 `.git/.bdtool/bin/obj/Library/Temp/Logs/UserSettings/node_modules/.vs` |
| `workspace.fingerprint` | `WorkspaceFingerprintResult` | `workspace.fingerprint` | 原始字节 SHA-256 与存在性 |
| `workspace.apply` | `WorkspaceChangeResult` | `workspace.write` | UTF-8 无 BOM 原子写入，CAS 与 dry-run |

所有业务 Data 都是 Contracts 中的固定 DTO；Operations 使用 `System.Text.Json` 严格解析输入，不返回 `object`、字典或原始 JSON 作为业务结果。目录 `resultSchema` 描述完整结果信封（`required: [status, data]`），`inputSchema` 一律 `additionalProperties: false`。

四个 `workspace.*` 操作不声明也不接受 JSON `projectPath`，项目边界只由 CLI `--project` 或组合根绑定决定。`workspace.fingerprint.relativePaths` 必填（空数组合法），`workspace.apply.content` 必填（缺失不等于空串，显式 `""` 才表示清空），`system.doctor` 与 `operations.list` 输入严格为空对象。

`workspace.apply` 的目录效果为写类（`workspace-write`）。CLI 在未提供 `dryRun` 时正常执行写入；Handler 仍支持请求级 `dryRun=true` 只做预检。

## 2. Provider 接线

```text
BDTool.Composition
  └─ new LocalWorkspaceKernel(projectPath)
       ├─ DescribeAsync() -> ProviderDescriptor ──┐
       │                                           ├─ DoctorHandler（providers 快照）
       │                                           ├─ CatalogCapabilityInspector（能力来源）
       │                                           └─ ApplicationGateway（快照展示）
       └─ IWorkspaceKernel -> OperationContextFactory -> Handler
```

- Composition 是应用侧唯一直接引用 `BDTool.Provider.Local` 的程序集。
- Application、Operations、CLI 只依赖 `IWorkspaceKernel`、`ProviderDescriptor` 与能力快照；没有 `providerId == "local"` 的分支。
- `OperationContextFactory` 注入 `IWorkspaceKernel`、`FileArtifactStore` 与 `CapabilityInspector`。Editor 在 M1 使用显式不可用实现，访问即失败。
- `ProjectContextData.Providers` 与 `DoctorReport.Providers` 来自组合根传入的真实描述，不再硬编码为空。
- `DescribeAsync().Limits` 暴露四项十进制字节/条数限制：`maxReadBytes`、`maxSearchFileBytes`、`maxSearchResults`、`maxApplyBytes`；值与 Provider 构造时快照的 `LocalWorkspaceOptions` 一致。

## 3. 能力检查

- 目录 `requiredCapabilities` 必须在已上报 Provider 能力中为 `true`；缺失时返回 `CAPABILITY_UNSUPPORTED`、`status=unsupported`、退出码 `3`。
- Provider 能力与目录操作本身的可用性合并成同一份 `CapabilitySnapshot`；未知能力一律视为不可用。
- Provider.Local 上报四个能力：`workspace.read`、`workspace.search`、`workspace.fingerprint`、`workspace.write`。`operations.*` 与 `project.context` 不要求额外 Provider 能力。

## 4. 超时、取消与 Activity

- 有效超时 `min(request.TimeoutSeconds > 0 ? request.TimeoutSeconds : catalog.TimeoutSeconds, catalog.TimeoutSeconds)`；请求只能缩短，不能延长。计算使用 `long` 秒数与 `TimeSpan.FromSeconds`，避免毫秒乘法溢出，并夹在 `CancelAfter` 支持的上限内。
- 调用方取消优先于超时：返回 `CANCELLED`、`status=cancelled`、退出码 `7`。
- 仅超时触发：返回 `TIMEOUT`、`status=failed`、退出码 `6`，`details.timeoutSeconds` 记录有效值。
- 两种终态都写入 Activity；ActivityId/TraceId 与调用上下文一致。
- Activity 写入失败只追加 `ACTIVITY_WRITE_FAILED` 告警，不改变业务结果。写操作在原子 Move 成功后即视为完成，之后的取消不会把结果改成失败。
- 原始序列化输入/输出超过 64 KiB（UTF-8 字节）时，将脱敏后的内容写入项目 Artifact，并在 Activity 中记录实际落盘 `bytes` 与 `sha256`；落盘后复验大小与哈希，失败只追加告警。
- 绑定项目根缺失时跳过 Activity 写入并追加 `AUDIT_UNAVAILABLE_PROJECT_MISSING` 告警，不创建目录；项目缺失事实仍保留在结果中。
- M1 只在终态持久化一次完整记录（含 StartedAt/FinishedAt）；Started 仅存在于内存，进程崩溃不会留下可查询的进行中记录。
- `activity list/show` 可读取记录；列表跳过损坏文件并保留诊断计数，`show` 对损坏文件返回明确的错误诊断。

错误码到退出码的稳定映射：

| 错误码 | 退出码 |
| --- | ---: |
| `INVALID_ARGUMENT` / `NOT_FOUND` | 2 |
| `CAPABILITY_UNSUPPORTED` | 3 |
| `PROJECT_NOT_FOUND` | 4 |
| `PRECONDITION_FAILED` / `EXECUTION_FAILED` | 5 |
| `TIMEOUT` | 6 |
| `CANCELLED` | 7 |
| `EXECUTION_UNCERTAIN` | 8 |

`WorkspaceException` 在 Handler 边界映射为上述原码与 Details，网关只做兜底，不把所有 Provider 异常折叠成 `EXECUTION_FAILED`。

## 5. CLI 契约

```text
bdtool doctor [--project <path>] [--human]
bdtool context [--project <path>] [--human]
bdtool operations list [--project <path>] [--human]
bdtool operations describe <id> [--project <path>] [--human]
bdtool run <id> [--input <file> | --input-json <json>] [--dry-run] [--project <path>] [--human]
bdtool <id> [--input <file> | --input-json <json>] [--dry-run] [--project <path>] [--human]
bdtool activity list|show <activityId> [--project <path>] [--human]
bdtool --version | --help
```

- 默认输出 JSON；`--json` 可省略，重复出现保持幂等；`--human` 只改变展示，不改变协议字段，二者不能同时使用。
- `--input` 必须是 UTF-8 JSON 文件路径，读入内容作为 `OperationRequest.InputJson`；`--input-json` 是内联扩展。二者互斥，缺文件或非法 JSON 返回 `INVALID_ARGUMENT`/退出码 `2`。
- `operations list/describe`、`context`、`doctor`、`run` 与 `<id>` 全部经 `IApplicationGateway`，因此共享能力检查、超时与 Activity 审计；解析阶段不直接读取目录。
- 参数/组合错误在默认 JSON 模式下仍向 stdout 写一个合法统一错误信封，退出码 `2`；stderr 只写诊断。正常调用 stdout 只有一个 JSON 文档。
- 所有失败结果都带 `data` 对象（缺省为 `{}`），CLI 不会为失败信封省略该字段。
- `workspace.*` 的 JSON 输入不接受 `projectPath`；`--project` 只决定绑定项目根。`activity list/show` 不写新 Activity，未知 ID 返回 `NOT_FOUND`，损坏记录给出诊断。
- 解析器拒绝把下一个选项当作值，重复 `--project`、`--catalog`、`--input`、`--input-json`、`--dry-run`、`--human` 会明确报错而不是静默覆盖。
- `Program` 接入 `Console.CancelKeyPress`，真实进程 Ctrl+C 取消当前调用并返回 `CANCELLED`/退出码 `7`。
- `activity list/show` 保留；未知 Activity ID 返回 `NOT_FOUND`/退出码 `2`。help/version 不写 Activity。

## 6. 明确限制

- M1 **不连接 Unity**：不启动 Editor、不读取场景/选中对象、不验证 Unity 版本兼容性。`project.context` 的 `unityVersion` 仅来自 `ProjectSettings/ProjectVersion.txt` 文本。
- 只做本地文本 Workspace；读取/搜索单文件上限 1 MiB，不做流式或分块传输，不写二进制。
- 跨进程 CAS 仍是“检查后原子替换”，无法消除检查与 Move 之间的竞争窗口；路径 reparse point 检查与打开之间仍有 TOCTOU 窗口。
- 不提供 CLI `--timeout` 覆盖；Activity 列表只返回最近 100 条摘要。
- `workspace.apply` 编码后上限 1 MiB，`dryRun` 同样受限；创建父链某段是普通文件时返回 `INVALID_ARGUMENT/parent-not-directory`，创建目标在二次预检后被其他进程创建时返回 `PRECONDITION_FAILED`。
- 项目缺失时按 0003 的审计优先策略处理：不创建项目目录，跳过 Activity 并告警 `AUDIT_UNAVAILABLE_PROJECT_MISSING`。

## 7. 验证基线

M1 最小收尾后在本工作区实际复跑（Release，2026-11-26）：

- `dotnet build BDTool.sln -c Release --nologo`：退出码 0，0 警告、0 错误。
- `dotnet test BDTool.sln -c Release --nologo`：退出码 0，`BDTool.Tests` 全量 337/337 通过，失败 0、跳过 0。
- `dotnet format BDTool.sln --verify-no-changes --no-restore --verbosity minimal`：退出码 0，无 whitespace/style 差异。
- `bdtool.dll --version`：退出码 0，输出 `bdtool 0.1.0 (contract 1.0.0, milestone M1)`。

以上数字均来自本次本地复跑，不引用任何外部交付记录。
