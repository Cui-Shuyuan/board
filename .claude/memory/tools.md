---
name: tools
description: 服务启动、索引、校验、后端测试、语音桥、Android、讲规动画编译与常用脚本入口
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
- 规则 JSON 由 `RulesDocumentStore` 按 `Length + LastWriteTimeUtc` 自动 freshness；改规则文件无需重新启动 API 服务。
- 规则文档变化会通过 `ClearDerivedCaches()` 清名称索引、Plan 类型缓存、Flow 位置缓存、Fact score 缓存。
- 语义检索仍必须重建 Qdrant 索引（见第 3 节）。
- `content/catalog/*.json` 和 `content/manifests/*.json` 更新后也无需重新启动 API 服务，它们在每个请求读取文件。

### Qdrant

```powershell
cd D:\qdrant
qdrant.exe
```

- 版本 1.18.3，Windows 原生二进制。
- gRPC 6334（.NET），HTTP 6333（Python 重建脚本）。
- 数据持久化在 `D:\qdrant\data`；必须在 `D:\qdrant` 目录下启动，否则会在仓库根生成 storage 垃圾目录。

## 2. 规则校验与后端测试

```bash
python tools/content/validate_rules.py                 # 全部文件
python tools/content/validate_rules.py --game splendor # 单游戏
python tools/content/validate_rules.py --errors-only   # 只输出 ERROR，供 hook
python tools/content/normalize_json.py                 # JSON 格式规范化
python tools/ops/check_no_secrets.py                   # 扫描已跟踪文件里的明文密钥
```

当前全库：0 errors / 72 warnings。

后端 xUnit：

```bat
D:\dotnet\dotnet.exe test D:\workspace\board\backend\BoardAI.Api.Tests\BoardAI.Api.Tests.csproj --nologo
```

当前为 75/75 全绿。测试项目：`backend/BoardAI.Api.Tests/BoardAI.Api.Tests.csproj`。

## 3. 向量索引

```bash
python tools/indexing/rebuild_index.py --all
python tools/indexing/rebuild_index.py --game splendor
python tools/indexing/rebuild_index.py --all --full
```

- 默认增量同步，按 `content_hash + model_tag` diff。
- 模型/提取逻辑大改时用 `--full`。
- 模型目录：`backend/BoardAI.Api/ml_models/`（gitignore）。
- 当前模型：`bge-base-zh-v1.5-fp32`，768 维。
- 冷启动顺序：Qdrant → 重建索引 → 启动 API。
- API 侧等价入口：`POST /api/rules/admin/rebuild-index/{game}`、`POST /api/rules/admin/rebuild-all`。

## 4. 检索评测

```bash
python tools/indexing/eval_retrieval.py --gold tools/qa/retrieval_gold.jsonl --api http://localhost:5000
```

- Gold set：`tools/qa/retrieval_gold.jsonl`，当前 85 条，覆盖 9 款游戏。
- 直接调用 `POST /api/rules/games/{game}/execute-plan`，不经过 LLM 回答。
- 指标：resolved_hit / resolved_wrong / candidate_top1 / candidate_top3 / unresolved / no_match。
- 当前基线仍沿用最近记录：82/85 resolved_hit，wrong=0，no_match=0（待复核）。

## 5. 后端语音桥

运行时语音走 Python 短进程桥，Android 不直接接触火山凭证；密钥只在仓库根 `.env`（git-ignored）。

- `tools/voice/asr_once.py`：一句话小模型 ASR，被 `POST /api/asr/once` 调用。
- `tools/voice/tts_once.py`：文本合成 MP3，被 `POST /api/tts` 调用。
- 配置说明：`tools/voice/README.md`、`docs/ops/start-services.md`。

自检：

```bash
# 文本 TTS 自检
python tools/voice/tts_once.py --text "这是一次语音测试" --out-file /tmp/tts-test.mp3
```

```bash
# 后端 TTS 自检
curl -X POST http://localhost:5000/api/tts \
  -H "Content-Type: application/json" \
  -d '{"text":"这是一次语音测试"}' \
  --output /tmp/backend-tts.mp3
```

```bash
# 后端 ASR 自检：准备 16 kHz、16-bit、mono WAV 后
curl -X POST http://localhost:5000/api/asr/once \
  -H "Content-Type: audio/wav" \
  --data-binary @/tmp/test.wav
```

Android 真机端到端验证步骤见 `docs/ops/start-services.md`。

## 6. Android 客户端

构建入口：

- `clients/android/README.md`
- `clients/android/build-uaal.bat`：Unity 导出 `unityLibrary` → Gradle `assembleDebug` 的固定顺序入口。
- `clients/android/gradlew.bat`：原生壳构建入口。

已存在的 7 个 JVM 测试：

