# 0006 BDTool M3：Unity Core 快照与单 Prefab activeSelf 写入（已完成并真实验证）

- 状态：实现、真实 Unity 端到端验证与版本元数据收口均已完成
- 日期：2026-11-26

## 背景与范围

M3 子任务 4 先收口 Host 侧：在已冻结的 Contracts 之上补充两个 Operations Handler、Composition 注册、catalog 描述、最小测试与文档；M3 收尾时再完成真实 Unity 验证与产品身份元数据收口。范围明确限定为：

- `project.asset-snapshot`：通过 Editor Provider 读取单个 Prefab 的稳定快照。
- `project.prefab-set-active`：在 SHA-256 CAS 保护下切换单个 Prefab 根对象的 activeSelf，支持 dry-run、Undo、保存与回读验证。
- 两个操作都只经由 `IEditorKernel`/`IWorkspaceKernel` 端口访问外部世界；Handler 不直接访问进程、Editor、Unity 或 Provider 私有类型。
- 不包含 multi-asset、bulk、场景写入、播放模式或 M4 能力；Host 子任务不改写 Provider 与 Unity 包实现，产品身份元数据（`0.3.0`/`M3`）在 M3 收尾阶段单独提升。

依赖冻结项保持不变：`ProjectAssetSnapshotData`、`PrefabActivationData`、`EditorToolWrite`、`EditorOperationRequest.Mutating`；operation ID 采用连字符形式 `project.asset-snapshot` 与 `project.prefab-set-active`，上游 tool 名保持 `bdtool_core_read` 与 `bdtool_core_prefab_set_active` 不变。`BDTool.Provider.Akio` 的 ID 映射由主线同步，本子任务未修改该包。

选择 activeSelf 而不是重命名根对象，是为了避免把 Unity 资产文件 stem 与结果校验耦合。测试和证明都围绕根对象状态、保存后的回读和磁盘 SHA，而不是文件名。

## 包与依赖

- 新增 `src/BDTool.Operations/Projects/ProjectAssetSnapshotHandler.cs`。
- 新增 `src/BDTool.Operations/Projects/PrefabSetActiveHandler.cs`。
- `src/BDTool.Composition/BDToolComposition.cs` 显式注册两个 Handler，并参与 `EnsureConsistent(catalog)` 校验。
- `BDTool.Operations` 仍只依赖 `BDTool.Contracts` 与 `BDTool.Core`；架构测试新增源码禁用检查，禁止 `System.Diagnostics`、`System.Net`、`UnityEditor`、`UnityEngine` 与 `BDTool.Provider` 的 using。

## 目录

`catalog/operations.json` 现为 12 个操作，全部 `available`：

- M1 八个：`system.doctor`、`operations.list`、`operations.describe`、`project.context`、`workspace.read`、`workspace.search`、`workspace.fingerprint`、`workspace.apply`。
- M2 两个：`project.inspect`、`project.probe`。
- M3 两个：`project.asset-snapshot`、`project.prefab-set-active`。

`project.asset-snapshot`：`read` + `editor`，要求 `editor.status`、`editor.tool.read`，输入严格为 `{assetPath}`。

`project.prefab-set-active`：`editor-write` + `editor`，要求 `editor.status`、`editor.tool.write`，输入严格为 `{assetPath, expectedSha256, isActive, dryRun?}`，`additionalProperties=false`，结果 schema 为 typed `PrefabActivationData`。

## project.asset-snapshot

1. 只接受必填非空 `assetPath`；未知字段直接 `INVALID_ARGUMENT`，不会透传到 Provider。
2. 通过 `context.Editor.SubmitAsync/GetResultAsync` 提交 `Mutating=false`、空 `WriteScope` 的只读请求。
3. 结果必须是 JSON 对象，并严格读取 `assetPath`、`exists`、`sha256`、`rootObjectName`、`childCount`、`unityVersion`。
4. 资产存在时 `sha256` 必须非空；字段缺失、类型错误或不完整都按 `EXECUTION_FAILED` 处理，不用默认值伪造成功快照。

## project.prefab-set-active

### 输入与 CAS

1. 严格校验 `assetPath`、`expectedSha256`、必填布尔 `isActive` 与可选布尔 `dryRun`；未知字段不提交。`isActive` 缺失不会被折叠成 `false`。
2. 请求级 `DryRun` 与输入级 `dryRun` 取并集：任一为 true 都不得写入资产。
3. Submit 之前调用 `context.Workspace.FingerprintAsync` 获取 pre-SHA；目标不存在或哈希与 `expectedSha256` 不一致立即返回 `PRECONDITION_FAILED`，且不创建 Editor Job。

