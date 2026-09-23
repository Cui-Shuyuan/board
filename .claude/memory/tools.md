---
name: tools
description: 服务启动、索引、校验、QA、讲规动画编译与常用脚本入口
metadata:
  type: project
---

# 工具与命令

## 1. 本地服务

### BoardAI.Api

```bash
cd backend/BoardAI.Api
dotnet run --urls "http://localhost:5000"
```

Windows 环境（项目文档中的原始路径）：

```bat
D:\dotnet\dotnet.exe run --project D:\workspace\board\backend\BoardAI.Api --urls http://0.0.0.0:5000
```

- API key 只走环境变量：`DEEPSEEK_API_KEY` 或 `LLM__ApiKey`，不要写进 `appsettings.json`。
- 改规则文件后必须重启 API：`GameRulesService.LoadJson` 按路径缓存且当前不失效。

### Qdrant

```powershell
cd D:\qdrant
qdrant.exe
```

- 版本 1.18.3，Windows 原生二进制。
- gRPC 6334（.NET），HTTP 6333（Python 重建脚本）。
- 数据持久化在 `D:\qdrant\data`；必须在 `D:\qdrant` 目录下启动，否则会在仓库根生成 storage 垃圾目录。

## 2. 规则校验与规范化

```bash
python scripts/validate_rules.py                 # 全部文件
python scripts/validate_rules.py --game splendor # 单游戏
python scripts/validate_rules.py --errors-only   # 只输出 ERROR，供 hook
python scripts/normalize_json.py                 # JSON 格式规范化
python scripts/check_no_secrets.py               # 扫描已跟踪文件里的明文密钥
```

当前全库：0 errors / 72 warnings。

## 3. 向量索引

```bash
python scripts/rebuild_index.py --all
python scripts/rebuild_index.py --game splendor
python scripts/rebuild_index.py --all --full
```

- 默认增量同步，按 `content_hash + model_tag` diff。
- 模型/提取逻辑大改时用 `--full`。
- 模型目录：`backend/BoardAI.Api/ml_models/`（gitignore）。
- 当前模型：`bge-base-zh-v1.5-fp32`，768 维。
- 冷启动顺序：Qdrant → 重建索引 → 启动 API。

## 4. 检索评测

```bash
python scripts/eval_retrieval.py --gold qa/retrieval_gold.jsonl --api http://localhost:5000
```

- Gold set：`qa/retrieval_gold.jsonl`，当前 85 条，覆盖 9 款游戏。
- 直接调用 `POST /api/rules/games/{game}/execute-plan`，不经过 LLM 回答。
- 指标：resolved_hit / resolved_wrong / candidate_top1 / candidate_top3 / unresolved / no_match。
- 最近记录：82/85 resolved_hit，wrong=0，no_match=0。

## 5. 讲规动画

### 总控编译

```bash
python3 scripts/compile_tutorial.py --game splendor --track full
python3 scripts/compile_tutorial.py --game splendor --track full --dry-run
python3 scripts/compile_tutorial.py --game splendor --track full --skip-tts
python3 scripts/compile_tutorial.py --game splendor --track full --validate-qa
```

### 编译与检查

```bash
python3 scripts/anim_schema_v2.py games/splendor/tutorial/anim/v2/full.anim.json
python3 scripts/compile_animation_v2.py --game splendor --track full
python3 scripts/compile_animation_v2.py --game splendor --track full --check
python3 scripts/check_anim_v2.py --game splendor --track full
python3 scripts/validate_anim_rules_v2.py --game splendor --track full
python3 scripts/check_unity_scripts.py
```

### 时间锚点

```bash
python3 scripts/migrate_time_anchors_v2.py
```

### Unity 采样对账

```bash
./scripts/dump_anim_v2.sh --game splendor --track full
python3 scripts/check_anim_v2_sample.py --game splendor --track full
```

### 结构编辑

```bash
python3 scripts/cue_graph_v2.py --help
```

`cue_graph_v2.py` 支持 insert / delete / split / merge，只维护 cue 链表、parent/entry 和 children 重接，不负责 TTS/runtime。

### LLM 写作规范

`games/splendor/tutorial/anim/v2/LLM-ANIMATION-GUIDE.md`

## 6. TTS

- `scripts/tts_doubao.py`：火山豆包语音合成 2.0，输出 mp3 + subtitle.json。
- `scripts/requirements-tts.txt`：TTS 依赖。
- `scripts/validate_timed_script.py`：LRC-like 口播脚本校验/解析。
- `scripts/split_lrc_long_cues.py`：过长 cue 拆分。
- `scripts/build_tutorial_runtime.py`：编译运行时 cue 数据。
- `scripts/tutorial_script_tool.py`：分层编辑源 split/merge/set-pause。
- `scripts/rebuild_tutorial.py`：source → LRC → TTS → runtime 一条命令。

## 7. QA / 回归

- 规则 QA 脚本按游戏散落在 `scripts/_qa_<game>_run.py`、`_qa_<game>_log_analysis.py`。
- 结果文件：`scripts/_qa_<game>_results.jsonl`。
- 动画 QA：`scripts/qa_anim_ask.py` 是主流程——**问题必须手写**，脚本只发送 Board API 问答并留档；`qa_anim_check.py` 可交叉验证；`qa_anim_percue.py` 是旧生成器，不作为主流程。
- 常用检查：
  - `scripts/check_unity_scripts.py`：Unity C# 编译检查。
  - `scripts/check_anim_v2.py`：编译/契约/状态/机位检查。
  - `scripts/check_anim_v2_sample.py`：Unity 采样与 compiled 逐 item 对账。

## 8. 工作区同步与 Unity

```bash
./scripts/sync_workspaces.sh from-linux    # 数据推到 Windows
./scripts/sync_workspaces.sh from-windows  # Windows 拉回
./scripts/sync_workspaces.sh status
```

Unity 批处理采样使用 Windows 侧编辑器（项目文档中的路径）：

```text
/mnt/d/Unity/Hub/Editor/6000.5.8f1/Editor/Unity.exe -batchmode -projectPath 'D:\workspace\board\client' ...
```

运行时快捷键：`G` 开动画，`B` 轮换关键帧，空格暂停，`←/→` 逐步，`A` 自动播。

## 9. 常用入口

- `backend/BoardAI.Api/README.md`
- `client/docs/`
- `games/splendor/tutorial/anim/v2/README.md`
- `games/splendor/tutorial/anim/v2/LLM-ANIMATION-GUIDE.md`
- `tutorial/README.md`
- `docs/tutorial-animation-refactor.md`

## 10. 环境注意

- WSL 启动 Windows 服务时环境变量不会自动传入，需在 `cmd.exe /c "set KEY=...&& dotnet ..."` 里设置。
- Windows 路径为 `D:\workspace\board`；WSL/当前工作区路径以实际为准。
- 模型、音频、视频、PDF、原始扫描、QA 生成物不进 Git。
