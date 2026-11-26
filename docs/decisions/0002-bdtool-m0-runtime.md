# 0002 BDTool M0 可运行范围：doctor、Activity 审计与 CLI 查询

日期：2026-11-26

## 背景

[0001](./0001-bdtool-m0.md) 记录了工程骨架与公共契约的历史事实：当时 `system.doctor` 没有 Handler，目录中所有条目均为 `implemented: false`，CLI 对 `doctor` 返回 `CAPABILITY_UNSUPPORTED`。本决策不修改 0001 的历史记录，只补充本轮把 M0 补成“可实际验收”的实现边界。

范围依据：`MyUnityAI/DESIGN.md:428-541`（CLI 契约、操作契约与统一结果）、`MyUnityAI/DESIGN.md:659-684`（M0 范围与验收）、`MyUnityAI/EXTENSIBILITY.md:843-879`（Activity 存储与脱敏）。

## M0 可运行范围

本轮完成后，M0 的可用范围是：

- `bdtool doctor --json` 成功返回 `status=completed` 与 typed `DoctorReport`。
- 每次 Application 调用（成功、未知、未支持、失败、取消）都尽力写入一条独立 Activity。
- `bdtool activity list` / `bdtool activity show <activityId>` 可查询本地 Activity 文件。
- 组合根显式装配 `FileActivityStore`、`FileArtifactStore` 与 `system.doctor` Handler。
- 仍然不连接 Unity、不安装 Editor Provider、不实现 Workspace Kernel。

不在本轮范围：`operations.list` / `operations.describe` 的 Handler、真实 Workspace 读写、Provider 探测、MCP、WPF UI、Formation。

## doctor 检查项

`system.doctor` 由 `BDTool.Operations.Diagnostics.DoctorHandler` 实现，环境探测由 `BDTool.Application.Diagnostics.FileSystemDoctorEnvironmentProbe` 完成；Handler 只消费探测快照，不直接调用文件系统 API。

| checkId | 含义 | 失败后果 |
| --- | --- | --- |
| `app.version` | 注入的应用版本非空 | `ENVIRONMENT_UNSUPPORTED` |
| `contract.version` | `BDToolContract.Version` 非空 | `ENVIRONMENT_UNSUPPORTED` |
| `project.path` | 目标项目目录存在 | `PROJECT_NOT_FOUND` |
| `state.root` | `<project>/.bdtool` 可创建且可写 | `ENVIRONMENT_UNSUPPORTED` |
| `activity.root` | `<project>/.bdtool/state/activity` 可写 | `ENVIRONMENT_UNSUPPORTED` |
| `catalog.loaded` | 操作目录已加载且包含 `system.doctor` | `ENVIRONMENT_UNSUPPORTED` |
| `providers.empty` | Provider 列表为空（非空降级为 `warn`，不失败） | — |
| `unity.connection` | 固定 `skipped` | — |

状态汇总规则：存在 `fail` 为 `not-ready`；只有 `warn` 为 `degraded`；否则 `ready`。

探测只创建 `.bdtool` 状态目录并写入一个短生命周期探针文件，**不替用户创建项目目录**。`DoctorReport.Unity` 固定为 `connected=false`、`integration=not-implemented`，任何输出都不得据此推断 Unity 可用。

## Activity 集成

- 权威路径与 0001 一致：`<project>/.bdtool/state/activity/<yyyyMMdd>/<activityId>.json`；写入、查询、按 ID 读取都经由 `BDTool.Activity.FileActivityStore`。
- `ApplicationGateway.ExecuteAsync` 在分发之前用 `ActivityRecordFactory.CreateStarted` 建立记录，因此目录中不存在的操作（`NOT_FOUND`）与未注册 Handler 的操作（`CAPABILITY_UNSUPPORTED`）同样留下审计轨迹。
- 记录完成后用 `ActivityRecordFactory.Complete` 写入 `status`、`exitCode`、`error`、`output` 与 `artifacts`；输入/输出摘要统一走 `ActivityContentPolicy`（敏感字段只保留存在性与长度，超过 64 KiB 只保留哈希与大小）。
- `ActivityId` 与 `TraceId` 来自同一个 `ActivityContext`，CLI 中用同一个 GUID 生成，因此同一次调用的两个字段相等。
- Activity 写入失败只追加 `ACTIVITY_WRITE_FAILED` 告警，不把成功的业务结果改成失败；写入使用独立 `CancellationToken.None`，避免调用方取消导致记录停留在 `running`。
- `IActivitySink` 是 `ApplicationGateway` 的构造必需依赖；组合根必须显式提供 `FileActivityStore`，缺少审计端口时在构造阶段失败，不允许出现“无记录的成功调用”。

## CLI 命令

