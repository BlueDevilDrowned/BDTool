# BDTool

BDTool 是面向个人 AI 工作流的本地工具。M1 已把本地 Workspace Provider 接入真实 Application：可查询操作目录与项目上下文，并在项目内执行文本读写、搜索、指纹与带 CAS 的原子写入。**M2 已完成**：新增可选的 `BDTool.Provider.Akio`，通过 `unity-cli` 在真实 Unity 6（6000.6.0f1）上验证 `system ping`、`project.inspect` 与 `project.probe` custom tool。**M3 已完成并真实验证**：新增只读 `project.asset-snapshot` 与单 Prefab 根对象 activeSelf 写入 `project.prefab-set-active`，目录 12 个操作全部 `available`；已在 Unity `6000.6.0f1` fixture 上跑通资产快照、dry-run、真实写入、stale CAS 拒绝与回滚恢复，Core EditMode 测试 25/25 通过。**M4 已完成**：提供可安装到项目 `.agents/skills` 的 `bdtool-overview` 与 `bdtool-unity-work` Codex Skill，包含防覆盖安装、Agent 使用边界和真实 CLI 验证。**M5 已完成**：新增 `bdtool-mcp` stdio Adapter，把 12 个 catalog 操作映射为高层 MCP Tool，并提供两个只读 Resource 与 Codex/JSON 配置生成。**M6 已完成并真实验证**：新增 `BDTool.Provider.Official`，通过 Unity 官方 `com.unity.pipeline@0.10.0-exp.1` 公共 HTTP API 支持状态、场景层级、Prefab 快照和 activeSelf 写入。**M7 已完成并真实验证**：Formation 支持人工闭环、原生子智能体派发包、真实 AgentId 绑定、结果回收、取消/DeliveryUnknown、用户授权下的独立 SessionId 绑定，以及项目级 Agent Profile 安装；应用自身不调用宿主私有接口，也不伪造 Agent/Session 身份。

## 当前状态