```text
clients/android/app/src/test/java/com/boardai/tutorial/uaal/content/ContentStatusTest.kt
clients/android/app/src/test/java/com/boardai/tutorial/uaal/content/ContentUpdaterTest.kt
clients/android/app/src/test/java/com/boardai/tutorial/uaal/home/HomeContentCoordinatorTest.kt
clients/android/app/src/test/java/com/boardai/tutorial/uaal/player/PlayerSessionControllerTest.kt
clients/android/app/src/test/java/com/boardai/tutorial/uaal/player/PlayerTimelineBarTest.kt
clients/android/app/src/test/java/com/boardai/tutorial/uaal/player/UnityLoadQueueTest.kt
clients/android/app/src/test/java/com/boardai/tutorial/uaal/qa/QaVoiceControllerTest.kt
```

Android JVM 测试命令以 `clients/android/app/build.gradle` 为准，通常为：

```bat
cd D:\workspace\board\clients\android
gradlew.bat testDebugUnitTest
```

该命令需要已导出的 `unityLibrary`、`local.properties` 中的 Android SDK 与可用 JDK；文档更新时未重新执行 Android 测试，真机验收范围也待复测。

## 7. 讲规动画

### 总控编译

```bash
python3 animation/compile_tutorial.py --game splendor --track full
python3 animation/compile_tutorial.py --game splendor --track full --dry-run
python3 animation/compile_tutorial.py --game splendor --track full --skip-tts
python3 animation/compile_tutorial.py --game splendor --track full --force-full-tts
python3 animation/compile_tutorial.py --game splendor --track full --validate-qa
python3 animation/compile_tutorial.py --game splendor --track full --validate-qa-all
```

`compile_tutorial.py` 负责增量 TTS / manifest / tts.lrc / runtime / compiled。`--validate-qa` 当前从 `_qa/questions.json` 选出受影响 cue 的手写问题做门禁，`--validate-qa-all` 跑全部手写 QA；`qa_anim_ask.py` 另可直接提取 cue 的 `qa` 字段。

### 编译与检查

```bash
python3 animation/anim_schema_v2.py content/games/splendor/tutorial/anim/v2/full.anim.json
python3 animation/compile_animation_v2.py --game splendor --track full
python3 animation/compile_animation_v2.py --game splendor --track full --check
python3 animation/check_anim_v2.py --game splendor --track full
python3 animation/validate_anim_rules_v2.py --game splendor --track full
python3 animation/audit_anim_v2.py --game splendor --track full
python3 animation/check_card_identity_v2.py --game splendor --track full
python3 -m unittest animation/test_audit_anim_v2.py
python3 tools/ops/check_unity_scripts.py
```

`check_anim_v2.py` 管契约 vs compiled、机位、脏帧与 stage；`validate_anim_rules_v2.py` 管单 cue 事件重放规则；`audit_anim_v2.py` 管跨 cue 实物守恒、`card_market` 补牌和 `point`/`highlight` pointer 解析；`check_card_identity_v2.py` 管 Splendor face-up 发展卡真卡身份唯一性（方案 B）。

### 时间锚点

`time_anchors` 已迁移并 commit（`38971d4`）；源数据保留 anchor，compiled 输出数值 `at`。一次性迁移/重生成：

```bash
python3 animation/migrate_time_anchors_v2.py
```

### Unity 采样对账

```bash
./animation/dump_anim_v2.sh --game splendor --track full
python3 animation/check_anim_v2_sample.py --game splendor --track full
```

### 结构编辑

```bash
python3 animation/cue_graph_v2.py --help
```

`cue_graph_v2.py` 支持 insert / delete / split / merge，只维护 cue 链表、parent/entry 和 children 重接；截至 2026-09-27 尚未接入 `compile_tutorial.py` 总控。

### LLM 写作规范

`content/games/splendor/tutorial/anim/v2/LLM-ANIMATION-GUIDE.md`

## 8. TTS

- `animation/tts_doubao.py`：provider-aware 豆包 TTS。默认 `standard`（标准语音合成小模型 v1）；`--provider seed2` 或 `DOUBAO_TTS_PROVIDER=seed2` 切回旧语音合成 2.0。
- `standard` 输出 MP3，但当前实现没有字级 subtitle / 词级时间戳；`seed2` 路径有 subtitle 事件。
- `content/games/splendor/media/tts/full/tts_manifest.json` 当前记录的 `voice=zh_female_vv_uranus_bigtts`、`resource_id=seed-tts-2.0`，也就是说这份已有 full 资产来自 seed2 路径；新生成的默认路径将是 standard。
- `tools/voice/tts_once.py`：命令行/后端桥用的单次合成入口。
- `animation/requirements-tts.txt`：TTS 依赖。
- `animation/validate_timed_script.py`：LRC-like 口播脚本校验/解析。
- `animation/split_lrc_long_cues.py`：过长 cue 拆分。
- `animation/build_tutorial_runtime.py`：编译运行时 cue 数据。
- `animation/tutorial_script_tool.py`：分层编辑源 split/merge/set-pause。
- `animation/rebuild_tutorial.py`：source → LRC → TTS → runtime 一条命令。

