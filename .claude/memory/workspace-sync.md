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

- **两个工作区同一条历史**，都在分支 `main`；GitHub(origin) 是共同真相。
- 本地还有两个离线通道：WSL 里的 remote `windows` → `/mnt/d/workspace/board`，
  Windows 里的 remote `wsl` → `/home/cui/workspace/board`（断网也能互相同步）。
- 统一用工具，别手敲 git：
  ```
  ./scripts/sync_workspaces.sh status          # 两边状态 + 差几个提交 + 是否分叉
  ./scripts/sync_workspaces.sh from-windows    # Windows 的提交同步到 WSL
  ./scripts/sync_workspaces.sh from-linux      # WSL 的提交同步到 Windows
  ./scripts/sync_workspaces.sh push            # WSL → GitHub（只快进）
  ```
  **只快进**：两边各有新提交（分叉）时它拒绝，让人决定；永不 force、永不 reset。
- 大文件如果要在多机使用，放仓库外共享目录或后续引入 Git LFS；不要提交进 Git。
- `.gitignore` 已加 `games/*/media/` 与 `client/CaptureOut/`：原始扫描/实物照片与出图产物
  都是**本地素材**，不跟踪也不该被 `git clean` 顺手删掉。

## 分工：**改动画在 Windows 下改**（2026-09-18 用户定）

| 改什么 | 在哪改 | 之后 |
|---|---|---|
| **动画数据**（`games/splendor/tutorial/anim/full.json`、`anim/_stage/*.table.json`） | **Windows**（`D:\workspace\board`） | 在 Windows 提交 → `sync_workspaces.sh from-windows` |
| 工具 / 文档 / 记忆 / 后端 | WSL（`/home/cui/workspace/board`） | 在 WSL 提交 → `push` → `from-linux` |

理由：Unity 读的就是 Windows 那份，**所见即所改**，省掉来回拷贝；而校验/对账/TTS 这些工具
在 WSL 跑更顺手。

⚠️ **一个必须记住的坑**：校验器（`validate_cue_anim.py`）与对账（`check_cue_script.py`）读的是
**当前仓库**里的文件。所以顺序永远是 **改 → 提交 → from-windows → 再校验/对账**；
反过来先校验，校验的是 WSL 的旧副本，会得出假结论。

## 2026-09-18 事故：Windows 落后 124 个提交，宝石动画"什么都没看到"

用户反馈：口播在讲宝石，画面却停在 cue13。根因不是代码 ——
**Windows 工作区停在 2026-09-13（51e98d8），落后 124 个提交**，那边根本没有宝石那 9 条动画，
引擎对每一条都走"无动画数据 → 保留牌桌"，画面于是不动。而引擎只在 log 级说了 100 次，
等于没说 —— 典型的静默失败。

已做的三件事：

1. **同步到一致**：Windows 快进到与 WSL/origin 同一个提交（`git checkout -B main wsl/main` 前，
   先把"未跟踪但新版已跟踪"的 27 个文件挪开、把落后的已跟踪改动归位；两者的内容都验证过
   **在 WSL 历史里找得到**，没有独有内容被丢）。
2. **引擎加自检**：第一次读脚本时打印"脚本 N 段 cue，其中 M 段有事件"；
   **某条 cue 在脚本里根本找不到时第一次就报 error** 并提示工作区可能没同步。
3. **Windows 侧 4 个独有改动用 `git stash` 保住了**（见下），没有静默丢弃。

## Windows 侧被 stash 的 4 个本地改动（待用户决定）

```
git -C /mnt/d/workspace/board stash list        # stash@{0}
git -C /mnt/d/workspace/board stash show -p stash@{0}
```

| 文件 | 内容 |
|---|---|
| `backend/BoardAI.Api/BoardAI.Api.csproj` | 用 `Microsoft.ML.OnnxRuntime.**Gpu.Linux**` 1.21.2（WSL 那边是 CPU 版） |
| `backend/BoardAI.Api/Services/EmbeddingService.cs` | 建 InferenceSession 时先 `AppendExecutionProvider_CUDA(0)`，失败回退 CPU（比 WSL 多 17 行） |
| `.claude/settings.local.json` | 本机设置（114 行 vs 113 行） |
| `.claude/memory/MEMORY.md` | 记忆索引，与 WSL 版不同 |

**没带进 WSL 的原因**：那两个后端文件会改变 WSL 后端的构建依赖（要能还原 Gpu.Linux 包），
属于基础设施决定，不该由同步顺手带上。要合的话说一声。

**Why:** Windows 和 WSL 两个工作区之前是两套不相关 Git 历史；现在已经合并并清理了历史大文件，新会话需要知道当前同步方式与资产策略。
**How to apply:** 之后优先在同一历史里提交；音频/视频/模型/PDF/原始扫描只放本地，加 `.gitignore`，需要时用共享目录同步。
