# 0008 BDTool M5：stdio MCP Adapter

- 状态：实现与真实 stdio 端到端验证完成
- 日期：2026-11-26

## 范围

M5 新增 `BDTool.Mcp` / `bdtool-mcp`，用官方 `ModelContextProtocol` .NET SDK 暴露项目级 MCP Adapter。

本里程碑包含：

- 从 catalog 动态生成高层 MCP Tool。
- 两个只读 MCP Resource。
- MCP Tool 调用统一进入 `IApplicationGateway`。
- CLI 与 MCP 共用操作结果 JSON 序列化。
- JSON 与 Codex TOML 配置生成。
- 架构、SDK 协议端到端、Schema 和 Activity 测试。

不包含新的 Unity/catalog 操作、HTTP transport、MCP Prompt、Resource 订阅、Provider 私有工具或跨 Provider fallback。

## 依赖与所有权

`BDTool.Mcp` 的直接项目引用为 `BDTool.Contracts`、`BDTool.Application`、`BDTool.Composition`，包固定为：

- `ModelContextProtocol` `2.4.0`
- `Microsoft.Extensions.Hosting` `10.0.14`

`BDTool.Composition` 只用于进程启动时创建与 CLI 相同的 `BDToolRuntime`。MCP 源码不引用 `BDTool.Provider*`、`System.Diagnostics`、UnityEditor 或 UnityEngine，架构测试会拒绝这些依赖。
MCP 入口不接受 `--unity-cli` / `--unity-editor`，底层 Provider 参数不会暴露给 MCP 客户端。

数据所有权：

- catalog：操作 ID、effect、Schema、能力与 timeout 的唯一来源。
- MCP：Tool/Resource 协议投影、参数对象到 `InputJson` 的转换、stdio 生命周期。
- Application：执行、能力检查、超时、Activity。
- Provider：仍是外部系统的唯一访问边界。

## Tool 契约

MCP 只广告 `implemented=true` 且 `availability=available` 的 catalog 条目。当前 12 个操作全部广告，Tool 名称等于 operation ID。

- `InputSchema` 与 catalog `inputSchemaJson` 语义一致。
- `OutputSchema` 与 catalog `resultSchemaJson` 语义一致。
- `CallToolRequestParams.Arguments` 序列化为操作输入对象。
- 调用创建 `source=mcp`、actor=`ai` 的 `ActivityContext`，不伪造 Agent ID 或 Session ID。
- 成功或业务失败都返回统一结果信封；失败同时设置 `CallToolResult.IsError=true`。
- 不把 Provider 原始错误、上游工具名或底层命令透传成公开 Tool。

## Resource 契约

固定 Resource：

- `bdtool://catalog/operations`：直接读取公共 catalog，不触达 Provider，也不写 Activity。
- `bdtool://project/context`：通过 Application 执行 `project.context`，保留 Activity 与统一结果信封。

当前不支持模板 URI、订阅或变更通知。

## 结果一致性

CLI 原先在 `CliOutput` 中自行序列化。M5 把信封逻辑移动到 `BDTool.Application.Serialization.OperationResultJsonSerializer`，CLI 和 MCP 均调用该实现，统一：

- camelCase 属性；
- 字符串枚举；
- 非泛型失败的空 `data`；
- `changedAssets`、`warnings`、`errors`、`artifacts`、`proof`、`next` 字段形状。

真实协议测试对同一临时文件分别通过 CLI 与 MCP 执行 `workspace.fingerprint`，比较完整 JSON 结构并要求完全一致。

## 配置生成

`bdtool-mcp config --project <path> --format json` 输出通用 `mcpServers.bdtool` JSON。

`bdtool-mcp config --project <path> --format codex` 输出：

```toml
[mcp_servers.bdtool]
command = "dotnet"
args = ["<bdtool-mcp.dll>", "--project", "<project>"]
cwd = "<project>"
startup_timeout_sec = 30
```

配置生成不写文件、不修改用户配置、不包含凭据或模型选择。

## 验证证据

- Release 构建成功，0 警告、0 错误。
- 全量 .NET 测试 465/465 通过。
- `dotnet format --verify-no-changes` 通过。
- SDK 客户端启动真实 `bdtool-mcp` stdio 子进程：
  - 发现 12 个 Tool 和 2 个 Resource；
  - Tool ID 与 catalog 完全一致；
  - 输入/输出 Schema 与 catalog 一致；
  - `project.context` 成功执行并返回 `structuredContent`；
  - Activity `source=mcp`、`operationId=project.context`；
  - CLI 与 MCP 的 `workspace.fingerprint` 结果 JSON 完全一致。
- JSON 与 Codex TOML 配置生成、非法 `--format` 退出码 `2` 均有自动测试。
- 使用生成的 Codex TOML 启动 `codex mcp list --json`，确认 Codex 将 `bdtool` 识别为启用的 stdio Server，并正确读取 `command`、`args`、`cwd` 与 `startup_timeout_sec=30`。

未执行 HTTP MCP、真实 Unity Editor 下的 MCP 写入或 Codex 客户端自动安装配置；这些不属于 M5 最小范围。
