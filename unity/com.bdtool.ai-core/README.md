# BDTool Unity Core

`com.bdtool.ai-core` 是 Unity 2022.3 的 Editor-only 本地包，为同步调用方提供两件事：

1. 读取 `Assets/` 下 Prefab 的稳定文件与根对象事实。
2. 在 SHA-256 前置条件保护下，受控修改 Prefab 根对象的 `activeSelf`，并在落盘后重新加载回读。

## 安装

将本目录放在 Unity 项目的 `Packages/` 下，或在项目 `Packages/manifest.json` 中使用本地路径：

```json
{
  "dependencies": {
    "com.bdtool.ai-core": "file:E:/AI/BDTools/BDTool/unity/com.bdtool.ai-core"
  },
  "testables": [
    "com.bdtool.ai-core"
  ]
}
```

包没有 Runtime 程序集。`Editor/BDTool.UnityCore.Editor.asmdef` 只启用 Editor 平台，因此不会进入播放器构建。

## API

所有类型位于 `BDTool.UnityCore` 命名空间。

```csharp
AssetSnapshot snapshot = new AssetSnapshotService().Read(
    "Assets/Prefabs/Example.prefab");

PrefabActivationResult activation = new PrefabActivationService().SetActive(
    "Assets/Prefabs/Example.prefab",
    snapshot.Sha256,
    isActive: false,
    dryRun: true);
```

### `AssetSnapshotService.Read`

- 只接受 `Assets/` 下的 `.prefab` 资产路径，并拒绝绝对路径、`.`/`..` 段、空段与规范化后的越界路径。
- 资产缺失时返回 `Exists == false`，不抛出“文件不存在”异常；路径非法仍抛出 `UnityCoreException`。
- `Sha256` 是文件原始字节的 SHA-256，小写十六进制；`ChildCount` 是根对象的直接子对象数。
- `UnityVersion` 来自当前 Editor 的 `Application.unityVersion`。

### `PrefabActivationService.SetActive`

- 只操作 `Assets/` 下 `.prefab`；`expectedSha256` 必填，按大小写不敏感比较。文件当前哈希不匹配时抛出 `UnityCoreException`，错误码为 `PreconditionFailed`，且发生在 Undo/Save 之前。
- `dryRun == true` 时只做路径、CAS 与 Prefab 可加载性检查，返回预览；不创建 Undo 组，不调用保存或导入。
- 实际写入创建 Undo 组，`LoadPrefabContents` 后对根对象调用 `SetActive`，`SaveAsPrefabAsset` 保存，`SaveAssets` 与 `ForceSynchronousImport` 提交，再重新加载回读根对象 `activeSelf` 和文件哈希。
- 新旧状态相同时返回 `Changed == false`、`Saved == false` 和空 `UndoGroup`，不保存资产。
- `Verified` 只有真实保存、回读 `activeSelf == isActive` 且文件哈希改变时才为 true；任一条件不满足会抛出 `WriteFailed`，不会返回 `verified=false` 的成功结果。
- 写入的是根对象状态，不依赖 Prefab 根名或文件 stem；文件路径、文件名与根对象名保持不变。

`UnityCoreException.Code` 可取 `InvalidArgument`、`AssetNotFound`、`NotPrefab`、`PreconditionFailed`、`InvalidAsset`、`WriteFailed`。

## 测试

EditMode 测试位于 `Tests/Editor`，程序集为 `BDTool.UnityCore.Tests.Editor`。测试只创建 `Assets/__BDToolUnityCoreTests__/` 下的临时 Prefab，并在 `TearDown` 中删除对应目录。带测试的包需要在项目 `Packages/manifest.json` 的 `testables` 中加入 `com.bdtool.ai-core`，测试程序集才会出现在 Test Runner 中。

只运行 Core 包测试时，用命名空间过滤：

```text
-testFilter "BDTool.UnityCore.Tests"
```

## 边界

- 路径防越界是规范化后的词法检查，不解析符号链接或目录联接的真实目标。
- `AssetDatabase.SaveAssets()` 会提交当前 Editor 中其他已标记为脏的资产，这是 Unity API 的全局行为。
- 哈希对文件原始字节计算；Unity 重新序列化即使语义不变也可能改变字节和哈希。
- `activeSelf` 只覆盖 Prefab 根对象自身激活状态，不递归修改子对象。
- 服务全部同步执行，不启动后台任务，也不依赖外部桥接协议。
