---
name: tools
description: 服务启动、校验、索引、测试、语音、Android、动画编译与常用脚本入口
metadata:
  type: project
---

# 工具与命令

> 运行时路径统一使用 `BOARD_RUNTIME_ROOT`（具体值由 git-ignored 本地配置赋值）。下文临时产物写在 `$BOARD_RUNTIME_ROOT/tmp/`，模型在 `$BOARD_RUNTIME_ROOT/models/`，生成 manifest/release 在 `$BOARD_RUNTIME_ROOT/content/`。

## 1. 本地服务

### BoardAI.Api

```bash
cd backend/BoardAI.Api
dotnet run --urls "http://0.0.0.0:5000"
```

- API key 只走环境变量：推荐 `LLM__ApiKey`，兼容 `DEEPSEEK_API_KEY`。
- 规则 JSON 由 `RulesDocumentStore` 按 `Length + LastWriteTimeUtc` 自动 freshness；改规则文件无需重启。
- 语义检索仍需重建 Qdrant 索引；`content/catalog/*.json` 与 `$BOARD_RUNTIME_ROOT/content/manifests/*.json` 每次请求读取，无需重启。

### Qdrant

```powershell
# %QDRANT_HOME_WIN% 来自根目录 config/local.properties
cd %QDRANT_HOME_WIN%
qdrant.exe
```

- 版本 1.18.3，Windows 原生；HTTP 6333（Python），gRPC 6334（.NET）。
- 必须在 Qdrant 自身目录启动，避免在仓库根生成 storage。

## 2. 规则校验与测试

```bash
python3 tools/content/validate_rules.py --errors-only   # 当前 0 errors / 72 warnings
python3 tools/content/normalize_json.py
python3 tools/ops/check_no_secrets.py
python3 -m unittest discover -s animation -p 'test_*.py'   # 当前 227/227（含素材库/夹具/边界/门禁测试）
```

后端：

```bash
dotnet test backend/BoardAI.Api.Tests/BoardAI.Api.Tests.csproj --nologo   # 当前 198/198
```

Android JVM：测试源码 13 个文件 / 104 个测试（ContentStatus、ContentStoreCleanup、ContentUpdater、HomeContentCoordinator、PlayerSessionController、PlayerTimelineBar、UnityLoadQueue、QaEvidenceParsing、QaPanelMode、QaRepository、QaSessionHolder、QaVoiceController 等）。2026-10-17 在 Windows 侧用 `gradlew.bat testDebugUnitTest` 重跑 104/104 通过；不等于真机验收。

pre-commit：

```bash
sh tools/ops/install_git_hooks.sh
```

hook 调用 `tools/content/validate_rules.py --errors-only`，只报告不阻止提交。

## 3. 向量索引

```bash
python3 tools/indexing/rebuild_index.py --all
python3 tools/indexing/rebuild_index.py --game splendor
python3 tools/indexing/rebuild_index.py --all --pooling cls --no-switch
```

- 每次构建生成 `board_{game}__v{version}` / `...__name` 两个具体 collection，校验点数后原子切换 `board_{game}__active[_name]`；失败保留旧 alias。
- Python CLI 与 C# `IndexContract` 共用身份路径：普通概念 `concept_id`，局部槽位 `<owner>.<slot_id>`。`id/name/material` 只作元数据。
- 模型目录 `$BOARD_RUNTIME_ROOT/models/`（运行时目录，不入 Git）；当前 `bge-base-zh-v1.5-fp32`，默认 CLS、L2 归一化；换 pooling 必须重建索引。
- API 等价入口：`POST /api/rules/admin/rebuild-index/{game}`、`POST /api/rules/admin/rebuild-all`。

## 4. 检索评测

```bash
python3 tools/indexing/eval_retrieval.py --gold tools/qa/retrieval_gold.jsonl --api http://localhost:5000
python3 tools/indexing/eval_retrieval.py --gold tools/qa/retrieval_regressions.jsonl --api http://localhost:5000
python3 tools/indexing/eval_query_intent.py --api http://localhost:5000 --out $BOARD_RUNTIME_ROOT/tmp/query-intent-after
python3 tools/indexing/eval_natural_retrieval.py --api http://localhost:5000 --out $BOARD_RUNTIME_ROOT/tmp/live-natural
python3 tools/indexing/retrieval_lab.py --out $BOARD_RUNTIME_ROOT/tmp/retrieval-lab --reranker $BOARD_RUNTIME_ROOT/models/bge-reranker-base
```

- Gold：`retrieval_gold.jsonl` 85 条、`retrieval_regressions.jsonl` 7 条、`retrieval_questions.jsonl` 53 条自然问句、`query_intent_regressions.jsonl` 13 行 / 14 个子查询；事实答案要求见 `rule_fact_regressions.jsonl`。
- 当前指标：85 条中 77 直接命中、8 条候选前三、0 wrong/no_match；7 条回归全部前三。自然问句正例首位/前三和 reranker 结论只见 `docs/reviews/retrieval-*-2026-10-09.md`。
- `retrieval_lab.py` 只读，不自动下载模型，reranker/片段/RRF 未接正式查询。