| 命令 | 说明 | 退出码 |
| --- | --- | --- |
| `bdtool doctor [--json] [--project <path>]` | 映射到冻结操作 `system.doctor` | `0` 成功；检查失败按错误码映射（`4`/`5`） |
| `bdtool activity list [--json] [--project <path>]` | 通过 `IActivityQuery` 列出最近 100 条活动摘要 | `0`；参数错误 `2` |
| `bdtool activity show <activityId> [--json] [--project <path>]` | 读取单条完整记录 | `0`；未知 ID `2` |

- `activity` 是 CLI 专用查询路由，不新增、也不改写 `catalog/operations.json` 中已冻结的操作 ID。
- `--json` 时 stdout 只包含一个 JSON 文档，正常日志不写 stderr；人类可读诊断只写 stderr。
- JSON 统一 camelCase 属性名与 camelCase 字符串枚举（如 `completed`、`unsupported`）；错误对象包含 `code`、`message`、`retryable`、`details`。
- 退出码保持 `0/2/3/4/5/6/7/8`，由 `ExitCodePolicy` 单点映射。
- 未指定 `--project` 时使用当前工作目录；指定时所有状态都落在该项目的 `.bdtool` 下。

## 组合根装配

`BDToolComposition.Create` 显式构造依赖，不使用 DI 容器：

```csharp
var catalog = OperationCatalogLoader.Load(catalogPath);
var activityStore = new FileActivityStore(projectPath);
var artifactStore = new FileArtifactStore(projectPath);
var handlers = new OperationHandlerRegistry()
    .Register(new DoctorHandler(probe, catalog, options.AppVersion, projectPath));
handlers.EnsureConsistent(catalog);
```

- `BDToolRuntime.ActivityQuery` 暴露 `IActivityQuery` 供 CLI 查询；CLI 不直接构造文件存储，也不感知 Provider 品牌。
- `EnsureConsistent` 强制 `implemented`/`availability` 与 Handler 注册状态一致，防止目录谎报可用。
- `OfflineOperationContextFactory` 注入真实的 `IArtifactStore` 与 `ICapabilityInspector`；`IWorkspaceKernel`/`IEditorKernel` 使用访问即抛错的占位实现，越界访问会在网关处变成明确的执行失败。

## 操作目录

`catalog/operations.json` 现在只有 `system.doctor` 为 `implemented: true`、`availability: available`。`operations.list` 与 `operations.describe` 保持 `implemented: false`，并仍为 `planned`/M1。

## 明确未验证项

- 未打开、未连接、未编译任何 Unity 项目；未验证 Editor 版本兼容性、PlayMode 或资源操作。
- 未装配任何 Provider，`Provider` 列表恒为空；未验证 `providerId=akio` 等后续 Provider 协议。
- 实现轮未运行整个解决方案的 `dotnet build` / `dotnet test`（与并行负责人共享 `obj` 目录，避免写冲突）。该缺口已由下方“集成验证记录（2026-11-26）”补齐。

## 已知限制与风险

- 若 `--project` 指向不存在的目录，doctor 如实返回 `PROJECT_NOT_FOUND`；但 Activity 落盘仍会按权威布局创建 `<project>/.bdtool/state/activity`，因此该目录会随后出现。M0 选择“审计优先”，M1 需要决定缺失项目调用的记录位置（例如用户级状态目录）。
- `activity list` 只返回最近 100 条摘要，`count` 表示本次返回数量，不代表项目内 Activity 总数。
- Activity 文件的查询实现属于并行负责人范围，本决策只描述集成契约与装配方式。

## 集成验证记录（2026-11-26）

由唯一写入负责人使用同一份最终源码执行：

| 命令 | 退出码 | 结果 |
| --- | ---: | --- |
| `dotnet build BDTool.sln -c Release` | 0 | 0 警告、0 错误 |
| `dotnet test BDTool.sln -c Release` | 0 | 109/109 通过 |
| `dotnet format BDTool.sln --verify-no-changes --no-restore` | 0 | 无格式差异 |

真实 CLI 在随机临时项目中验证：`doctor` 返回 `completed`/`ready`，stdout 为单个合法 JSON 且 stderr 为空；Activity 记录满足 `activityId=traceId`、`operationId=system.doctor`、`status=completed`、`exitCode=0`；`activity list` 与 `activity show` 可读回同一条记录；`nope.nope` 返回 `NOT_FOUND`、退出码 `2`，并写入第二条 Activity；`--project` 指向不存在目录时 doctor 返回 `PROJECT_NOT_FOUND`、退出码 `4`，但仍写入审计记录。

本机验证证据目录（临时目录，不属于仓库）：`C:\Users\BlueDevil\AppData\Local\Temp\bdtool-m0-verify-ea4edbff`。全部验证均未连接 Unity，也未验证任何 Unity 兼容性。
