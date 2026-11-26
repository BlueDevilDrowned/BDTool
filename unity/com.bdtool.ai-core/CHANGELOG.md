# Changelog

## [0.3.0] - 2026-11-26

- 新增 Editor-only 的 Prefab 资产快照服务。
- 新增带 SHA-256 CAS、dry-run、保存后回读校验的 Prefab 根对象 `activeSelf` 写入服务。
- 移除根对象重命名服务与测试：真实 Unity 验证证明根名与文件 stem 耦合，不适合作为可靠写样本。
- 新增 EditMode 测试，覆盖读取、路径边界、CAS、失配无副作用、状态写入与保存后哈希变化。