- 已冻结解决方案、程序集、命名空间与 CLI 可执行名：`BDTool` / `bdtool`。
- 产品版本 `1.2.0`，里程碑 `M7`；`BDToolIdentity` 与 `Directory.Build.props` 为 `1.2.0`。Akio Editor Provider 行为未变，仍声明实现版本 `0.3.0`。
- 目录共十二个操作：全部已注册 Handler 并标记为 `available`，包括 M1 的 `system.doctor`、`operations.list`、`operations.describe`、`project.context`、`workspace.read`、`workspace.search`、`workspace.fingerprint`、`workspace.apply`，M2 的 `project.inspect`、`project.probe`，以及 M3 的 `project.asset-snapshot`、`project.prefab-set-active`。
- M4 新增两个项目级 Skill。安装目标固定为 `<project>/.agents/skills/<skill>`；只有带合法 `.bdtool-managed` 标记的目录可被更新，未受管同名目录会使整次安装失败且不写入另一个 Skill。
- M5 新增 `BDTool.Mcp`，可执行名为 `bdtool-mcp`。MCP 工具直接来自 catalog，输入/输出 Schema 与 CLI 相同；调用统一进入 Application Gateway，并写 `source=mcp` 的 Activity。
- M6 新增 `BDTool.Provider.Official`。默认仍选择 `akio`；`--editor-provider official` 显式使用官方 Pipeline，不会与 Akio 交叉重试。
- `BDTool.Provider.Local` 上报 `workspace.read/search/fingerprint/write` 四个能力；Composition 是应用侧唯一引用该 Provider 的程序集。
- `BDTool.Provider.Akio` 为可选 Editor Provider，通过 `unity-cli` 上报 `editor.status`、`editor.tool.read`、`editor.tool.write`、`editor.job`、`editor.cancel`；Composition 同样是唯一引用它的程序集。未安装 `unity-cli` 时应用仍按 M1 本地能力运行。
- 每次有效调用都会尽力写入 Activity；`activity list` / `activity show` 可查询。
- `doctor` 与 `context` 只有在 Editor 报告 `ready` 且其挂载项目与请求项目一致时才显示 Connected/Pass；超时与 Provider 错误分别映射为稳定的未连接状态。
- 真实 Unity 6 端到端已验证：`system ping`、`project.inspect`、`project.probe` custom tool 均通过 `unity-cli`/Akio Provider/Gateway 产品链路执行，Activity 记录 `providerId=akio` 且状态 `completed`。
- M3 真实 Unity 6 端到端已验证：`project.asset-snapshot` 成功；`project.prefab-set-active` 的 dry-run、真实写入、stale CAS 拒绝与恢复均按预期工作（详见下文 M3 章节）。
- M4 已验证首次安装、二次更新、冲突防覆盖、默认 JSON 信封、发布目录 references 复制与 Codex 实际发现/触发边界；安装不会创建 `.bdtool` Activity。
- M5 已用官方 `ModelContextProtocol` SDK 客户端启动真实 stdio 子进程，验证 12 个工具、2 个 Resource、参数转发与 `project.context`/Activity，以及同一 `workspace.fingerprint` 请求在 CLI/MCP 返回相同结果信封。
- M6 已在隔离 Unity `6000.6.0f1` 项目验证官方状态、场景层级、Prefab 快照、dry-run、真实 activeSelf 写入、保存回读和 stale CAS；Akio 专属 `project.probe` 在 Official Provider 上明确返回 `CAPABILITY_UNSUPPORTED`。
- M7.1 状态文件位于 `<project>/.bdtool/state/formations/<formationId>.json`，使用独占锁和原子替换；同一 `requestId` 只有在命令、实体与规范化载荷 SHA-256 全部一致时才重放。
- M7.1 人工流程为 `Ready → DispatchRequested → ManualInProgress → Submitted → Passed|NeedsRework`；人工路径永不进入 `Running`，`AgentId`/`SessionId` 保持为空。`formation` mutation 不创建 Activity。
- M7.2 生成 `Native` 叶子派发包；只有 Codex Coordinator 调用宿主工具并把真实 AgentId 回填后才能进入 Running。预算耗尽返回冲突，DeliveryUnknown 以退出码 `8` 记录且禁止自动重派。
- M7.3 只在 API/CLI 显式携带用户授权且 Formation `sessionMax` 允许时生成 Session 派发包；真实 SessionId 回填后才进入 Running。M7.4 跨实例仍明确不支持。
- `bdtool agents install` 把固定的 Coordinator、Implementer、Reviewer 与 Session Worker Profile 安装到项目 `.codex/agents`；未受管同名文件冲突时全量预检、零写入。

## 目录

```text
BDTool.sln
Directory.Build.props
catalog/operations.json
skills/                     两个 Codex Skill 的权威源与 Agent 使用说明
src/BDTool.Contracts        BCL-only 公共 DTO、Port、错误与能力
src/BDTool.Core             不依赖外部系统的领域规则（路径策略、原子写、Skill 安装）
src/BDTool.Operations       目录加载/校验、输入解析与十二个 Handler（含 M3 Projects）
src/BDTool.Activity         Activity 存储与脱敏策略
src/BDTool.Application      用例运行时、能力检查、超时与退出码映射
src/BDTool.Orchestration    Formation 文件仓储、人工状态机与范围校验
src/BDTool.Provider.Local   本地 Workspace Provider（仅依赖 Contracts/Core）
src/BDTool.Provider.Akio    可选 unity-cli 只读 Editor Provider（仅依赖 Contracts）
src/BDTool.Provider.Official 官方 com.unity.pipeline Editor Provider（仅依赖 Contracts）
src/BDTool.Composition      唯一组合根
src/BDTool.Cli              bdtool 可执行入口
src/BDTool.Mcp              bdtool-mcp stdio MCP Adapter
tests/BDTool.Tests          单元、组合、CLI、MCP 与架构测试
docs/decisions              决策记录（0001-0011）
```

## 构建与测试

```powershell
dotnet build BDTool.sln -c Release
dotnet test BDTool.sln -c Release
dotnet format BDTool.sln --verify-no-changes --no-restore
```

架构测试直接解析真实 `.csproj` 的 `ProjectReference`，验证 Provider 依赖方向与 Composition 的唯一引用位置。