## 4.0 程序控制编排回归

```bash
# 后端单测（program + legacy 对照）
dotnet test backend/BoardAI.Api.Tests/BoardAI.Api.Tests.csproj --nologo

# Android 证据解析（Windows）
cd clients/android
gradlew.bat testDebugUnitTest --no-daemon --console=plain
```

`LLM:OrchestrationMode=program` 是显式实验路径（生产默认仍为 `legacy`）：Understand → 程序取正式证据 → Respond；普通问题 2 次模型调用，need_more 最多补充一次，协议修复与补充共享 4 次总调用上限。查询轨迹在 `/api/chat` 的 `orchestration` 字段，证据读取/引用计数在 `evidence` 中。

程序控制对比可复用 `tools/eval/`：先生成回归证据、合并 `tools/qa/program_orchestration_holdout.jsonl`，再跑 `compare_versions.py`、两个 blind judge、`combine_judges.py` 和 `analyze_compare.py`；legacy 基线模型调用数可用 `analyze_model_calls.py` 从服务日志解析。完整命令见 `tools/eval/README.md`。

## 4.1 新旧版本全面对比

```bash
python3 tools/eval/compare_versions.py --questions tools/qa/comprehensive_eval_questions.jsonl   --old-api http://172.17.208.1:5001 --new-api http://172.17.208.1:5002   --out $BOARD_RUNTIME_ROOT/tmp/comprehensive-eval/main --repeats 3
python3 tools/eval/build_question_ground_truth.py --questions tools/qa/comprehensive_eval_questions.jsonl   --api http://172.17.208.1:5002 --out $BOARD_RUNTIME_ROOT/tmp/comprehensive-eval/question_ground_truth.json
python3 tools/eval/fetch_rule_details.py --runs $BOARD_RUNTIME_ROOT/tmp/comprehensive-eval/main/runs.jsonl   --api http://172.17.208.1:5002 --out $BOARD_RUNTIME_ROOT/tmp/comprehensive-eval/main/rule_details.json
# judge 在具备 DeepSeek key 的环境运行，见 tools/eval/README.md
python3 tools/eval/combine_judges.py --judge $BOARD_RUNTIME_ROOT/tmp/comprehensive-eval/main/judge.jsonl   --judge $BOARD_RUNTIME_ROOT/tmp/comprehensive-eval/main/judge-deepseek-chat.jsonl   --out $BOARD_RUNTIME_ROOT/tmp/comprehensive-eval/main/judge-consensus.jsonl   --summary $BOARD_RUNTIME_ROOT/tmp/comprehensive-eval/main/judge-consensus-summary.json
python3 tools/eval/analyze_compare.py --runs $BOARD_RUNTIME_ROOT/tmp/comprehensive-eval/main/runs.jsonl   --judge $BOARD_RUNTIME_ROOT/tmp/comprehensive-eval/main/judge-consensus.jsonl   --out-dir $BOARD_RUNTIME_ROOT/tmp/comprehensive-eval/main/analysis-consensus
```

最近一次报告：`docs/reviews/version-comparison-2026-10-17.md`。完整操作见 `tools/eval/README.md`。

## 5. 语音桥

- ASR 默认由 `BoardAI.Api` 进程内 C# `VolcengineAsrClient` 处理；`tools/voice/asr_once.py` 是 `Voice:AsrMode=python` 回退/自检脚本。问答 TTS 由 `BoardAI.Api` 的 `VolcengineTtsClient` 进程内直连火山；`tools/voice/tts_once.py` 只保留给离线自检和教学动画批量音频。两者分别由 `POST /api/asr/once`、`POST /api/tts` 暴露。
- 火山凭证只写 `$BOARD_RUNTIME_ROOT/config/.env`（运行时目录，不入 Git）；默认 TTS provider `standard`，可切 `seed2`。
- 配置与自检见 `tools/voice/README.md`、`docs/ops/start-services.md`。

## 6. 讲规动画

总控（section 级连续 TTS / runtime / compiled）：

```bash
python3 animation/compile_tutorial.py --game splendor --track quick
python3 animation/compile_tutorial.py --game splendor --track quick --dry-run
python3 animation/compile_tutorial.py --game splendor --track quick --skip-tts
python3 animation/compile_tutorial.py --game splendor --track quick --validate-qa
python3 animation/compile_tutorial.py --game splendor --track quick --validate-qa-all
```

检查：