## 9. QA / 回归

- 规则 QA 脚本按游戏散落在 `tools/qa/_qa_<game>_run.py`、`tools/qa/_qa_<game>_log_analysis.py`。
- 结果文件：`tools/qa/_qa_<game>_results.jsonl`。
- 动画 QA：`animation/qa_anim_ask.py` 是唯一流程——**问题必须手写**，可放在 cue 的 `qa` 字段或 `_qa/questions.json`；脚本自动提取、发送 Board API 问答并留档。
- 常用检查：
  - `tools/ops/check_unity_scripts.py`：Unity C# 编译检查。
  - `animation/check_anim_v2.py`：编译/契约/状态/机位检查。
  - `animation/validate_anim_rules_v2.py`：单 cue 事件重放规则。
  - `animation/audit_anim_v2.py`：跨 cue 实物守恒、补牌、pointer 解析。
  - `animation/check_card_identity_v2.py`：Splendor 每个 state 的 face-up 真卡身份唯一性。
  - `animation/check_anim_v2_sample.py`：Unity 采样与 compiled 逐 item 对账。

## 10. Content / Catalog / Manifest

- `content/catalog/{game}.json`：Android 首页游戏目录来源，当前只有 `content/catalog/splendor.json`，且已入 Git。条目显式声明：
  - `rules_ready`：有可问答规则数据；
  - `tutorial_ready`：有完整教程 runtime 包（补 manifest 不等于教程就绪）；
  - `tutorial_tracks`：可播放 track 列表，无教程则为空；
  - `tutorial_track` 仅作为旧客户端/旧缓存兼容字段。
- `content/manifests/{game}.json`：当前 runtime-only package 清单与版本；生成物，不入 Git。
- `content/releases/{game}/{version}/`：不可变发布目录。构建入口 `python3 tools/content/build_content_manifest.py --game splendor` 只收集 `tutorial/{track}.runtime.json`、`tutorial/anim/v2/{track}.compiled.json` 及其实际引用的媒体；排除 `_qa/**`、`checks/**`、`animation/**`、`*.md`、`*.py`、`*.pyc`、`__pycache__/**`、`*.lrc`、`*.tmp`、`*.log`、`*.exitstate.json`、`*.v2sample.json` 等非运行数据。
- version 只由 package 内 `path + sha256` 决定；QA 日志、pyc、文档、动画源变化不改变 version。发布通过 `{version}.tmp` 暂存、size/sha256 校验、原子 rename；manifest 再用 `.tmp + atomic rename` 切换。同 version 已存在但内容不一致时失败，不覆盖；保留当前 + 最近 2 个历史 release。
- 内容更新接口为 `/api/content/games/{game}/manifest` 与 `/api/content/games/{game}/files/...`，无需重新启动 API 服务。versioned URL 从 release 目录读取且缺文件 404；无版本 `/files/...` 才继续从 `content/games` 读并返回 no-cache。

## 11. 工作区同步与 Unity

```bash
./tools/ops/sync_workspaces.sh from-linux    # 数据推到 Windows
./tools/ops/sync_workspaces.sh from-windows  # Windows 拉回
./tools/ops/sync_workspaces.sh status
```

Unity 批处理采样使用 Windows 侧编辑器（项目文档中的路径）：

```text
/mnt/d/Unity/Hub/Editor/6000.5.8f1/Editor/Unity.exe -batchmode -projectPath 'D:\workspace\board\clients\unity' ...
```

运行时快捷键：`G` 开动画，`B` 轮换关键帧，空格暂停，`←/→` 逐步，`A` 自动播。

## 12. 常用入口

- `backend/BoardAI.Api/README.md`
- `clients/android/README.md`
- `clients/unity/docs/`
- `content/catalog/`
- `content/manifests/`
- `content/games/splendor/tutorial/anim/v2/README.md`
- `content/games/splendor/tutorial/anim/v2/LLM-ANIMATION-GUIDE.md`
- `docs/ops/start-services.md`
- `docs/tutorial/README.md`
- `tools/voice/README.md`

## 13. 环境注意

- WSL 启动 Windows 服务时环境变量不会自动传入，需在 `cmd.exe /c "set KEY=...&& dotnet ..."` 里设置。
- Windows 路径为 `D:\workspace\board`；WSL/当前工作区路径以实际为准。
- 模型、音频、视频、PDF、原始扫描、QA 生成物不进 Git。
