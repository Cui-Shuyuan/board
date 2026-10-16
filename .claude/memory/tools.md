---
name: tools
description: 服务启动、校验、索引、测试、语音、Android、动画编译与常用脚本入口
metadata:
  type: project
---

# 工具与命令

## 1. 本地服务

### BoardAI.Api

```bash
cd backend/BoardAI.Api
dotnet run --urls "http://0.0.0.0:5000"
```

- API key 只走环境变量：推荐 `LLM__ApiKey`，兼容 `DEEPSEEK_API_KEY`。
- 规则 JSON 由 `RulesDocumentStore` 按 `Length + LastWriteTimeUtc` 自动 freshness；改规则文件无需重启。
- 语义检索仍需重建 Qdrant 索引；`content/catalog/*.json`、`content/manifests/*.json` 每次请求读取，无需重启。

### Qdrant

```powershell
cd D:\qdrant
qdrant.exe
```

- 版本 1.18.3，Windows 原生；HTTP 6333（Python），gRPC 6334（.NET）。
- 必须在 Qdrant 自身目录启动，避免在仓库根生成 storage。

## 2. 规则校验与测试

```bash
python3 tools/content/validate_rules.py --errors-only   # 当前 0 errors / 72 warnings
python3 tools/content/normalize_json.py
python3 tools/ops/check_no_secrets.py
python3 -m unittest discover -s animation -p 'test_*.py'   # 当前 188/188
```

后端：

```bash
dotnet test backend/BoardAI.Api.Tests/BoardAI.Api.Tests.csproj --nologo   # 当前 161/161
```

Android JVM：测试源码 12 个文件 / 93 个 `@Test`（ContentStatus、ContentStoreCleanup、ContentUpdater、HomeContentCoordinator、PlayerSessionController、PlayerTimelineBar、UnityLoadQueue、QaEvidenceParsing、QaPanelMode、QaRepository、QaSessionHolder、QaVoiceController）。最近一次记录 91/91；WSL 侧因 Android SDK/AAPT 在 Windows 侧，未直接重跑，重跑用 Windows `gradlew.bat testDebugUnitTest`。

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
- 模型目录 `backend/BoardAI.Api/ml_models/`（gitignore）；当前 `bge-base-zh-v1.5-fp32`，默认 CLS、L2 归一化；换 pooling 必须重建索引。
- API 等价入口：`POST /api/rules/admin/rebuild-index/{game}`、`POST /api/rules/admin/rebuild-all`。

## 4. 检索评测

```bash
python3 tools/indexing/eval_retrieval.py --gold tools/qa/retrieval_gold.jsonl --api http://localhost:5000
python3 tools/indexing/eval_retrieval.py --gold tools/qa/retrieval_regressions.jsonl --api http://localhost:5000
python3 tools/indexing/eval_natural_retrieval.py --api http://localhost:5000 --out .tmp/live-natural
python3 tools/indexing/retrieval_lab.py --out .tmp/retrieval-lab --reranker backend/BoardAI.Api/ml_models/bge-reranker-base
```

- Gold：`retrieval_gold.jsonl` 85 条、`retrieval_regressions.jsonl` 7 条、`retrieval_questions.jsonl` 53 条自然问句；事实答案要求见 `rule_fact_regressions.jsonl`。
- 当前指标：85 条中 77 直接命中、8 条候选前三、0 wrong/no_match；7 条回归全部前三。自然问句正例首位/前三和 reranker 结论只见 `docs/reviews/retrieval-*-2026-10-09.md`。
- `retrieval_lab.py` 只读，不自动下载模型，reranker/片段/RRF 未接正式查询。

## 5. 语音桥

- ASR 默认由 `BoardAI.Api` 进程内 C# `VolcengineAsrClient` 处理；`tools/voice/asr_once.py` 是 `Voice:AsrMode=python` 回退/自检脚本。TTS 仍走 `tools/voice/tts_once.py`；两者由 `POST /api/asr/once`、`POST /api/tts` 暴露。
- 火山凭证只写根目录 `.env`（git-ignored）；默认 TTS provider `standard`，可切 `seed2`。
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

- 当前 full 67 cue / 12 section / 338 anchors；quick 24 cue / 11 section / 106 anchors；静态检查全绿。
- 源真值：`content/games/splendor/tutorial/{tracks,sections,shared}/`；`build/` 与 `anim/v2/{track}.compiled.json` 按源生成。
- QA：`animation/qa_anim_ask.py --game splendor --track quick --only <qualified_cue>`；问题必须手写，放在 cue 的 `qa` 或 `sections/<id>/qa.json`。
- 一次性迁移脚本已移到 `animation/archive/`。
- `animation/cue_graph_v2.py` 支持 insert/delete/split/merge，但截至当前尚未接入 `compile_tutorial.py` 总控。
- 写作入口：`content/games/splendor/tutorial/anim/v2/LLM-ANIMATION-GUIDE.md`、`.../v2/README.md`、`animation/README.md`。

## 7. Content / Catalog / Manifest

- `content/catalog/{game}.json`：当前只有 Splendor，入 Git；显式 `rules_ready` / `tutorial_ready` / `tutorial_tracks`，旧 `tutorial_track` 仅兼容。Splendor 默认 quick。
- `content/manifests/{game}.json` 与 `content/releases/{game}/{version}/`：生成物，不入 Git；version 只由 package 内 `path + sha256` 决定。
- 构建：

```bash
python3 tools/content/build_content_manifest.py --game splendor
```

builder 只收集 runtime/compiled 及实际引用媒体，排除 QA/文档/脚本/pyc/lrc/动画源；发布通过 `{version}.tmp` 校验后原子 rename，versioned URL 只读 release。

## 8. 工作区同步与 Unity

```bash
./tools/ops/sync_workspaces.sh from-linux
./tools/ops/sync_workspaces.sh from-windows
./tools/ops/sync_workspaces.sh status
```

Unity 批处理采样使用 Windows 编辑器路径；运行时快捷键 `G` 开动画、`B` 轮换关键帧、空格暂停、`←/→` 逐步、`A` 自动播。

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
- Windows 路径示例 `D:\workspace\board`；WSL 路径 `/mnt/d/workspace/board`，以实际工作区为准。
- 模型、音频、视频、PDF、原始扫描、QA 留档、manifest/release 等生成物不进 Git。
