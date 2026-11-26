# BDTool 故障排查

保留原始 JSON、退出码和 `errors[].code`，再按下面顺序处理。所有检查仍通过 `bdtool` 完成。

## 命令不可用

- 确认当前环境可以执行 `bdtool`；不要用其他编辑器、桥接或底层命令替代。
- 从目标项目根目录执行，避免相对路径落到错误项目。

## 项目未找到或未就绪

```powershell
bdtool doctor --project <项目路径> --json
bdtool context --project <项目路径> --json
```

- 核对 `projectPath` 与 `projectExists`；路径不一致时停止，向用户确认，不要猜测。
- `--project` 只指向一个项目根，不要用工作区父目录代替。

## Editor 未就绪或项目不匹配

- 请用户在目标项目中打开并完成导入，等待 Editor 报告 ready。
- 重新执行 `bdtool context --json`，确认 Editor 挂载项目与请求项目一致。
- 仍不匹配时停止；不要尝试切换后端或手工修改 Editor 状态。

## 能力缺失或操作不可用

```powershell
bdtool operations list --json
bdtool operations describe <operationId> --json
```

- 只有 `availability=available` 且所需能力存在时才继续。
- 退出码 `3` 表示能力不支持；向用户说明限制，不尝试底层替代。

## 前置条件失败、超时或结果不确定

- 退出码 `5`：重新读取目标快照或指纹，确认前置条件后再决定是否重试。
- 退出码 `6`：停止自动重试，回读项目状态并报告超时范围。
- 退出码 `8`：结果不确定，禁止盲目重试写入；先回读目标资产，再请用户确认下一步。

## 审计告警

- 出现 `AUDIT_UNAVAILABLE_PROJECT_MISSING` 等警告时，不创建目录补救；继续操作但在报告中保留告警。
