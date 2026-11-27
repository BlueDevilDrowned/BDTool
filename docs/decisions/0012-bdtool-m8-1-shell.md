# 0012 BDTool M8.1：桌面 Shell 与 Design System

- 状态：实现与真实 WPF 渲染验证完成
- 日期：2026-11-27
- 产品版本：`1.3.0`，里程碑：`M8.1`

## 目标与范围

M8.1 建立桌面控制台的基础结构，不接入 Unity、Activity、Codex 或 Formation 的真实业务数据：

- UI 无关 Presentation 层；
- 八个一级模块注册；
- WPF Shell；
- Graphite Studio Token、深浅主题；
- 统一状态语义；
- 空、加载、错误和未支持组件；
- 命令面板骨架；
- 可重复视觉捕获和架构约束。

M8.2/M8.3/M8.4 的真实数据页面仍使用占位状态，不把“页面已注册”冒充“功能已完成”。

## 依赖与所有权

```text
BDTool.Desktop
  → BDTool.Presentation
  → BDTool.Composition

BDTool.Presentation
  → BDTool.Application
  → BDTool.Contracts
```

- Presentation 不引用 WPF UI 命名空间、Provider、UnityEditor、UnityEngine 或 Codex SDK。
- Desktop 不直接引用 Provider 或 UnityEditor。
- Desktop 只负责数据绑定、视觉资源和组合根装配。
- Composition 的 `BDToolRuntime` 增加 `ICapabilityInspector`，页面通过能力快照决定可用性。

## Shell

一级模块固定为：

```text
Overview
Unity
Codex
Operations
Activity
Artifacts
Formation
Settings
```

`IUiModule` 负责 ID、标题、顺序、能力判断和页面创建。`ModuleRegistry` 负责排序、重复 ID 拒绝和能力过滤。主窗口通过选中项切换当前页，不包含模块业务分支。

## 设计 Token

权威数据：

```text
design/tokens.json
design/themes/dark.json
design/themes/light.json
```

WPF 资源：

```text
src/BDTool.Desktop/Themes/Dark.xaml
src/BDTool.Desktop/Themes/Light.xaml
src/BDTool.Desktop/Themes/Controls.xaml
```

架构测试校验 Dark 主题颜色键与 `tokens.json` 完全一致，并禁止 Views/Controls 散落十六进制颜色。

## 状态语义

`StatusSemantics` 统一映射：

```text
Ready / Completed / Passed      → Success
Running / Dispatching           → Running
NeedsRework / CancelRequested   → Warning
Blocked / Failed                → Failed
DeliveryUnknown / Uncertain     → Uncertain
Unsupported                     → Unsupported
Cancelled                       → Cancelled
```

颜色、图标文字和状态词由 Presentation/Desktop 共享，页面不能另造状态词。

## 视觉捕获

Desktop 支持环境变量：

```text
BDTOOL_CAPTURE_PATH=<png path>
```

该入口使用正常 Application 资源、DataTemplate 和绑定，将真实 WPF 内容离屏渲染为 1440×900 PNG。它解决 150% DPI 下屏幕物理尺寸限制导致的截图裁切，不伪造页面数据。

真实验证文件：

```text
E:\AI\BDTools\M7TestArea\visual-m8\m8.1-shell-dark-1440x900-offscreen.png
```

截图检查确认：

- 标准窗口运行成功；
- 左侧八模块导航完整；
- 顶部项目、Unity、Provider 和命令区可见；
- 中央页面与右侧 Inspector 不重叠；
- Activity 抽屉高度足够；
- 中央页面背景不再回退为系统白色。

## 自动验证

- 八模块顺序和默认页；
- 模块重复 ID 拒绝；
- 状态文本统一映射；
- Placeholder 使用空状态；
- Presentation 依赖方向；
- Desktop TargetFramework/UseWPF；
- Theme Token 一致；
- View 无散落颜色。

## 限制

- Unity、Codex、Activity、Artifacts、Formation 当前是占位页。
- 命令面板只提供 Shell 骨架和模块导航，操作命令注册属于后续阶段。
- 当前只验证 1440×900 Dark；1280/1920、浅色、125%/150% DPI 和完整状态截图属于 M8.4。
- 不提供自定义标题栏、Scene View、Prefab Mode 或完整 Profiler。
