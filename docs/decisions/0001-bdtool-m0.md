# 0001 BDTool M0 工程骨架与公共契约

日期：2026-11-26

## 范围

本轮严格实现设计文档默认的 M0：解决方案、项目骨架、Contracts、操作目录、最小组合根、Activity 契约与架构测试。不连接 Unity，不创建 `UnityHost`，不实现 M1+ 的真实 Workspace、Provider、WPF UI、MCP 或 Formation。

`system.doctor` 的 Handler、CLI 成功输出和 Activity 文件写入不在本轮完成。因此当前 CLI 对 `doctor` 返回明确的 `CAPABILITY_UNSUPPORTED`，不会伪造成功；`catalog/operations.json` 中所有条目均为 `implemented: false`，任何未注册 Handler 的操作都不能被报告为可用。

## 实现根与偏离

设计文档原定实现根为 `E:\AI\BDTools\MyUnityAI`。本轮按用户指令新建 `E:\AI\BDTools\BDTool`，现有四份设计文档与参考仓库保持只读。该偏离只影响实现位置，不改变契约含义。

## 品牌映射

| 旧标识 | 冻结值 |
| --- | --- |
| `MyUnityAI` | `BDTool` |
| `MyUnityAI.sln` | `BDTool.sln` |
| `myunity` | `bdtool` |
| `.myunity/state/` | `.bdtool/state/` |
| `.myunity/artifacts/` | `.bdtool/artifacts/` |
| `%LOCALAPPDATA%\MyUnityAI\` | `%LOCALAPPDATA%\BDTool\` |
| `IMyUnityApplication` | `IApplicationGateway` |

`IApplicationGateway` 是冻结命名，不再新增 `IMyUnityApplication`。业务协议值 `project.*`、`providerId=akio`、`source=cli|mcp|desktop` 不随品牌改名。

## Activity 冻结规则

- 唯一权威路径：`<project>/.bdtool/state/activity/<yyyyMMdd>/<activityId>.json`。
- 每次 Application 调用写入一个独立文件，使用原子替换，不允许多进程追加同一文件。
- 输入或输出超过 64 KiB 时写入 artifact，Activity 只保留摘要和引用。
- 默认保留 30 天。
- 以下字段只保存存在性和长度，不保存原值：`token`、`password`、`secret`、`apiKey`、`authorization`、`cookie`、`sessionCredential`、`memberToken`。

## 结果类型

公共执行入口返回 `OperationResult`。每个操作必须使用 `OperationResult<TData>` 携带冻结的 typed DTO；禁止用万能 `object` 或 `Dictionary<string, object?>` 作为最终业务结果。Kernel 传输信封可以保留动态数据，但不得越过 Handler 边界成为 CLI/UI 的公共结果。

## 操作目录语义

当前目录只登记 `system.doctor`（M0 契约）、`operations.list` 和 `operations.describe`（M1 计划）。`implemented` 表示当前构建是否注册了可执行 Handler；M0 骨架没有注册任何 Handler，所以全部为 `false`。`availability` 区分 `contract-only` 与 `planned`，避免把计划项误报为可用。

## 测试框架

选择 xUnit 2.9.3（模板生成），使用 `Microsoft.NET.Test.Sdk` 17.14.1 与 `xunit.runner.visualstudio` 3.1.4。架构测试解析实际 `.csproj` 的 `ProjectReference`，并检查 Contracts 的项目/包引用及源码禁用命名空间。

## 与设计证据的显式偏离

以下偏离均为有意决策，不是遗漏：

| 设计证据 | 本实现 | 理由 |
| --- | --- | --- |
| `MyUnityAI` / `MyUnityAI.Contracts`（DESIGN.md:136、387） | `BDTool` / `BDTool.Contracts` | 用户冻结品牌；产品、解决方案、程序集、命名空间统一 |
| `<project>/.myunity/state/activity/`（EXTENSIBILITY.md:848） | `<project>/.bdtool/state/activity/` | 品牌映射；唯一权威路径已冻结 |
| `KernelError.Details` 为 `Dictionary<string, object?>`（EXTENSIBILITY.md:151） | `Dictionary<string, string>` | 错误详情在进入协议边界前统一转为字符串，禁止任意对象泄漏；仍满足 code/message/retryable/details |
| Activity `preview` 为对象（EXTENSIBILITY.md:824-833） | `ActivityPayload.PreviewJson` 为 JSON 文本 | Contracts 禁止依赖 JSON 库；文本由 Adapter 原样写入或解析 |
| `KernelResult`（EXTENSIBILITY.md:126） | `OperationResult` / `OperationResult<TData>` | 与操作目录、Handler 命名统一；业务数据必须经泛型携带 |
| `inputSchema` / `resultSchema` 为对象（DESIGN.md:507-508） | `InputSchemaJson` / `ResultSchemaJson` 文本 | 同上，Contracts 不解析 JSON；目录加载器校验其为 JSON 对象 |

`OperationDescriptor` 额外携带 `Implemented`、`Milestone`、`Availability`、`Notes`，用于表达“契约已登记但 Handler 未注册”的状态；这些字段不改变设计中的操作 ID、效果、环境与超时语义。

## 目录加载与 Handler 一致性

- `OperationCatalogLoader` 读取 `catalog/operations.json`，校验版本、必需字段、点分小写 ID、effect/environment/availability 枚举值、Schema 为 JSON 对象、超时范围与 ID 唯一性。
- `OperationHandlerRegistry` 在 M0 保持为空。`EnsureConsistent` 要求 `implemented` 与 Handler 注册状态一致，并要求只有已注册 Handler 的操作才能标记 `availability=available`。
- `CatalogCapabilityInspector` 只按目录与注册表报告能力，不探测 Provider 或 Unity；未注册操作一律为 false。

## CLI 契约

- 可执行名 `bdtool`；`doctor` 映射到目录操作 `system.doctor`，其余参数按点分操作 ID 直接查找。
- `--json` 时 stdout 只输出单个 JSON 文档，人类可读错误写 stderr。
- 未注册操作返回 `Unsupported` + `CAPABILITY_UNSUPPORTED`，退出码 `3`；未知操作返回 `NOT_FOUND`，退出码 `2`。
- `--version`、`--help` 成功返回 `0`。

## 验证边界

本轮只验证 .NET 编译与测试。未执行 Unity 打开、编译、编辑模式测试、PlayMode、构建或任何版本兼容性验证；不得据此声称 Unity 集成可用。
