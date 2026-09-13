---
name: workspace-sync
description: Windows D:\workspace\board 与 WSL /home/cui/workspace/board 的同步状态、合并历史与大型资产忽略策略
metadata:
  type: project
---

# 工作区同步

## 2026-09-13 合并结果

- Windows 仓库：`D:\workspace\board`，分支 `master`，原历史从 2026-07-17 开始，含 Unity 客户端动画工作。
- WSL 仓库：`/home/cui/workspace/board`，分支 `main`，原历史从 2026-08-19 `Initial commit` 开始，含后端重构、游戏数据与讲规 TTS 工作。
- 两边是**不相关历史**，直接 `git pull` 会产生 195 个 add/add 冲突。
- 合并方式：以 WSL 根提交 `810cb95` 的 tree 作为 synthetic base，`git replace --graft` 两个根提交后做三路合并；冲突 31 个，按“Windows 客户端/保留删除侧、WSL 数据/新增侧保留”策略解决。
- 原合并提交为 `33ab20d`。

## 2026-09-13 历史清理

- 合并推送后 GitHub 提示 `backend/BoardAI.Api/ml_models/bge-small-zh/model.onnx.data` 约 91MB，另有 Civolution PDF/原始扫描等大文件被一起推入历史。
- 用户定稿：**模型、音频、视频、PDF、大页原始扫描不进 Git**。
- `.gitignore` 已新增：
  - 模型：`*.onnx`、`*.onnx.data`、`*.pt`、`*.pth`、`*.safetensors`、`*.gguf`、`*.bin`、`backend/BoardAI.Api/ml_models/`
  - 音频/视频：`*.mp3`、`*.wav`、`*.ogg`、`*.flac`、`*.m4a`、`*.aac`、`*.opus`、`*.mp4`、`*.mov`、`*.mkv`、`*.webm`、`*.avi`
  - 规则书/原始图：`*.pdf`、`games/civolution/page-*.jpg`、`games/civolution/*_600dpi*.jpg`、`games/civolution/_review_pages/`
- 用 `git filter-repo` 重写历史，移除上述 blob 后 force-push；新历史合并提交 `5793137`，清理后的同步点 `e8d040a chore: ignore large model/audio/video/raw scan assets`。
- 清理后 GitHub main 从 206cbb9 force-update 到 `e8d040a`；WSL 与 Windows 本地仓库都已 reset 到该历史。
- TTS mp3、PDF、原始扫描等本地文件已从备份恢复，现为 ignored 本地资产，不参与 Git。

## 后续同步方式

- WSL 仓库有本地 remote `windows` → `/mnt/d/workspace/board`。
- Windows 仓库 origin 已指向 GitHub；WSL origin 也已指向 GitHub。
- 跨工作区同步优先走 GitHub 或本地 remote + fast-forward；不要再次做无关历史 merge。
- 大文件如果要在多机使用，放仓库外共享目录或后续引入 Git LFS；不要提交进 Git。

**Why:** Windows 和 WSL 两个工作区之前是两套不相关 Git 历史；现在已经合并并清理了历史大文件，新会话需要知道当前同步方式与资产策略。
**How to apply:** 之后优先在同一历史里提交；音频/视频/模型/PDF/原始扫描只放本地，加 `.gitignore`，需要时用共享目录同步。