## CLI

默认输出 JSON，`--json` 可省略且幂等；`--human` 切换人类可读输出。所有 `operation` 路由都经过 Application Gateway，因此共享能力检查、超时与 Activity 审计；`activity list/show` 直接查询 Activity 存储，不生成新的 Activity。

Editor Provider 默认选择 `akio`。可使用 `--editor-provider official` 选择 Unity 官方 Pipeline；当前调用只装配所选 Editor Provider，另一 Provider 不参与 fallback。

Akio Provider 可用 `--unity-cli <exe>` 显式指定 `unity-cli`，用 `--unity-editor <exe>` 指定 Unity Editor。未显式指定时，`unity-cli` 按以下顺序发现：`--unity-cli` → `BDTOOL_UNITY_CLI_PATH` → 当前目录及祖先目录的 `.tools/unity-cli/win-x64/unity-cli.exe`（兼容平面 `.tools/unity-cli/unity-cli.exe`）→ 只读 PATH。显式路径或环境变量无效时直接报错而不回退；BDTool 不写入或修改系统 PATH。

```powershell
bdtool doctor [--project <path>] [--editor-provider akio|official]
bdtool context [--project <path>] [--editor-provider akio|official]
bdtool operations list [--project <path>]
bdtool operations describe workspace.read [--project <path>]
bdtool run workspace.read --input input.json [--project <path>]
bdtool run workspace.apply --input-json '{"relativePath":"Assets/a.txt","content":"hi","createIfMissing":true}' [--dry-run]
bdtool run project.asset-snapshot --input-json '{"assetPath":"Assets/Prefabs/A.prefab"}' [--project <path>]
bdtool run project.prefab-set-active --input-json '{"assetPath":"Assets/Prefabs/A.prefab","expectedSha256":"<sha256>","isActive":false,"dryRun":true}' [--project <path>]
bdtool workspace.fingerprint --input-json '{"relativePaths":["Assets/a.txt"]}'
bdtool skills install [--project <path>] [--scope project] [--human] [--json]
bdtool activity list [--project <path>]
bdtool activity show <activityId> [--project <path>]
bdtool formation create --id <id> --objective <text> --request-id <id|auto> [--project <path>]
bdtool formation show <id> [--include-receipts] [--project <path>] [--human]
bdtool formation list [--project <path>] [--human]
bdtool formation task native-prepare|session-prepare ...
bdtool formation task bind-agent|bind-session ...
bdtool agents install [--project <path>] [--human]
bdtool --version | --help
```

- `--input` 必须是 UTF-8 JSON 文件路径；`--input-json` 为内联扩展。二者互斥，缺文件或非法 JSON 返回 `INVALID_ARGUMENT`/退出码 `2`。
- `--unity-cli`/`--unity-editor` 缺少值或重复出现返回 `INVALID_ARGUMENT`/退出码 `2`。
- `operations list/describe` 进入 Gateway 并写入 Activity，不在解析阶段直接读目录。
- 参数/组合错误在默认 JSON 模式下仍向 stdout 输出合法统一错误信封；stderr 只写诊断。
- Ctrl+C 会取消当前调用并返回 `CANCELLED`/退出码 `7`。
- `--editor-provider` 只接受 `akio` 或 `official`；未知值返回 `INVALID_ARGUMENT`/退出码 `2`。
- `skills install` 是安装路由，不是 catalog operation；成功不创建 Activity，也不接受 `--unity-cli`、`--unity-editor`、`--catalog` 等未声明选项。
- `workspace.*` 的 JSON 输入不接受 `projectPath`；项目边界由 `--project`（或组合根绑定的项目根）决定。`workspace.fingerprint` 的 `relativePaths` 必填，`workspace.apply` 的 `content` 必填（显式 `""` 表示有意清空）。
- 失败信封始终包含 `data` 对象（缺省 `{}`），并保持统一信封字段（`status`、`warnings`、`errors`、`artifacts`、`proof`、`next`）。

退出码：

