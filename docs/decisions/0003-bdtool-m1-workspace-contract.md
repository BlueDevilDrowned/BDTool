# 0003 BDTool M1 Workspace 契约与共享内核

日期：2026-11-26

状态：M1 冻结

## 范围

本决策冻结 BDTool M1 的 Workspace 公共契约、路径策略、原子写入和 CLI 边界。M1 只实现本地文本 Workspace、离线项目上下文、操作目录查询与稳定 JSON 信封，不建立 Editor 连接，不传输二进制大文件，不新增 Job 或 MCP 能力。

本决策优先于旧设计文档中与 M1 冲突的示例；旧文档未涉及的部分继续有效。

## 1. 编码与文本语义

- 文本文件严格按 UTF-8 解码。
- 开头的 UTF-8 BOM 接受并剥离；剥离后的文本进入 `Content`，`Length` 表示内容的 UTF-8 字节数，不包含 BOM。
- 非法 UTF-8 或包含 NUL 的文件不按文本读取；读取返回 `INVALID_ARGUMENT`，搜索计入 `SkippedBinary`。
- 写入使用 UTF-8 无 BOM。写入内容含 NUL、孤立代理项或非法 UTF-16 时在提交前拒绝，不写临时结果。
- CRLF、LF 和混合换行原样保留，不做换行规范化。
- `Sha256` 和所有指纹始终对磁盘原始字节计算，因此带 BOM 的文件其指纹覆盖 BOM。写回无 BOM 文本后指纹自然会变化。
- ReadResult 的 `Content` 是解码文本，不允许用它传输二进制文件。`Length` 为 `long`，不替代指纹。

## 2. 文件上限、搜索与排除

- `maxReadBytes = 1 MiB`。恰好等于上限允许读取；超过上限返回 `INVALID_ARGUMENT`，不截断文本。
- `maxSearchFileBytes = 1 MiB`。超过上限的文件跳过并累加 `SkippedTooLarge`。
- `maxApplyBytes = 1 MiB`。`workspace.apply` 在 UTF-8 编码后按字节数判定，超限返回 `INVALID_ARGUMENT`（`reason=content-too-large`）；`dryRun=true` 使用同一上限。
- 搜索串是字面量，不是正则表达式。M1 默认且固定大小写敏感，不提供隐藏的大小写开关。
- 空或全空白 `query` 返回 `INVALID_ARGUMENT`，不会把空串当作匹配所有行。
- 搜索结果按稳定顺序返回：先按路径排序（忽略大小写），再按 `Ordinal` 路径、行号和行文本兜底；请求的 `maxResults` 先收敛到 `min(请求值, Provider.maxSearchResults)`（默认 200），匹配最多收集有效 `maxResults+1` 条用于判定，超过有效上限时令 `Truncated=true`，其余文件仍继续扫描以保持跳过计数完整。
- 搜索跳过 reparse point，累加 `SkippedSymlink`；候选文件在打开前按 PathPolicy 对整条父链复检，枚举后被替换为 reparse point 的文件计入跳过而不读取项目外内容。跳过疑似二进制文件，累加 `SkippedBinary`。
- 搜索和文件枚举排除以下目录名（Windows 下不区分大小写）：`.git`、`.bdtool`、`bin`、`obj`、`Library`、`Temp`、`Logs`、`UserSettings`、`node_modules`、`.vs`。
- 排除规则只作用于目录遍历，不改变显式 `workspace.read` 对单个文件的路径校验语义；读显式禁止目录仍返回 `INVALID_ARGUMENT`。

## 3. Glob 语义

- Glob 统一使用 `/` 分隔，不使用反斜杠作为模式分隔符。
- `**` 匹配零个或多个目录段；`*` 和 `?` 只匹配单个路径段内的字符，不跨 `/`。
- Windows 上 glob 匹配不区分大小写，并与路径大小写规则一致。
- 拒绝绝对 glob、盘符 glob、UNC glob，以及任何包含 `..` 段的 glob。非法 glob 返回 `INVALID_ARGUMENT`。
- 文件系统枚举仍执行第 2 节的目录排除与 reparse point 拒绝。

