# BoardAI — 桌游规则操作系统

BoardAI 不是聊天机器人，而是面向桌游店的**规则驱动 AI Runtime + 讲规动画平台**：

- 客人选游戏后，由 AI 讲师播放讲规动画；
- 客人可随时按住说话打断提问；
- 规则查询、判定、流程、动画编译全部由程序完成，LLM 只负责理解语言与组织回答。

```text
LLM 负责语言层，程序负责事实、规则和确定性播放。
```

## 目录

- [核心能力](#核心能力)
- [架构总览](#架构总览)
- [目录结构](#目录结构)
- [快速开始](#快速开始)
- [讲规动画](#讲规动画)
- [Android / Unity 客户端](#android--unity-客户端)
- [校验与测试](#校验与测试)
- [开发约定](#开发约定)
- [当前状态与待办](#当前状态与待办)
- [文档索引](#文档索引)

## 核心能力

1. **规则问答**
   - 自然语言提问 → LLM 生成 `execute_plan` 查询计划 → 程序查询结构化规则 → LLM 组织回答。
   - 支持概念解释、行动条件、流程顺序、边界限制、外观识别等 relation。
   - Runtime 不追踪实时棋盘状态；需要状态的问题由 LLM 向客人反问。

2. **结构化规则数据**
   - `content/ontology/` 通用本体与 trigger pipeline。
   - `content/games/{game}/concepts.json + flow.json` 游戏规则层。
   - 当前已有 9 款游戏规则数据；Splendor 是 Runtime 与动画首个试点。

3. **内容分发**
   - catalog + runtime-only manifest 驱动 Android 首页、内容下载、增量更新、断点续传。
   - `tools/content/build_content_manifest.py` 只按 Unity/Android 运行依赖白名单打包，不包含 QA 日志、动画源、文档、脚本、`.pyc`、`.lrc` 等非运行文件；version 只由 package 内 `path + sha256` 决定。
   - manifest 指向 `content/releases/{game}/{version}/` 不可变发布目录；已发布的同 version URL 永不改变字节。
   - `content/manifests/*.json` 与 `content/releases/` 均为生成物、**不入 Git**；`tools/ops/sync_workspaces.sh` 只同步 Git 提交。生产 API 主机必须在本机用 `python3 tools/content/build_content_manifest.py --game <game>` 生成这两个目录，或单独部署已校验的发布目录；只同步代码不能发布新内容。
   - 后端只读提供 `/api/catalog/games`、`/api/content/games/{game}/manifest` 与版本化文件接口。
   - catalog 显式声明 `rules_ready` / `tutorial_ready` / `tutorial_tracks`；规则-only 游戏不提供教程下载/播放入口，但可从首页直接进入规则问答。

4. **讲规动画**
   - 口播稿主导，TTS 冻结后再编译动画时间轴。
   - 静态资源 + 数据驱动 2D/2.5D sprite 动画，Unity as a Library（UaaL）。
   - 支持播放、字幕、分段跳转、问答打断后精确恢复到进入问答时的 cue 内位置。

5. **语音链路**
   - 后端 Python 短进程桥接火山引擎 ASR / TTS，Android 不接触服务密钥。
   - Android 端已有 PTT、识别文本回填、回答 TTS、重播/继续播放；继续播放会先停回答音频，再按记录的 cueId + 位置精确恢复。完整真机验收待复测。

6. **Flow Guide（下一步）**
   - 程序维护流程游标，条件判断交玩家回答；不做 CV，不获取实时棋盘状态。
   - 首个目标：Civolution 顶层时代/阶段循环与终局计分助手。

## 架构总览

```text
L0 本体层      content/ontology/concepts.json + flow.json
L1 游戏规则层  content/games/{game}/concepts.json + flow.json + instances.json
L2 Runtime     backend/BoardAI.Api（Chat + Rules + Catalog + Content + ASR/TTS）
L3 动画源      script.full.json + full.anim.json + _stage/*.stage.json
L4 编译产物    full.runtime.json + full.compiled.json + TTS 音频 + content manifests
L5 客户端      Android UaaL 原生壳（Kotlin + Compose）+ Unity as a Library
```

### 规则问答数据流

```text
客人语音/文字
  → LLM 生成 execute_plan
  → GameRulesService facade
  → RulesPlanService / 检索 / 规则服务
  → 结构化事实返回
  → LLM 组织为 TTS 友好回答
  → TTS 播放
```

### 讲规动画数据流

```text
script.full.json（口播文字）
full.anim.json（动画事件 / 状态 / camera）
_stage/*.stage.json（zone / template / 机位）
  → compile_tutorial.py（TTS + runtime + compiled）
  → Unity 播放 full.compiled.json
```

## 目录结构

```text
backend/
  BoardAI.Api/            .NET 9 Web API（Chat、Rules、Catalog、Content、ASR/TTS）
  BoardAI.Api.Tests/      xUnit 测试
clients/
  android/                UaaL 原生 Android 壳 + Kotlin + Jetpack Compose
  unity/                  Unity 工程 / Unity as a Library 导出侧
content/
  ontology/               通用本体与 trigger pipeline
  games/{game}/           每款游戏规则、流程、素材、教程、动画
  catalog/{game}.json     Android 首页游戏目录（当前仅 Splendor）
  manifests/{game}.json   内容同步生成物，不入 Git
  releases/{game}/{ver}/  runtime-only 不可变内容发布目录，不入 Git
animation/                动画生产链工具（schema / 编译 / time anchors / TTS / QA）
tools/
  content/                规则校验、内容 manifest 生成
  indexing/               Qdrant 索引重建与检索评测
  media/                  素材处理
  ops/                    环境与脚本检查
  qa/                     QA / 检索 gold set
  voice/                  ASR / TTS Python 桥
docs/                     运维与专题文档
doc/                      规则书、研究材料、原始扫描（多数不进 Git）
assets/                  图标与通用资源
protocol/                协议资料
.claude/memory/           新会话必读的项目记忆与当前状态
.claude/archive/          历史归档，默认不读
```

## 快速开始

### 1. 环境依赖

| 组件 | 用途 | 备注 |
|---|---|---|
| .NET 9 SDK | 后端 Runtime | `backend/BoardAI.Api` |
| Python 3.10+ | 规则校验 / 索引 / 动画工具 / 语音桥 | 建议 3.12 |
| Qdrant 1.18.x | 向量检索 | 本地独立进程，默认 gRPC `6334` |
| DeepSeek / LLM API Key | 规则问答语言层 | 只走环境变量，不进仓库 |
| 火山引擎语音凭证 | ASR / TTS | 只写在根目录 `.env`（git-ignored） |
| Unity 6000.5.x + Android SDK/NDK | 构建 UaaL 客户端 | 仅客户端开发需要 |

### 2. 启动 Qdrant

```bash
# Windows 示例
cd D:\qdrant
qdrant.exe
```

### 3. 重建规则索引

```bash
cd <repo-root>
python3 tools/indexing/rebuild_index.py --game splendor
# 全部游戏
python3 tools/indexing/rebuild_index.py --all
# 模型或提取逻辑大改时
python3 tools/indexing/rebuild_index.py --all --full
```

### 4. 启动后端

```bash
cd backend/BoardAI.Api
dotnet run --urls "http://0.0.0.0:5000"
```

API Key 只走环境变量，例如：

```bash
export LLM__ApiKey="sk-..."
# 或 Windows:
set LLM__ApiKey=sk-...
```

验证：

```bash
curl -s http://localhost:5000/api/rules/games/splendor/types
curl -s http://localhost:5000/api/rules/games/splendor/concepts?type=actions
curl -X POST http://localhost:5000/api/chat \
  -H "Content-Type: application/json" \
  -d '{"game_id":"splendor","messages":[{"role":"user","content":"贵族怎么获得？"}]}'
```

详细说明见 [backend/BoardAI.Api/README.md](backend/BoardAI.Api/README.md) 与 [docs/ops/start-services.md](docs/ops/start-services.md)。

### 5. 规则校验与后端测试

```bash
python3 tools/content/validate_rules.py --errors-only
python3 tools/ops/check_no_secrets.py

dotnet test backend/BoardAI.Api.Tests/BoardAI.Api.Tests.csproj --nologo
```

### 6. 语音自检

```bash
pip install -r animation/requirements-tts.txt

python3 tools/voice/tts_once.py \
  --text "这是一次语音测试" \
  --out-file /tmp/tts-test.mp3
```

配置说明见 [tools/voice/README.md](tools/voice/README.md)。

## 讲规动画

当前试点：**Splendor full**，75 cue；口播、TTS / runtime / compiled / Unity 播放器 / 采样对账链已跑通，发展卡真卡身份与终局 cue 已收口。

### 关键文件

```text
content/games/splendor/tutorial/script.full.json              口播脚本
content/games/splendor/tutorial/anim/v2/full.anim.json        动画源
content/games/splendor/tutorial/anim/v2/_stage/*.stage.json   zone / template / shot
content/games/splendor/tutorial/full.runtime.json             编译产物：音频与字幕时间轴
content/games/splendor/tutorial/anim/v2/full.compiled.json    编译产物：Unity 只读
```

### 常用命令

```bash
# 总控：文字差量 → TTS → runtime → compiled
python3 animation/compile_tutorial.py --game splendor --track full
python3 animation/compile_tutorial.py --game splendor --track full --skip-tts

# 编译与检查
python3 animation/compile_animation_v2.py --game splendor --track full --check
python3 animation/check_anim_v2.py --game splendor --track full
python3 animation/validate_anim_rules_v2.py --game splendor --track full
python3 animation/audit_anim_v2.py --game splendor --track full

# Unity 采样对账
./animation/dump_anim_v2.sh --game splendor --track full
python3 animation/check_anim_v2_sample.py --game splendor --track full
```

### 写作入口

- 动画编写指南：[content/games/splendor/tutorial/anim/v2/LLM-ANIMATION-GUIDE.md](content/games/splendor/tutorial/anim/v2/LLM-ANIMATION-GUIDE.md)
- 数据模型详细说明：[content/games/splendor/tutorial/anim/v2/README.md](content/games/splendor/tutorial/anim/v2/README.md)
- 动画工具链概览：[animation/README.md](animation/README.md)

## Android / Unity 客户端

Android 是 UaaL 原生壳（Kotlin + Jetpack Compose），Unity 作为底层渲染区域；Compose 控制层负责首页、资源管理、播放器控件、章节与问答面板。

### 构建

```bat
cd D:\workspace\board\clients\android
build-uaal.bat
```

只重编原生壳、不重新导出 Unity：

```bat
gradlew.bat assembleDebug
```

构建说明与 `local.properties` / `adb reverse` / APK 路径见 [clients/android/README.md](clients/android/README.md)。

### 真机开发常用

```bat
adb reverse tcp:5000 tcp:5000
adb install -r clients\android\app\build\outputs\apk\debug\app-debug.apk
```

Android 端 API 地址通过 `local.properties` 的 `board.api.baseUrl` 注入；默认 `http://127.0.0.1:5000`，配合 `adb reverse` 使用。

## 校验与测试

| 层级 | 命令 | 说明 |
|---|---|---|
| 规则 JSON | `python3 tools/content/validate_rules.py --errors-only` | 9 款游戏规则数据校验 |
| 密钥扫描 | `python3 tools/ops/check_no_secrets.py` | 防止明文密钥入库 |
| 后端 | `dotnet test backend/BoardAI.Api.Tests/BoardAI.Api.Tests.csproj --nologo` | 当前 121/121 |
| 动画契约 | `python3 animation/check_anim_v2.py --game splendor --track full` | 契约 vs compiled |
| 动画规则 | `python3 animation/validate_anim_rules_v2.py --game splendor --track full` | 单 cue 事件重放 |
| 动画审计 | `python3 animation/audit_anim_v2.py --game splendor --track full` | 跨 cue 守恒 / 补牌 / pointer |
| 卡身份 | `python3 animation/check_card_identity_v2.py --game splendor --track full` | 同一 state 无重复真卡 |
| Unity 采样 | `python3 animation/check_anim_v2_sample.py --game splendor --track full` | 75 cue 状态对账 |
| 单元测试 | `python3 -m unittest animation/test_compile_animation_v2.py animation/test_audit_anim_v2.py` | 动画工具测试 |

## 开发约定

- 新会话先读 [CLAUDE.md](CLAUDE.md) 与 [.claude/memory/MEMORY.md](.claude/memory/MEMORY.md)。
- **程序确定性优先**：规则查询、校验、判定、编译、播放由程序完成；LLM 只做语言层。
- **JSON 是运行前提**：写完规则文件必须过 `validate_rules.py`。
- **动画是手写静态资产**：按“文字脚本 → 手写 QA → 原语 events → 编译 → 检查 → Unity 采样对账”的顺序改。
- **QA 必须手写并随 cue 同步**：逐条写问题，不能让过期问题重复问新动作。
- **隐藏不是账**：`offstage` / 不可见不能用来掩盖非法棋盘状态；实物总数仍必须守恒。
- **不要提交生成物 / 密钥 / 大文件**：模型、音频、PDF、原始扫描、内容 manifest、APK、Unity Library、采样产物均不入 Git。
- 每个可验收节点做一次 commit。

## 当前状态与待办

当前进度、已知缺口、推荐下一步以记忆文件为准：

- [.claude/memory/current-state.md](.claude/memory/current-state.md)
- [.claude/memory/game-status.md](.claude/memory/game-status.md)
- [.claude/memory/tutorial-animation.md](.claude/memory/tutorial-animation.md)

简要状态：

- 后端服务化拆分完成，xUnit 121/121；索引契约已对九款真实规则数据做 CLI/API 版本 hash 回归。
- 9 款游戏有规则数据，8 款有 `flow.json`；Splendor 是 Runtime + 动画试点。
- Splendor full 75 cue；QA、time_anchors、compiled、Unity 采样链可运行，终局与真卡身份已收口。
- Android UaaL / 内容更新 / 播放器 / 问答语音首版已落地；完整真机端到端验收待复测。
- Splendor 发展卡身份保真已完成扫描/模板/state 检查；下一步重新过 full 75 cue，逐 cue 看真卡画面。
- 其余 8 款游戏 catalog / manifest 待补。
- Flow Guide 尚未开始；动画 Quick 版尚未开始。

## 文档索引

| 文档 | 内容 |
|---|---|
| [CLAUDE.md](CLAUDE.md) | 顶层协作约定与关键文件入口 |
| [.claude/memory/MEMORY.md](.claude/memory/MEMORY.md) | 项目记忆索引，新会话第一步 |
| [.claude/memory/current-state.md](.claude/memory/current-state.md) | 当前进度、HEAD、待办与不建议事项 |
| [.claude/memory/architecture.md](.claude/memory/architecture.md) | 本体 / Runtime / 检索 / 客户端架构 |
| [.claude/memory/tutorial-animation.md](.claude/memory/tutorial-animation.md) | 讲规动画 v3 数据模型、文件与生产流程 |
| [.claude/memory/conventions.md](.claude/memory/conventions.md) | JSON / 本体 / pipeline / QA 规范 |
| [.claude/memory/tools.md](.claude/memory/tools.md) | 服务、命令、测试、脚本入口 |
| [backend/BoardAI.Api/README.md](backend/BoardAI.Api/README.md) | 后端接口、配置与调用示例 |
| [clients/android/README.md](clients/android/README.md) | UaaL 构建、内容更新、资源管理 |
| [animation/README.md](animation/README.md) | 动画工具链与 checker 分工 |
| [content/games/splendor/tutorial/anim/v2/README.md](content/games/splendor/tutorial/anim/v2/README.md) | 动画数据模型细节 |
| [content/games/splendor/tutorial/anim/v2/LLM-ANIMATION-GUIDE.md](content/games/splendor/tutorial/anim/v2/LLM-ANIMATION-GUIDE.md) | 动画编写指南 |
| [docs/ops/start-services.md](docs/ops/start-services.md) | Qdrant / API / 语音服务启动 |
| [tools/voice/README.md](tools/voice/README.md) | ASR / TTS 配置与自检 |