| 退出码 | 含义 |
| ---: | --- |
| `0` | 成功 |
| `2` | 参数错误或未找到（`INVALID_ARGUMENT`/`NOT_FOUND`） |
| `3` | 能力不支持（`CAPABILITY_UNSUPPORTED`） |
| `4` | 项目或环境未就绪（如 `PROJECT_NOT_FOUND`） |
| `5` | 执行失败（`PRECONDITION_FAILED`/`EXECUTION_FAILED`） |
| `6` | 超时（`TIMEOUT`） |
| `7` | 调用方取消（`CANCELLED`） |
| `8` | 结果不确定（`EXECUTION_UNCERTAIN`） |

## M4 Codex Skill（已完成）

### 安装与更新

在目标项目根目录执行：

```powershell
bdtool skills install --scope project
```

也可以从其他目录显式指定项目：

```powershell
bdtool skills install --project <项目路径> --scope project
```

CLI 从自身发布输出的 `skills/` 读取固定源，并安装到：

```text
<project>/.agents/skills/bdtool-overview/
<project>/.agents/skills/bdtool-unity-work/
```

- 首次安装的两个结果均带 `updated=false`；再次安装会受管刷新并带 `updated=true`。
- 已存在但不是 BDTool 管理的同名 Skill 目录会返回统一 JSON 信封、`PRECONDITION_FAILED`、退出码 `5` 和 `data.conflicts`；整次安装不会覆盖或部分写入另一个 Skill。
- `.bdtool-managed` 记录格式版本、Skill 名和源树 SHA-256。源 Skill、目标目录及子项会检查路径逃逸与 reparse point。
- M4 只支持 `--scope project`；`user` 或未知作用域返回 `INVALID_ARGUMENT`/退出码 `2`。
- Skill 安装不是 catalog operation，不写 Activity，也不会创建 `<project>/.bdtool`。

安装或刷新后重新加载 Codex 会话，再在目标项目中确认两个 Skill 已被发现。

### Agent 使用边界

- `bdtool-overview`：仅在用户明确提到 BDTool，或请求很可能需要通过 BDTool 完成时使用。先调用 `bdtool doctor --json` 与 `bdtool context --json`，按真实 Provider、能力和 Editor 状态决定后续。
- `bdtool-unity-work`：仅在用户明确要求通过 BDTool 检查或修改 Unity 项目、场景、Prefab、资源配置或验证结果时使用。写入必须获得当次项目、目标资产和预期变更授权，并依次完成快照、dry-run、真实写入、回读与验证。
- 普通编程或普通 Unity 问答，在没有 BDTool 参与时，不触发这两个 Skill。
- Skill 只调用 `bdtool` 稳定 CLI；不暴露或调用 `unity-cli`、具体 Provider 类或底层桥接命令。
- 当前 Unity 写入范围仍是单 Prefab 根对象 activeSelf；场景写入、批量资产与播放模式控制明确不在范围内。

参见 [skills/README.md](skills/README.md)、[项目总览 Skill](skills/bdtool-overview/SKILL.md) 与 [Unity 工作 Skill](skills/bdtool-unity-work/SKILL.md)。

## M6 官方 Pipeline Provider（已完成并真实验证）

官方 Provider 通过 Unity Pipeline 发布的实例描述连接当前项目：

```text
<project>/Library/Pipeline/.unity-pipeline-port
```

运行时只读取本机 loopback 地址、进程信息与认证令牌；令牌不进入 ProviderDescriptor、错误详情、Activity 或日志。项目不包含 Pipeline 实例时，状态返回 `not-found`，操作明确失败。

支持的公开操作：

- `project.inspect`：调用官方 `scene get_hierarchy`，转换为既有 typed 层级。
- `project.asset-snapshot`：调用官方 `script eval` 中的固定只读脚本。
- `project.prefab-set-active`：调用固定写脚本，执行 CAS、Prefab 内容加载、保存、同步导入和回读。
- `project.probe`：依赖 Akio 自定义 probe，Official Provider 明确返回 `CAPABILITY_UNSUPPORTED`。

官方 Provider 的上游命令不作为 BDTool 公共工具暴露。CLI、MCP 与 Skill 仍只使用 catalog operation ID；业务层、Skill 与 MCP 工具目录未因 Provider 切换而改变。