## 4. 路径策略

`BDTool.Core.Workspace.PathPolicy` 是 Local Workspace 的共享路径入口。Contracts 只描述请求/结果形状，不包含文件系统实现。

- 项目根先通过 `Path.GetFullPath` 规范化为绝对路径，并去掉非根目录末尾分隔符。
- 项目内相对路径拒绝：空值、根路径、盘符路径、UNC 路径、`.`/`..` 段、空段、冒号或 ADS、以点或空格结尾的段、Windows 非法文件名字符、Windows 保留设备名（含 `CON.txt` 形式）。
- 相对路径规范化为 `/` 分隔；解析后再次检查绝对路径必须以 `projectRoot + DirectorySeparatorChar` 开头，Windows 使用 `OrdinalIgnoreCase`。
- 项目根目录自身不是合法文件目标；路径前缀碰撞（例如 `C:\Project2` 对 `C:\Project`）必须拒绝。
- 已存在的路径段逐级拒绝 reparse point。缺失的尾部段允许用于后续创建，但检查与打开之间存在无法完全消除的修复窗口；Provider 必须在实际打开前再次做不跟随链接的处理。
- 操作输入不包含 `projectPath`；项目边界只由 CLI `--project` 或组合根绑定的项目根决定，JSON 中的未知字段按 `additionalProperties: false` 拒绝。
- 文件不存在返回 `NOT_FOUND`；目标是目录返回 `INVALID_ARGUMENT`。路径校验失败必须在任何写入副作用之前发生。

## 5. Apply 原子写与 CAS

- Apply 只接受 UTF-8 文本，写 UTF-8 无 BOM；不提供二进制写入。
- 编码后内容上限为 `maxApplyBytes = 1 MiB`；超限在路径解析和任何写入之前返回 `INVALID_ARGUMENT`（`reason=content-too-large`），`dryRun=true` 同样拒绝。
- 所有写入通过 Core 的共享 `AtomicFile`：同目录唯一临时文件、异步写入、`Flush(true)`、原子 Move 与 `finally` 清理。Activity、Artifact 和 Workspace 不得保留第二份实现。
- 目标已存在时必须提供 `ExpectedSha256`；未提供返回 `INVALID_ARGUMENT`，不触碰目标。
- `ExpectedSha256` 必须等于当前原始字节 SHA-256；不一致返回 `PRECONDITION_FAILED`，且无任何文件副作用。
- 目标缺失时只有 `CreateIfMissing=true` 才可创建；否则返回 `NOT_FOUND`。创建时 `PreviousSha256=null`、`Created=true`，提交使用非覆盖 Move；二次预检后目标被其他进程创建时返回 `PRECONDITION_FAILED`，不覆盖对方内容。
- 创建预检同时要求父链中已存在的每一段都是目录；某段是普通文件时返回 `INVALID_ARGUMENT`（`reason=parent-not-directory`），dry-run 与实写一致，均在任何写入之前失败。
- 内容与现有字节完全相同时不重写，返回 `Changed=false`、`Created=false`，保留原文件时间戳与指纹。
- `DryRun=true` 只做路径、内容上限、CAS、父链结构与创建权限预检，返回计划结果并设置 `DryRun=true`，不创建目录、临时文件或目标。
- 正常写入先写并落盘临时文件，再在 Move 前重新执行一次 CAS 检查，然后原子替换目标。两次检查缩小但不能消除跨进程竞争窗口；操作系统没有基于内容比较的原子替换原语，因此不同进程并发写同一文件时，最终仍以提交时的检查结果为准。
- 提交点定义为原子 Move 成功。提交前取消或超时抛出 `CANCELLED`/`TIMEOUT` 并清理临时文件；提交后即使调用方取消，也按已完成返回，调用方可通过指纹确认结果。

## 6. 超时、取消与审计

