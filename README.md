# BDTool

BDTool 是面向个人 AI 工作流的本地工具。M0 建立可构建、可测试、依赖方向可验证的 .NET 10 解决方案与公共契约，并实现离线 `system.doctor` 与 Activity 审计；不连接 Unity，不包含 Workspace/Provider/UI/MCP/Formation 的真实实现。

## 当前状态

- 已冻结解决方案、程序集、命名空间和 CLI 可执行名：`BDTool` / `bdtool`。
- `catalog/operations.json` 中 `system.doctor` 已实现并标记为 `available`；`operations.list`、`operations.describe` 仍为 `implemented: false`、`planned`（M1），未注册 Handler 的操作不会被报告为可用。
- M0 已注册离线 `system.doctor` Handler，并完成 Activity 落盘与 `activity list` / `activity show` 查询。
- 本仓库没有创建 Unity 工程，也没有执行任何 Unity 兼容性验证；doctor 固定报告 `unity.connected=false`、`unity.integration=not-implemented`。

## 目录

```text
BDTool.sln
Directory.Build.props
catalog/operations.json
src/BDTool.Contracts        BCL-only 公共 DTO、Port、错误与能力
src/BDTool.Core             不依赖外部系统的领域规则
src/BDTool.Operations       操作目录加载、校验与 Handler 注册表
src/BDTool.Activity         Activity 路径、内容与脱敏策略
src/BDTool.Application      用例运行时与快照
src/BDTool.Composition      唯一组合根
src/BDTool.Cli              bdtool 可执行入口（doctor 与 Activity 查询）
tests/BDTool.Tests          单元、组合与架构测试
docs/decisions              冻结决策记录
```

## 构建与测试

```powershell
dotnet build BDTool.sln -c Release
dotnet test BDTool.sln -c Release
dotnet format BDTool.sln --verify-no-changes --no-restore
```

架构测试会解析实际 `.csproj` 的 `ProjectReference`，不会用硬编码依赖表替代工程事实。

## M0 CLI 行为

```powershell
bdtool doctor --json --project <path>
bdtool activity list --json --project <path>
bdtool activity show <activityId> --json --project <path>
```

- `doctor` 是 CLI 短命令，映射到目录操作 `system.doctor`。它执行离线检查：应用与契约版本、项目路径、`.bdtool` 状态目录可写性、Activity 根、操作目录加载与空 Provider 列表；成功返回 `status = completed` 和 typed `DoctorReport`，退出码 `0`。
- doctor 明确报告 `unity.connected = false`、`unity.integration = not-implemented`，不连接 Unity，也不代表任何 Unity 能力可用。
- 每次调用（成功或失败）都尽力在 `<project>/.bdtool/state/activity/<yyyyMMdd>/<activityId>.json` 写入独立 Activity 记录；`activity list` 返回最近 100 条摘要，`activity show` 读取单条完整记录。
- `--json` 时 stdout 只包含一个 JSON 文档，诊断信息写 stderr；JSON 使用 camelCase 属性名与字符串枚举。
- 退出码：`0` 成功、`2` 参数错误或未知操作（`NOT_FOUND`）、`3` 能力不支持、`4` 环境未就绪、`5` 执行失败、`6` 超时、`7` 取消、`8` 结果不确定。
- `--project` 指向不存在的目录时，doctor 返回 `PROJECT_NOT_FOUND`（退出码 `4`）；Activity 落盘仍按权威路径创建，这是 M0 已记录的“审计优先”行为。