```powershell
bdtool context --project <项目路径> --editor-provider official
bdtool run project.inspect --project <项目路径> --editor-provider official --input-json '{}'
bdtool run project.asset-snapshot --project <项目路径> --editor-provider official --input-json '{"assetPath":"Assets/Prefabs/A.prefab"}'
```

真实验证环境：

- Unity `6000.6.0f1`，独立 fixture `E:\AI\BDTools\M6TestArea\PipelineFixture`。
- `com.unity.pipeline` `0.10.0-exp.1`。
- `context` 报告 Editor ready、`providerId=official` 和正确的 Unity 版本。
- `project.inspect` 返回真实场景层级。
- Prefab dry-run 返回 `saved=false`、`verified=false` 且 SHA 不变；真实写入返回 `saved=true`、`verified=true`，磁盘 SHA 从 `bc91…` 变为 `231d…`。
- stale CAS 以退出码 `5` 拒绝，磁盘 SHA 保持不变。
- `project.probe` 明确返回 `CAPABILITY_UNSUPPORTED`/退出码 `3`，未 fallback 到 Akio。
- Activity 记录 `providerId=official` 与实际操作和 changedAssets。

## M7 Formation 与 Agent/Session（已完成并真实验证）

M7.1 提供人工与 Coordinator 顺序推进；M7.2/M7.3 在此基础上生成宿主派发包并回填真实身份。完整命令：

```text
bdtool formation create ...
bdtool formation seat add ...
bdtool formation task create ...
bdtool formation task dispatch|start|submit|review|rework|cancel ...
bdtool formation task native-prepare|session-prepare|bind-agent|bind-session ...
bdtool formation task unsupported|delivery-unknown ...
bdtool formation cancel|archive ...
bdtool formation show|list ...
bdtool agents install ...
```

- 所有 mutation 要求 `--request-id`，也接受 `auto` 并在 `data.requestId` 回显实际值。
- 声明 `--write-path` 而未给 `--access` 时按 `write` 处理；`--role` 大小写不敏感并归一化为 `Executor`/`Reviewer`/`Coordinator`。
- 状态文件默认隐藏 `requestReceipts`；仅 `show --include-receipts` 显式返回。
- `changedPaths` 必须位于任务写入范围；席位和活动任务写入范围不能重叠，Windows 比较忽略大小写。
- `../`、项目外绝对路径、ADS、Windows 保留设备名、reparse point 与 `.bdtool` 内部目录作为写范围时均拒绝。
- 失败审核必须带返工要求；只能审核最新 Submission，历史 Submission 和重复审核均拒绝。
- 达到 `reviewRetryMax` 后将任务与 Formation 标记为 `Blocked`，原因固定为 `REVIEW_RETRY_EXCEEDED`。
- Formation 状态与 review 历史原子持久化；损坏 JSON 返回 `FORMATION_STATE_CORRUPT`，未知 Schema 返回 `FORMATION_STATE_UNSUPPORTED`，均不覆盖原文件。
- 原生派发包固定 `leafOnly=true`、`maxSubagents=0`、`maxDepth=1`，提示词禁止继续委派或创建线程。
- `session-prepare` 未获得显式用户授权时返回 `PRECONDITION_FAILED`，`sessionMax/treeSessionMax` 在创建 Attempt 前检查。
- 真实验证使用 `E:\AI\BDTools\M7TestArea\host-native-v2` 与 `host-session-v2`：原生 AgentId 与独立 SessionId 均由宿主工具实际返回、经结构化 attestation 回填、通过 `collect` 收集结果并审核完成；没有手工生成 ID。

阶段设计与限制详见 [0010](docs/decisions/0010-bdtool-m7-1-formation.md) 与 [0011](docs/decisions/0011-bdtool-m7-runtime-formations.md)。

## M5 MCP Adapter（已完成）

### 工具与 Resource

`bdtool-mcp` 默认运行 stdio MCP Server。它只广告 catalog 中已实现且 `availability=available` 的操作，当前共 12 个 Tool：