```bash
python3 animation/material_cli.py validate --game splendor
python3 animation/check_materials.py --game splendor --track quick
python3 animation/material_cli.py impact --game splendor --material splendor.action.seed_gem_supply --check
python3 animation/section_source.py --game splendor --track quick --check
python3 animation/compile_animation_v2.py --game splendor --track quick
python3 animation/compile_animation_v2.py --game splendor --track quick --check
python3 animation/check_anim_v2.py --game splendor --track quick
python3 animation/validate_anim_rules_v2.py --game splendor --track quick
python3 animation/audit_anim_v2.py --game splendor --track quick
python3 animation/check_card_identity_v2.py --game splendor --track quick
python3 animation/check_anim_v2_sample.py --game splendor --track quick
python3 tools/ops/check_unity_scripts.py
```

- 当前 full 81 cue / 12 section / 394 anchors；quick 32 cue / 11 section / 138 anchors；静态检查全绿。
- 源真值：`content/games/splendor/tutorial/{tracks,sections,shared}/`；`build/` 与 `anim/v2/{track}.compiled.json` 按源生成。
- QA：`animation/qa_anim_ask.py --game splendor --track quick --only <qualified_cue>`；问题必须手写，放在 cue 的 `qa` 或 `sections/<id>/qa.json`。
- 一次性迁移脚本已移到 `animation/archive/`。
- `animation/cue_graph_v2.py` 支持 insert/delete/split/merge，但截至当前尚未接入 `compile_tutorial.py` 总控。
- 复核修复报告：`docs/tutorial/animation-reuse-review-response-20261017.md`。
- 写作入口：`docs/tutorial/animation-materials-authoring-guide.md`、`content/games/splendor/tutorial/anim/v2/LLM-ANIMATION-GUIDE.md`、`.../v2/README.md`、`animation/README.md`。

## 7. Content / Catalog / Manifest

- `content/catalog/{game}.json`：当前只有 Splendor，入 Git；显式 `rules_ready` / `tutorial_ready` / `tutorial_tracks`，旧 `tutorial_track` 仅兼容。Splendor 默认 quick。
- `$BOARD_RUNTIME_ROOT/content/manifests/{game}.json` 与 `$BOARD_RUNTIME_ROOT/content/releases/{game}/{version}/`：生成物，不入 Git；version 只由 package 内 `path + sha256` 决定。
- 构建：

```bash
# BOARD_RUNTIME_ROOT 来自本地配置
python3 tools/content/build_content_manifest.py --game splendor
```

builder 只收集 runtime/compiled 及实际引用媒体，排除 QA/文档/脚本/pyc/lrc/动画源；发布通过 `{version}.tmp` 校验后原子 rename，versioned URL 只读 release。

## 8. 工作区同步、Unity 与 ADB

工作区同步：

```bash
./tools/ops/sync_workspaces.sh from-linux
./tools/ops/sync_workspaces.sh from-windows
./tools/ops/sync_workspaces.sh status
```

Unity 批处理采样使用 Windows 编辑器路径；运行时快捷键 `G` 开动画、`B` 轮换关键帧、空格暂停、`←/→` 逐步、`A` 自动播。

### 测试机 ADB

本机没有独立加入 PATH 的 `adb`，使用 Unity 自带 Android SDK 里的 Windows `adb.exe`。绝对路径统一记录在仓库根目录 `config/local.properties`（已 gitignore，模板为 `config/local.properties.example`）：

- Windows 键：`ADB_WIN`
- WSL 键：`ADB_WSL`

WSL 使用示例：

```bash
# 先从 config/local.properties 取得 ADB_WSL
ADB="$ADB_WSL"
"$ADB" devices -l
"$ADB" reverse tcp:5000 tcp:5000
```

- WSL 中的 `.exe` 调用的是 Windows adb server；不要直接运行裸 `adb`（WSL 内通常不存在）。
- 测试机通过 USB 连接，机型为 Redmi K30 Pro；设备序列号以 `"$ADB" devices -l` 为准。
- 常用验证：`"$ADB" shell pidof com.boardai.tutorial.uaal`、`"$ADB" shell run-as com.boardai.tutorial.uaal cat /data/user/0/com.boardai.tutorial.uaal/files/board-content/active.json`。

## 9. 常用入口

- `backend/BoardAI.Api/README.md`
- `clients/android/README.md`
- `clients/unity/docs/`
- `content/games/splendor/tutorial/anim/v2/README.md`
- `content/games/splendor/tutorial/anim/v2/LLM-ANIMATION-GUIDE.md`
- `docs/tutorial/README.md`
- `docs/ops/start-services.md`
- `tools/voice/README.md`

## 10. 环境注意

- WSL 启动 Windows 服务时环境变量不会自动传入，可在 `cmd.exe /c "set KEY=...&& dotnet ..."` 里设置。
- 工作区、Unity、adb 等机器路径从根目录 `config/local.properties` 的 `*_WIN` / `*_WSL` 键读取；不要把实际绝对路径写回 README。
- 模型、音频、视频、PDF、原始扫描、QA 留档、manifest/release 等生成物不进 Git。
