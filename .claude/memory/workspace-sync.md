---
name: workspace-sync
description: Windows D:\workspace\board 与 WSL /home/cui/workspace/board 的同步状态与合并历史
metadata:
  type: project
---

# 工作区同步

## 2026-09-13 合并结果

- Windows 仓库：`D:\workspace\board`，分支 `master`，原历史从 2026-07-17 开始，含 Unity 客户端动画工作。
- WSL 仓库：`/home/cui/workspace/board`，分支 `main`，原历史从 2026-08-19 `Initial commit` 开始，含后端重构、游戏数据与讲规 TTS 工作。
- 两边是**不相关历史**，直接 `git pull` 会产生 195 个 add/add 冲突。
- 实际合并方式：以 WSL 根提交 `810cb95` 的 tree 作为 synthetic base，`git replace --graft` 两个根提交后做三路合并；冲突 31 个，按“Windows 客户端/删除侧保留、WSL 数据/新增侧保留”策略解决。
- 合并提交：`33ab20d Merge remote-tracking branch 'wsl/main' into sync/merged`。
- 合并后 `D:\workspace\board`（master）和 WSL 仓库（main）都已 fast-forward 到该提交。
- Windows 工作区原有未提交修改全部保留（`.claude/settings.local.json`、`BoardAI.Api.csproj`、`EmbeddingService.cs`、QA jsonl 等）。

## 后续同步方式

- WSL 仓库有本地 remote `windows` → `/mnt/d/workspace/board`。
- WSL 中拉 Windows：
  `git fetch windows master:refs/remotes/windows/master && git merge --ff-only refs/remotes/windows/master`
- Windows 中拉 WSL（在 WSL 环境下执行）：
  `git -C /mnt/d/workspace/board fetch /home/cui/workspace/board main:refs/remotes/wsl/main && git -C /mnt/d/workspace/board merge --ff-only refs/remotes/wsl/main`
- GitHub origin 已存在，但推送需要可用凭证；2026-09-13 本地同步完成，GitHub 推送未完成。

**Why:** Windows 和 WSL 两个工作区之前是两套不相关 Git 历史，新会话需要知道它们已经合并以及如何继续同步。
**How to apply:** 之后优先在同一个历史里提交；跨工作区同步优先用本地 remote/fast-forward，不要再做无关历史 merge。