- 操作 ID 就是 MCP Tool 名称，例如 `system.doctor`、`workspace.read`、`project.context`、`project.prefab-set-active`。
- Tool 的输入和输出 Schema 直接复用 `catalog/operations.json`，不维护第二份 MCP Schema。
- 调用参数对象直接映射为 `OperationRequest.InputJson`；结果返回同一个统一 JSON 信封到文本内容和 `structuredContent`。
- 未知 Tool 进入 Gateway 后返回稳定 `NOT_FOUND`，不会回退到 Provider 或底层编辑器命令。
- Provider 名称、`unity-cli` 命令和桥接私有工具不会出现在 MCP Tool 列表中。

当前 Resource：

```text
bdtool://catalog/operations   当前公开操作目录与完整 Schema
bdtool://project/context      当前项目、Editor 与 Provider 能力快照
```

`project.context` Resource 与同名 Tool 共用 Application，读取时也会生成来源为 `mcp` 的 Activity。

### 启动

```powershell
dotnet src\BDTool.Mcp\bin\Release\net10.0\bdtool-mcp.dll --project <项目路径>
# 或安装/发布后
bdtool-mcp --project <项目路径>
```

可选参数：

- `--catalog <file>`：显式操作目录；默认使用发布目录 `catalog/operations.json`。
- `--help`、`--version`：只输出入口信息，不启动 MCP。

### 配置生成

生成通用 JSON 配置：

```powershell
bdtool-mcp config --project <项目路径> --format json
```

生成 Codex `config.toml` 片段：

```powershell
bdtool-mcp config --project <项目路径> --format codex
```

生成器只输出启动 `bdtool-mcp` 所需的 `command`、`args`、项目 `cwd` 和启动超时，不写入凭据、模型或 Provider 配置，也不会直接修改用户配置。

## Workspace 语义

- 读取与搜索只处理 UTF-8 文本；开头 BOM 被剥离，`Sha256` 始终对磁盘原始字节计算（包含 BOM）。
- 读取恰好 1 MiB 允许，超过返回 `INVALID_ARGUMENT`。写入按 UTF-8 编码后字节数计，`maxApplyBytes` 默认 1 MiB，`dryRun` 同样受限。
- 搜索排除 `.git/.bdtool/bin/obj/Library/Temp/Logs/UserSettings/node_modules/.vs`；空或全空白 `query` 返回 `INVALID_ARGUMENT`，超过 `maxResults` 时 `truncated=true`，打开候选前复检 reparse。
- `workspace.apply` 写 UTF-8 无 BOM，使用同目录临时文件 + 原子 Move；覆盖已有文件必须提供 `expectedSha256`，陈旧 CAS 返回 `PRECONDITION_FAILED` 且无副作用。
- 创建使用非覆盖提交，二次预检后目标被创建返回 `PRECONDITION_FAILED`；父链某段是普通文件返回 `INVALID_ARGUMENT`/`parent-not-directory`；更新仍保留已承认的 CAS→Move 窗口。
- `dryRun=true` 只做路径、大小、CAS 与父链结构预检，不创建目录、临时文件或目标。
- 有效超时为 `min(请求值, 目录 timeoutSeconds)`；超时 `TIMEOUT`/`6`，调用方取消 `CANCELLED`/`7`，两者都写 Activity 终态。

## Editor（M2、M3 均已完成并真实验证）

