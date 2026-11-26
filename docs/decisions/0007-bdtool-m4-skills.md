# 0007 BDTool M4：项目级 Codex Skill 与安全安装

- 状态：已完成并验证
- 日期：2026-11-26

## 背景与范围

M4 把 BDTool 的可操作入口封装成两个项目级 Codex Skill，让 Agent 通过稳定 CLI 使用现有能力，而不是猜测或直接调用具体 Editor Provider。

本里程碑只包含：

- 权威 Skill 源：`skills/bdtool-overview` 与 `skills/bdtool-unity-work`。
- Agent 使用说明、触发/非触发边界、操作契约与验证清单。
- `<project>/.agents/skills` 安装与受管更新。
- `bdtool skills install` 路由、稳定 JSON/人类输出、冲突与退出码。
- 发布输出复制、测试、文档、`0.4.0`/`M4` 身份同步。

本里程碑不包含新的 catalog operation、Provider 行为、Unity 写入能力、用户级 Skill 安装、MCP Adapter、Agent 编队或自动修改 Codex 配置。

## Skill 边界

### `bdtool-overview`

仅在用户明确提到 BDTool，或当前请求很可能需要通过 BDTool 完成时使用。Skill 先执行 `bdtool doctor --json` 与 `bdtool context --json`，根据真实 Provider 与能力快照判断后续；写入请求再路由到 `bdtool-unity-work`。普通软件问答且未涉及 BDTool 时不触发。

### `bdtool-unity-work`

仅在用户明确要求通过 BDTool 检查或修改 Unity 项目、场景、Prefab、资源配置或验证结果时使用。普通 Unity 问答不触发。写入前必须取得当次项目、目标资产和预期变更授权，按“快照 → dry-run → 真实写入 → 回读 → 验证 → 报告”执行。

当前写入能力仍限定为单 Prefab 根对象 activeSelf。dry-run 的成功口径是 `changed=true`、`saved=false`、`verified=false`，且文件 SHA 不变；不能把 `changed=false` 当成必要成功条件。

两个 Skill 的命令示例只使用 `bdtool`，不暴露 `unity-cli`、`BDTool.Provider.Akio`、`BDTool.Provider.Local` 或旧 `myunity` 命名。

## 安装器设计

### 输入与目标

- 固定源根：CLI 发布目录下的 `skills/`。
- 固定 Skill：`bdtool-overview`、`bdtool-unity-work`。
- 固定作用域：`project`。
- 固定目标：`<project>/.agents/skills/<skill>`。

项目根必须真实存在，不接受缺失项目自动创建。源 Skill 必须包含根级 `SKILL.md`，其全部文件先读入内存快照，确保校验和复制使用同一版本。

### 防覆盖与冲突

目标目录只有在包含合法 `.bdtool-managed` 标记时才允许接管。标记使用纯文本格式，记录格式版本、Skill 名称和源树 SHA-256；不反序列化项目内对象，不执行项目内配置。

安装前会对两个 Skill 一起完成：

- 源树完整性读取。
- 目标是否存在、是否目录、标记是否合法。
- reparse point、设备文件与路径逃逸检查。
- 受管目录内嵌套链接检查。

任一目标冲突时返回全部 `data.conflicts`，`data.skills` 为空，且两个目标都不写入。未受管目录、非法标记、文件占位目标或链接目录都不会被覆盖。

### 写入与更新

两个目标都通过预检后才创建 `.agents/skills`。文件按源树相对布局写入，并用 `AtomicFile` 做同目录原子替换；标记最后刷新。第二次安装会在受管目录刷新并报告 `updated=true`，但安装不进入 catalog 或 Activity。

### 失败语义

- 参数、作用域或路径不安全：`INVALID_ARGUMENT`，退出码 `2`。
- 项目不存在：`PROJECT_NOT_FOUND`，退出码 `4`。
- 未受管冲突或父路径被文件占用：`PRECONDITION_FAILED`，退出码 `5`。
- 源文件缺失：`NOT_FOUND`，退出码 `2`。
- 意外读写失败：`EXECUTION_FAILED`，退出码 `5`。

所有 CLI 失败在默认模式下仍向 stdout 输出一个合法统一 JSON 信封；人类模式只作为显式展示选项。

## 发布结构

`BDTool.Cli.csproj` 将仓库 `skills/**/*` 复制到 CLI 输出的 `skills/`，并使用 `Link` 保留 `references/` 层级。测试同时验证源文件存在与 CLI 实际从输出目录安装，避免出现“仓库有文档、发布包没有”的假通过。

## Agent 调用约束

- 只调用稳定 CLI，不依赖项目内源码路径或 Provider 私有参数。
- `skills install` 只安装文件；能力与 Editor 状态仍以 `doctor`、`context`、`operations list/describe` 的真实返回为准。
- 不把文件存在当作 Codex 已发现；必须用当前 Codex 的模型可见输入或新会话确认项目 Skill 被加载。
- 不把普通 Unity 问答描述成 BDTool 操作触发。
- 写入报告必须包含 `changedAssets`、`proof` 和未验证项；超时、取消或 `EXECUTION_UNCERTAIN` 不得报告为成功。

## 验证结果

1. `dotnet build BDTool.sln -c Release --nologo`：成功，0 警告、0 错误。
2. `dotnet test BDTool.sln -c Release --no-build --nologo`：457/457 通过，0 失败、0 跳过。
3. `dotnet format BDTool.sln --verify-no-changes --no-restore`：退出码 `0`，无待格式化变更。
4. 隔离目录 `E:\AI\BDTools\M4TestArea\SkillFixture\CleanProject`：
   - 首次安装退出码 `0`，两个 Skill 的 `updated=false`。
   - 二次安装退出码 `0`，两个 Skill 的 `updated=true`。
   - 两个 `SKILL.md`、三个 reference 文件和两个 `.bdtool-managed` 均存在且层级正确。
   - 安装后 `.bdtool` 不存在，证明安装路由不写 Activity。
5. 隔离目录 `ConflictProject`：
   - 预置未受管 `bdtool-overview` 与用户文件。
   - 安装退出码 `5`，错误码 `PRECONDITION_FAILED`，`data.skills` 为空并返回 `data.conflicts`。
   - 用户文件保持不变，未生成标记，另一个 Skill 未落盘，`.bdtool` 不存在。
6. Codex CLI `0.146.0` 发现验证：
   - `codex debug prompt-input` 的模型可见 Skill 列表包含 `bdtool-overview` 与 `bdtool-unity-work`。
   - 两项源路径均指向隔离项目的 `.agents/skills/<name>/SKILL.md`，不是仓库源路径。
7. 触发验证：
   - “通过 BDTool 检查当前项目状态”会读取 Skill 并实际执行 `bdtool doctor`、`bdtool context` 与 `bdtool operations list`。
   - “通过 BDTool 只读检查 Assets/Prefabs/Missing.prefab”会读取 Unity 工作流程，读取操作描述并在能力缺失时停止，不修改项目。
   - “请解释 Unity 中 GameObject.activeSelf 与 SetActive 的区别”没有读取 BDTool Skill、没有调用工具，直接完成知识问答。
8. `bdtool --version` 输出 `bdtool 0.4.0 (contract 1.0.0, milestone M4)`；`--help` 列出 `skills install` 与 M4 范围。

触发验证在 Codex 只读 sandbox 中执行，因此 `doctor` 对 `.bdtool` 的写入检查按预期报告权限失败并产生 `ACTIVITY_WRITE_FAILED`；这证明 Skill 遵守了只读边界，不表示安装或产品回归失败。原 TTF2 项目未参与本次安装或写入验证。