### 写请求

满足 CAS 后提交 `EditorOperationRequest`：

- `Mutating=true`
- `WriteScope=[assetPath]`
- `ExpectedFingerprint=expectedSha256`
- `DryRun=effectiveDryRun`

上游 tool 为 `bdtool_core_prefab_set_active`。Provider 负责 Undo 组、保存、强制同步导入与回读；Operations 只消费 typed 结果并校验一致性。

### 写后验证与证据

- 非 dry-run 必须满足 `changed=true`、`saved=true`、`verified=true`、回读 `isActive` 等于请求值、`wasActive != isActive`，且 `newSha256 != previousSha256`。
- 完成后再读取一次 Workspace 指纹作为 post-SHA，并与 Editor 返回的 `newSha256` 对照；不一致返回 `Uncertain`。
- 填充 `ChangedAssets`（仅发生实际写入时）、`Proof.InputFingerprint`、`Proof.OutputFingerprint`、`Proof.Evidence` 与 warnings。
- dry-run 不创建 Undo、不保存、不修改磁盘，也不要求文件 SHA 变化；`Proof.InputFingerprint` 与 `Proof.OutputFingerprint` 都等于 pre-SHA。

### 不确定语义

- 提交后超时或取消：返回 `OperationStatus.Uncertain` + `EXECUTION_UNCERTAIN`，因为写入可能已部分发生。
- 非 dry-run 收到 completed 但 `changed=false`、保存/回读/哈希验证不完整或无法确认写后指纹：同样返回 `Uncertain`。
- dry-run 的超时/取消可保持普通 `TIMEOUT`/`CANCELLED`，因为资产未被写入。

## 验证状态

- 静态边界：`BDTool.Operations` 源码禁用进程、网络、Unity 与 Provider 直接依赖的架构测试。
- 单元测试：覆盖快照 typed 映射、严格输入、CAS 不提交、真实写证据、dry-run SHA 不变、post-SHA 不一致和提交后超时 Uncertain。
- Release 构建：`dotnet build BDTool.sln -c Release --nologo` 退出码 `0`，0 警告、0 错误。
- 完整测试：`dotnet test BDTool.sln -c Release --nologo --no-build` 退出码 `0`，437/437 通过。
- 格式校验：`dotnet format BDTool.sln --verify-no-changes --no-restore` 退出码 `0`。
- 版本输出：`bdtool --version` 输出 `bdtool 0.3.0 (contract 1.0.0, milestone M3)`，退出码 `0`。

### 真实 Unity 端到端验证

- 环境：Unity `6000.6.0f1`，fixture `E:\AI\BDTools\M3TestArea\Unity6Fixture`；`com.bdtool.ai-core` 与 `com.bdtool.ai-adapter-akio` 以 embedded 方式安装成功。
- Core EditMode 过滤测试 25/25 通过，结果文件 `E:\AI\BDTools\M3TestArea\Results\core-editmode-final2.xml`，根节点 `result="Passed"`。
- adapter tools schema：`bdtool_core_read` 为 `mutating=false`，`bdtool_core_prefab_set_active` 为 `mutating=true`，与 Handler 侧请求语义一致。
- `project.asset-snapshot` 在真实 Prefab 上成功返回 typed 快照。
- dry-run：`Saved=false`，文件 SHA 不变，未产生 Undo 或磁盘写入。
- 真实写入：activeSelf `true` → `false`，`Saved=true`、`Verified=true`，磁盘 SHA `c11...` → `031...`。
- stale CAS：以过期 `expectedSha256` 提交返回 exit `5`/`PRECONDITION_FAILED`，SHA 不变，未创建 Editor 写入。
- 恢复：将 activeSelf 设回 `true` 后 SHA 回到 `c11...`，证明写入可逆。
- Activity：`providerId=akio`、状态 `completed`、`changedAssets=Assets/BDToolM3/ActivationProbe.prefab`。
- `refresh_assets` 后先观察到 1 次 ping 失败，随后 bridge 恢复，新工具被重新发现并完成写入。

### 版本元数据

- 产品版本 `0.3.0`、里程碑 `M3`，同步于 `BDToolIdentity`、`Directory.Build.props` 与 `BDTool.Provider.Akio` 的 `ImplementationVersion`。

## 明确不包含

- multi-asset、bulk、场景写入、播放模式、M4 能力。
- 修改 Contracts、`BDTool.Provider.Akio` 或 Unity 两个包的行为；M3 收尾只更新版本元数据（产品 `0.3.0`/`M3`，Provider 仅同步声明 `ImplementationVersion=0.3.0`）。
- Git 写入。