- `project.inspect` 环境为 `workspace` + `editor`，要求 `editor.status` 与 `editor.tool.read`；通过 `get_hierarchy` 只读读取层级，参数 `maxDepth` 范围 `0..32`、`maxObjects` 范围 `1..10000`。
- 结果为 DTO 结构：`projectPath`、`unityVersion`、`sceneName`、`isPrefabMode`、`objectCount` 与递归 `children` 节点；上游未提供版本时 `unityVersion` 为空字符串。
- `project.probe` 环境仅为 `editor`，要求 `editor.status` 与 `editor.tool.read`；输入严格为空对象，结果为 typed 快照：`product`、`unityVersion`、`platform`、`sceneName`、`scenePath`、`sceneIsDirty`、`rootObjectCount`、`observedAtUtc`。
- 真实 Unity 6 验证环境：Unity `6000.6.0f1`，fixture 项目位于 `E:\AI\BDTools\M2TestArea`；原 TTF2 未修改。
- `unity-cli`/bridge 版本 `0.19.1`，`versionCheck` 匹配通过；`system ping` 成功。
- `project.inspect` 通过 `get_hierarchy` 读取真实层级；`project.probe` custom tool 返回 Product、UnityVersion、Platform、SceneName、ScenePath、SceneIsDirty、RootObjectCount、ObservedAtUtc 全部 typed 字段。
- 对应产品调用写出 Activity，记录 `providerId=akio`、状态 `completed`。
- `refresh_assets` 之后实际观察到 1 次 ping 失败，随后恢复；该瞬态失败未产生错误的成功结论，也未改变只读边界。
- 本节两个操作当前仅只读：不修改场景、资源或 Editor 状态，不提供播放模式控制，也不写入项目文件；不覆盖全部 Unity 工具。M3 的唯一写入能力见下节。

## M3 Prefab（已完成并真实验证）

- `project.asset-snapshot`：环境仅 `editor`，要求 `editor.status` + `editor.tool.read`；输入严格为 `{assetPath}`，`Mutating=false`、空 `WriteScope`，通过 Editor Provider 调用 `bdtool_core_read`，并把结果严格收敛为 `projectPath`、`assetPath`、`exists`、`sha256`、`rootObjectName`、`childCount`、`unityVersion` 七个 typed 字段。
- `project.prefab-set-active`：环境仅 `editor`，要求 `editor.status` + `editor.tool.write`；输入严格为 `{assetPath, expectedSha256, isActive, dryRun?}`，`isActive` 必填布尔值，`additionalProperties=false`。
- operation ID 使用连字符形式；上游 tool 名保持 `bdtool_core_read` 与 `bdtool_core_prefab_set_active` 不变。
- 写前 CAS：Submit 之前先用 Workspace 指纹确认 `expectedSha256` 与磁盘一致；不一致返回 `PRECONDITION_FAILED`，且不创建 Editor Job。
- 写请求：`Mutating=true`、`WriteScope=[assetPath]`、`ExpectedFingerprint` 与请求/输入 dry-run 并集一并下发；上游执行 Undo 组、保存、强制同步导入与回读验证。
- 写后证据：非 dry-run 要求 `changed=true`、`saved=true`、`verified=true`、`isActive` 回读等于请求值且 `newSha256 != previousSha256`，并再次读取 Workspace 指纹作为磁盘证据；`ChangedAssets`、`Proof.InputFingerprint`、`Proof.OutputFingerprint`、`Evidence` 与 warnings 由真实结果填充。
- dry-run：不创建 Undo 组、不保存、不修改资产，也不要求文件 SHA 变化；`Proof` 的输入/输出指纹都等于 pre-SHA，结果只描述 activeSelf 预检。
- 提交后超时、取消或无法确认写入结果返回 `EXECUTION_UNCERTAIN`/`Uncertain`，不伪装成可安全重试的普通失败。
- 仅支持单个 Prefab 根对象 activeSelf；不支持 multi-asset、bulk、场景写入或播放模式。M4 Skill 不扩大这些项目操作范围。
- 验证环境：Unity `6000.6.0f1`，fixture `E:\AI\BDTools\M3TestArea\Unity6Fixture`；两个 BDTool 包以 embedded 方式安装成功。
- Core EditMode 过滤测试 25/25 通过，结果文件 `E:\AI\BDTools\M3TestArea\Results\core-editmode-final2.xml`（`result="Passed"`）。
- adapter tools schema 如实上报：`bdtool_core_read` 为 `mutating=false`，`bdtool_core_prefab_set_active` 为 `mutating=true`。
- `project.asset-snapshot` 在真实 Prefab 上成功返回 typed 快照。
- dry-run 实测 `Saved=false` 且文件 SHA 不变；真写实测 activeSelf `true` → `false`、`Saved=true`、`Verified=true`、SHA `c11...` → `031...`；stale CAS 以 exit `5`/`PRECONDITION_FAILED` 拒绝且 SHA 不变；恢复 activeSelf `true` 后 SHA 回到 `c11...`。
- Activity 记录 `providerId=akio`、状态 `completed`、`changedAssets=Assets/BDToolM3/ActivationProbe.prefab`；`refresh_assets` 后观察到 1 次 ping 失败随后恢复，新工具重新发现并完成写入。

