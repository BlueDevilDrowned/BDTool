# 0009 BDTool M6：Unity 官方 Pipeline Provider

- 状态：实现与真实 Unity 6 端到端验证完成
- 日期：2026-11-26
- 产品版本：`1.1.0`，里程碑：`M6`

## 目标与范围

M6 新增 `BDTool.Provider.Official`，实现既有 `IEditorKernel`。Provider 使用 Unity 官方 `com.unity.pipeline` 的公开实例发现与 HTTP 命令协议，不修改、分叉或再分发官方包。

本轮不增加 catalog operation，不修改 Operations Handler、Skill 或 MCP Tool 集合。Editor Provider 由组合根显式选择：默认保持 `akio`，CLI 使用 `--editor-provider official` 切换。

## 发现与安全

Provider 从以下官方描述文件发现当前项目 Editor：

```text
<project>/Library/Pipeline/.unity-pipeline-port
```

只使用 `127.0.0.1` HTTP 端点，并读取描述文件中的 Bearer Token。Token：

- 只保留在 Provider 内存；
- 不进入 `ProviderDescriptor`、错误 Details、Activity、stdout 或日志；
- 每次请求前重新读取描述，兼容 Editor 重启后的令牌轮换。

描述的项目路径必须与请求项目一致；心跳超过 60 秒视为断连。缺少描述、连接拒绝、认证失败、超时和上游命令错误都转换成稳定 `EditorException`。

## Provider 选择

`BDToolCompositionOptions.EditorProvider` 只接受：

- `akio`：默认值，保持既有 Akio 行为。
- `official`：只装配 `BDTool.Provider.Official`。

未知值在组合阶段明确失败。当前调用一次只装配一个 Editor Provider，因此写操作不会跨 Provider 自动重试或 fallback。

## 操作映射

| BDTool operation | 官方 Pipeline 命令 | 说明 |
| --- | --- | --- |
| `project.inspect` | `scene get_hierarchy` | 将 roots、hierarchyPath、activeSelf、components 转换为既有 typed 层级；应用 rootPath、深度与节点预算 |
| `project.asset-snapshot` | `script eval` | 固定只读脚本读取 Prefab、SHA-256、根名称、子节点数与 Unity 版本 |
| `project.prefab-set-active` | `script eval` | 固定写脚本执行 CAS、Prefab Contents 加载、保存、同步导入、回读和 SHA 核对 |
| `project.probe` | 无 | Akio 专属 custom probe，明确返回 `CAPABILITY_UNSUPPORTED` |

脚本不是由 Agent 或调用方提供。Provider 将 `assetPath` 限制为 `Assets/` 下单个 `.prefab`，拒绝换行、NUL 和 `..`；所有字符串以 JSON 字符串字面量转义后嵌入固定脚本。

## 结果语义

- 只读操作返回现有 Handler 已冻结的 typed JSON 形状。
- Official 写操作不宣称 Unity Undo，`undoGroup` 为空。
- 真实写必须得到 `saved=true`、`verified=true`、目标 active state 回读正确、`newSha256 != previousSha256`；Handler 还会用 Workspace 磁盘指纹复核。
- 写请求提交后连接中断、超时或无法确认结果返回 `EXECUTION_UNCERTAIN`，禁止自动重试。
- 不支持的 `project.probe` 在 HTTP 调用前失败，Details 列出 Official 支持的 operation ID。

## 架构约束

- `BDTool.Provider.Official` 只引用 `BDTool.Contracts`。
- 不引用 Akio、Application、Operations、CLI、MCP 或 Unity 程序集。
- 不修改官方 Pipeline 包、不动态加载 DLL、不使用 shell 拼接。
- `BDTool.Composition` 是唯一注册具体 Provider 的位置。
- 业务 Handler、Skill 和 MCP Tool 目录保持不变。

## 真实 Unity 6 验证

环境：

- Unity `6000.6.0f1`
- `com.unity.pipeline` `0.10.0-exp.1`
- 隔离 fixture `E:\AI\BDTools\M6TestArea\PipelineFixture`
- 原 TTF2 项目未修改

结果：

- `context --editor-provider official`：Editor ready，Provider ID `official`，Unity 版本正确。
- `project.inspect`：成功读取真实场景层级；带 `rootPath` 的过滤也已验证。
- `project.asset-snapshot`：返回真实 Prefab SHA-256、根名称、子节点数和 Unity 版本。
- dry-run：`saved=false`、`verified=false`，文件 SHA 不变。
- 真实写入：activeSelf `false → true`，`saved=true`、`verified=true`，SHA 从 `bc9125e9…` 变为 `231d931b…`。
- stale CAS：返回 `PRECONDITION_FAILED`/退出码 `5`，文件 SHA 不变。
- `project.probe`：Official 返回 `CAPABILITY_UNSUPPORTED`/退出码 `3`，未 fallback 到 Akio。
- Activity：对应操作来源 `cli`、Provider ID `official`，状态与 changedAssets 正确。

## 自动验证

- Provider Contract Test 覆盖 descriptor、状态、层级归一化、快照脚本、Prefab 写脚本、能力差异与写失败 Uncertain。
- CLI 测试覆盖 Official Provider 选择、互斥装配与非法 Provider 参数。
- 架构测试验证 Official 只依赖 Contracts、Composition 为唯一 Provider 注册点，以及业务层和 MCP 不直接依赖 Provider。
- Release 构建成功，0 警告、0 错误；全量 .NET 测试 479/479 通过；`dotnet format --verify-no-changes` 通过。

## 限制

- 不支持官方 Pipeline Runtime Player。
- Official 写路径不提供 Unity Undo；以保存、强制导入、回读和磁盘 SHA 作为完成证据。
- 官方包处于 experimental 版本；实现只依赖已公开的实例描述与命令协议。
