# BDTool

BDTool 是面向个人 AI 工作流的本地工具。M1 已把本地 Workspace Provider 接入真实 Application：可查询操作目录与项目上下文，并在项目内执行文本读写、搜索、指纹与带 CAS 的原子写入。M1 **不连接 Unity**，Editor、场景与资源操作属于后续里程碑。

## 当前状态

- 已冻结解决方案、程序集、命名空间与 CLI 可执行名：`BDTool` / `bdtool`。
- 八个操作均已注册 Handler 并在目录中标记为 `available`：`system.doctor`、`operations.list`、`operations.describe`、`project.context`、`workspace.read`、`workspace.search`、`workspace.fingerprint`、`workspace.apply`。
- `BDTool.Provider.Local` 上报 `workspace.read/search/fingerprint/write` 四个能力；Composition 是应用侧唯一引用该 Provider 的程序集。
- 每次有效调用都会尽力写入 Activity；`activity list` / `activity show` 可查询。
- 未创建 Unity 工程，也未执行任何 Unity 兼容性验证；`doctor` 与 `context` 固定报告 Editor 未连接。

## 目录

```text
BDTool.sln
Directory.Build.props
catalog/operations.json
src/BDTool.Contracts        BCL-only 公共 DTO、Port、错误与能力
src/BDTool.Core             不依赖外部系统的领域规则（路径策略、原子写）
src/BDTool.Operations       目录加载/校验、输入解析与八个 Handler
src/BDTool.Activity         Activity 存储与脱敏策略
src/BDTool.Application      用例运行时、能力检查、超时与退出码映射
src/BDTool.Provider.Local   本地 Workspace Provider（仅依赖 Contracts/Core）
src/BDTool.Composition      唯一组合根
src/BDTool.Cli              bdtool 可执行入口
tests/BDTool.Tests          单元、组合、CLI 与架构测试
docs/decisions              决策记录（0001-0004）
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

```powershell
bdtool doctor [--project <path>]
bdtool context [--project <path>]
bdtool operations list [--project <path>]
bdtool operations describe workspace.read [--project <path>]
bdtool run workspace.read --input input.json [--project <path>]
bdtool run workspace.apply --input-json '{"relativePath":"Assets/a.txt","content":"hi","createIfMissing":true}' [--dry-run]
bdtool workspace.fingerprint --input-json '{"relativePaths":["Assets/a.txt"]}'
bdtool activity list [--project <path>]
bdtool activity show <activityId> [--project <path>]
bdtool --version | --help
```

- `--input` 必须是 UTF-8 JSON 文件路径；`--input-json` 为内联扩展。二者互斥，缺文件或非法 JSON 返回 `INVALID_ARGUMENT`/退出码 `2`。
- `operations list/describe` 进入 Gateway 并写入 Activity，不在解析阶段直接读目录。
- 参数/组合错误在默认 JSON 模式下仍向 stdout 输出合法统一错误信封；stderr 只写诊断。
- Ctrl+C 会取消当前调用并返回 `CANCELLED`/退出码 `7`。
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

## Workspace 语义

- 读取与搜索只处理 UTF-8 文本；开头 BOM 被剥离，`Sha256` 始终对磁盘原始字节计算（包含 BOM）。
- 读取恰好 1 MiB 允许，超过返回 `INVALID_ARGUMENT`。写入按 UTF-8 编码后字节数计，`maxApplyBytes` 默认 1 MiB，`dryRun` 同样受限。
- 搜索排除 `.git/.bdtool/bin/obj/Library/Temp/Logs/UserSettings/node_modules/.vs`；空或全空白 `query` 返回 `INVALID_ARGUMENT`，超过 `maxResults` 时 `truncated=true`，打开候选前复检 reparse。
- `workspace.apply` 写 UTF-8 无 BOM，使用同目录临时文件 + 原子 Move；覆盖已有文件必须提供 `expectedSha256`，陈旧 CAS 返回 `PRECONDITION_FAILED` 且无副作用。
- 创建使用非覆盖提交，二次预检后目标被创建返回 `PRECONDITION_FAILED`；父链某段是普通文件返回 `INVALID_ARGUMENT`/`parent-not-directory`；更新仍保留已承认的 CAS→Move 窗口。
- `dryRun=true` 只做路径、大小、CAS 与父链结构预检，不创建目录、临时文件或目标。
- 有效超时为 `min(请求值, 目录 timeoutSeconds)`；超时 `TIMEOUT`/`6`，调用方取消 `CANCELLED`/`7`，两者都写 Activity 终态。

## Activity

- 每次有效调用都会尽力写 Activity 终态；原始序列化输入/输出超过 64 KiB（UTF-8 字节）时，将脱敏后的内容写入项目 Artifact，摘要记录落盘内容的实际字节数与 SHA-256，并在落盘后复验。
- Started 仅存在于内存，M1 只在终态持久化一次；进程崩溃可能丢失进行中的记录。
- 项目根缺失时不创建目录，跳过审计并告警 `AUDIT_UNAVAILABLE_PROJECT_MISSING`。
- `activity list` 跳过损坏文件并保留诊断，`activity show <id>` 对损坏记录返回明确错误。

## 明确限制

- M1 不连接 Unity，不实现 Editor Provider；`unityVersion` 只从 `ProjectSettings/ProjectVersion.txt` 读取。
- 不支持二进制写入、流式传输、跨进程强一致 CAS 或 CLI `--timeout` 覆盖；`workspace.apply` 编码后上限 1 MiB。
- Activity 列表最多返回最近 100 条摘要；不持久化 Started 中间态。
- 详细设计见 [0003](docs/decisions/0003-bdtool-m1-workspace-contract.md) 与 [0004](docs/decisions/0004-bdtool-m1-runtime.md)。