## Activity

- 每次有效调用都会尽力写 Activity 终态；原始序列化输入/输出超过 64 KiB（UTF-8 字节）时，将脱敏后的内容写入项目 Artifact，摘要记录落盘内容的实际字节数与 SHA-256，并在落盘后复验。
- Started 仅存在于内存，Activity 只在终态持久化一次；进程崩溃可能丢失进行中的记录。
- 项目根缺失时不创建目录，跳过审计并告警 `AUDIT_UNAVAILABLE_PROJECT_MISSING`。
- `activity list` 跳过损坏文件并保留诊断，`activity show <id>` 对损坏记录返回明确错误。

## 明确限制

- M2 已完成范围为可选的 `unity-cli` 只读 Editor 能力（状态、层级与 custom probe 快照）；M3 已完成并真实验证单 Prefab 根对象 activeSelf 写入与资产快照；M4 增加项目级 Codex Skill 与安全安装；M5 增加 MCP 映射与配置生成；M6 增加官方 Pipeline Provider，不增加新的 catalog 操作；M7 增加 Formation、真实 Agent/Session 身份回填和 Agent Profile 安装。
- M3 不包含 multi-asset、bulk、场景写入、播放模式或全部 Unity 工具；`project.prefab-set-active` 只接受单个 `assetPath`。
- M4 Skill 安装只支持项目级 `.agents/skills`，不做用户级安装、全局 Skill 替换或自动修改 Codex 配置。
- M5 MCP 只实现 stdio，不提供 HTTP transport、MCP Prompt、资源订阅或低层 Editor 工具；MCP Resource 当前为固定 URI，不提供服务端资源变更通知。
- M6 Official Provider 依赖 Unity 官方 `com.unity.pipeline` 的公开实例描述与 HTTP 命令 API，不修改或再分发官方包。
- M7 不提供跨实例调度、自动递归、Formation UI 或 MCP Formation Tool；应用只生成派发包，实际 Agent/Session 创建仍由 Codex 宿主工具执行。
- Official 写入通过固定的 `script eval` 脚本完成，不宣称 Unity Undo；结果中 `undoGroup` 为空，保存、强制导入、回读与磁盘 SHA 仍为完成证据。
- Akio 专属 `project.probe` 在 Official Provider 上明确返回 `CAPABILITY_UNSUPPORTED`；不跨 Provider fallback。
- 真实 Unity 6 验证使用 `E:\AI\BDTools\M2TestArea`（M2）与 `E:\AI\BDTools\M3TestArea\Unity6Fixture`（M3）fixture，原 TTF2 未修改；验证依赖本机 `unity-cli`/bridge。
- 未安装 `unity-cli` 或 Editor 未就绪时，Editor 能力返回未就绪/不支持，M1 本地操作不受影响。
- 不支持二进制写入、流式传输、跨进程强一致 CAS 或 CLI `--timeout` 覆盖；`workspace.apply` 编码后上限 1 MiB。
- Activity 列表最多返回最近 100 条摘要；不持久化 Started 中间态。
- 详细设计见 [0003](docs/decisions/0003-bdtool-m1-workspace-contract.md)、[0004](docs/decisions/0004-bdtool-m1-runtime.md)、[0005](docs/decisions/0005-bdtool-m2-akio-editor.md)、[0006](docs/decisions/0006-bdtool-m3-unity-core.md)、[0007](docs/decisions/0007-bdtool-m4-skills.md)、[0008](docs/decisions/0008-bdtool-m5-mcp.md)、[0009](docs/decisions/0009-bdtool-m6-official-provider.md)、[0010](docs/decisions/0010-bdtool-m7-1-formation.md) 与 [0011](docs/decisions/0011-bdtool-m7-runtime-formations.md)。