- 操作有效超时为 `min(请求值, catalog.timeoutSeconds)`；请求未提供时使用 catalog 值。CLI M1 不新增 `--timeout` 覆盖。
- 取消优先于超时；已提交的写操作不得因随后取消报告为失败或不确定。
- 调用边界先建立内存中的 Activity 记录，再执行项目存在性检查。项目目录缺失时令 `projectMissing=true`，但不得为了写审计而自动创建项目目录。
- Activity 的权威路径保持 `<project>/.bdtool/state/activity`。项目根缺失时跳过 Activity 写入并追加 `AUDIT_UNAVAILABLE_PROJECT_MISSING` 告警，项目缺失事实仍保留在结果中。
- 原始序列化输入/输出超过 64 KiB（UTF-8 字节）时，将脱敏后的内容写入项目 Artifact；Activity 摘要记录实际落盘字节数与 SHA-256。

## 7. M1 操作与 CLI

M1 冻结的操作：

```text
system.doctor
operations.list
operations.describe
project.context
workspace.read
workspace.search
workspace.fingerprint
workspace.apply
```

- `system.doctor` 是 M1 内可用的诊断操作：检查应用自身、项目路径、Provider 快照与 Activity 存储，不要求 Workspace 能力，并固定不连接 Unity。
- `project.context` 返回离线 typed 上下文：项目绝对路径、是否存在、可读取的 `unityVersion`、Editor 固定 `connected=false/status=not-connected`、Providers、Capabilities 和 Activity 限制。不得伪造场景、选中对象或 Editor 进程。
- `operations.list` 使用 `OperationsListData`；`operations.describe` 使用 `OperationsDescribeData`。业务 Data 不得使用 `object` 或 `Dictionary<string, object>`。
- `OperationSummary` 镜像目录的 ID、描述、效果、环境、所需能力、超时、产物、实现状态、里程碑、可用性和备注；`OperationDetail` 额外携带 `InputSchemaJson`、`ResultSchemaJson` 原文。
- CLI 默认输出 JSON；`--human` 只改变展示，不改变协议字段。`--input` 表示输入 JSON 文件路径，`--input-json` 表示内联 JSON；两者互斥。
- Result Schema 描述完整结果信封（`status`、`data`、`changedAssets`、`warnings`、`errors`、`artifacts`、`proof`、`next`），不是只描述 `data`。

## 8. 依赖与类型边界

- Contracts 保持 `netstandard2.1;net10.0`、C# 9、无项目/包引用；禁止 `System.Text.Json`、`System.IO`、HTTP 和 Unity 类型。
- Core 只依赖 Contracts；Operations、Activity 依赖 Contracts 与 Core。
- Application 依赖 Contracts、Core、Operations、Activity。
- Provider.Local 依赖 Contracts 与 Core，是 Provider 访问共享路径/IO 规则的显式例外；其他 Provider 只依赖 Contracts。
- CLI 依赖 Application、Contracts、Composition；Composition 是唯一可引用具体 Provider 的应用侧入口。
- Provider 不得反向依赖 Core 的 Application/CLI，也不得引用其他 Provider。

## 9. 验证基线

M1 契约阶段必须至少通过：Release 构建、全套测试、格式校验，外加 Core 原子写与路径策略的真实文件系统测试。Provider.Local 已接入并通过针对 Workspace 边界的真实文件系统测试（读取与写入上限、搜索截断、reparse 跳过、CAS 与父链结构等）；仍不宣称 Unity 或 Editor 能力可用。

## 10. 遗留风险

- 跨进程 CAS 仍是“检查后替换”，无法抵御检查与 Move 之间的恶意或并发写入。
- 路径 reparse point 检查和后续打开之间存在 TOCTOU 窗口，需要 Local Provider 在打开时避免跟随链接，并在支持的平台上复检句柄目标。
- 1 MiB 的读取与写入上限会拒绝较大的合法源码或生成文件；M1 明确不做流式/分块文本传输。
- 项目缺失时的审计落点依赖 Composition 策略，可能出现“审计不可用但项目缺失事实已记录”的状态。
