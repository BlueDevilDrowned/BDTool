# 0005 BDTool M2：可选 Akio Editor Provider（已完成）

- 状态：已完成（M2）
- 日期：2026-11-26

## 背景与范围

M1 只接入本地 Workspace Provider，不连接 Unity。M2 子任务 C 的收口范围是在不改变 M1 行为的前提下，增加一个可选的 Editor 读取通道：

- 新增 `BDTool.Provider.Akio`，通过外部 `unity-cli` 获取 Editor 状态与项目层级。
- Composition 组合 Local Provider 与可选 Akio Provider；找不到 `unity-cli` 时应用仍按 M1 能力运行。
- 目录新增第 9 个可用操作 `project.inspect` 与第 10 个可用操作 `project.probe`；保留 M1 的 8 个操作。
- 不包含场景/资源写入、播放模式控制、Editor 状态修改；真实 Unity 6 已完成 `system ping`、`project.inspect` 与 `project.probe` 产品链路验证。
- `BDToolIdentity.Version` 为 `0.2.0`、`Milestone` 为 `M2`；`Directory.Build.props` 的 `Version` 同步为 `0.2.0`。

## 安装与发现

CLI 可能从仓库根（例如 `E:\AI\BDTools\BDTool`）启动，因此默认发现必须覆盖仓库内安装位置。`unity-cli` 的发现顺序为：

1. 显式 `--unity-cli <exe>`；`--unity-editor <exe>` 单独指定 Unity Editor。
2. 环境变量 `BDTOOL_UNITY_CLI_PATH`。
3. 当前目录及其逐级祖先的 `.tools/unity-cli/win-x64/unity-cli.exe`，并兼容平面 `.tools/unity-cli/unity-cli.exe`。
4. 只读查询系统 `PATH`。

显式路径或环境变量指向无效文件时直接报错，不静默回退到其他候选。BDTool 只读取 `PATH`，不写入、追加或修改系统 `PATH`。CLI 参数会传播到 `BDToolCompositionOptions`；`--unity-cli`/`--unity-editor` 缺少值或重复出现返回 `INVALID_ARGUMENT` 与退出码 `2`。

## 目录契约

`catalog/operations.json` 共 10 个操作：M1 的 8 个、`project.inspect` 与 `project.probe` 全部为 `available`：

- `system.doctor` 与 `project.context` 的环境为 `["workspace","editor"]`。
- `project.inspect` 的环境为 `["workspace","editor"]`，要求 `editor.status` 与 `editor.tool.read`。
- `maxDepth` 范围 `0..32`，`maxObjects` 范围 `1..10000`。
- 结果 schema 包含 `unityVersion` 字符串、typed 字段与递归 `children`，字段与 `ProjectInspectData` DTO 一致；上游缺失版本时使用空字符串。
- `project.probe` 的环境仅为 `["editor"]`，要求 `editor.status` 与 `editor.tool.read`；`inputSchema` 为严格空对象（`additionalProperties=false`、无属性）。
- `project.probe` 的 result schema 对应 typed `Product`、`UnityVersion`、`Platform`、`SceneName`、`ScenePath`、`SceneIsDirty`、`RootObjectCount`、`ObservedAtUtc`（JSON 使用 camelCase）。

## 真实 Unity 6 端到端验证

- 验证环境为 Unity `6000.6.0f1`；测试项目位于 `E:\AI\BDTools\M2TestArea`，原 TTF2 未修改。
- `unity-cli`/bridge 版本为 `0.19.1`，`versionCheck` 匹配通过；`system ping` 成功。
- `project.inspect` 通过 `get_hierarchy` 读取真实层级并返回 typed DTO。
- `project.probe` custom tool 返回 `Product`、`UnityVersion`、`Platform`、`SceneName`、`ScenePath`、`SceneIsDirty`、`RootObjectCount`、`ObservedAtUtc` 全部 8 个 typed 字段。
- 产品链路调用写出 Activity，记录 `providerId=akio`、状态 `completed`。
- `refresh_assets` 之后实际观察到 1 次 ping 失败，随后恢复；该瞬态失败未产生错误的成功结论，也未突破只读边界。
- 以上只证明 M2 的只读 Editor 状态、层级与 custom probe 快照路径；不代表 M3、写操作、播放模式控制或全部 Unity 工具。

## 状态与错误语义

- `doctor` 与 `context` 只有 Editor 状态为 `ready` 且其挂载项目与请求项目匹配时才报告 Connected/Pass。空项目路径视为不匹配；Windows 路径比较大小写不敏感。
- `TimeoutException` 稳定映射为 `integration=timeout` 与失败检查；`EditorException` 映射为 `integration=error` 与失败检查。`context` 的外层操作结果仍可 Completed，Editor 状态在结果中显式表达。
- `ApplicationGateway` 捕获 `EditorException` 时保留 `Code`、`Details` 并标记 `stage=editor`，不再折叠为 `EXECUTION_FAILED`；`CANCELLED` 映射为 `OperationStatus.Cancelled` 与退出码 `7`。
- `CatalogCapabilityInspector` 仅在恰好一个 Provider 时报告其 `ProviderId`；无 Provider 或多个 Provider 时返回空字符串，避免谎报单一来源。单 Provider 的 M1 行为保持不变。

## 只读限制

当前 Akio 通道仅通过 `get_hierarchy` 读取层级；custom probe 也仅返回项目与场景快照。二者都不写场景、资源或项目文件，不控制播放模式，也不修改 Editor 状态。任何写入型 Editor 操作都不在本范围。

## 已执行的验证

- `dotnet test BDTool.sln -c Release --nologo`：退出码 `0`，408 通过、0 失败、0 跳过。
- `dotnet build BDTool.sln -c Release --nologo`：退出码 `0`，0 警告、0 错误。
- `dotnet format BDTool.sln --verify-no-changes --no-restore`：退出码 `0`。
- 测试构建会显示 `AkioEditorKernel.cs:182` 的 1 个既有 CS8604 nullable 警告；该文件不在本次元数据改动范围，未修改。

以上为自动化测试、编译与格式事实；真实 Unity 6 端到端证据见上一节。

## 风险与待办

- 真实 Unity 6 证据为手工端到端执行；自动化测试仍使用 fake Provider，CI 不连接真实 Editor。
- `refresh_assets` 后观察到 1 次 ping 失败并恢复，说明 bridge 仍有瞬态失败风险；本次只确认恢复，不证明失败率为零。
- `unity-cli` 的版本/协议差异、Editor 启动时机与项目路径规范化（符号链接、映射盘）仍可能影响发现与匹配。
- 本里程碑不覆盖 M3、写操作、播放模式或全部 Unity 工具；原 TTF2 未修改。

## 本地提交说明（未执行 Git）

建议提交信息：`feat: complete BDTool M2 Akio editor read path`

提交范围：M2 Akio 只读 Editor Provider、`project.inspect`/`project.probe` 产品链路、Composition 注册、catalog 10 操作全部 available、错误与状态语义、README/0005 文档，以及版本/里程碑元数据 `0.2.0`/`M2`。不包含 Provider 行为之外的写操作、M3 或原 TTF2 改动。本记录只说明提交边界，本次未执行 Git。
