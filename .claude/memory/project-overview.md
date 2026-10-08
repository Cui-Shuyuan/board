---
name: project-overview
description: Board AI 项目定位、功能、阶段与技术选型（2026-10-07 当前版）
metadata:
  type: project
---

# Board AI — 桌游规则操作系统

## 定位

不是聊天机器人，而是规则驱动的 AI Runtime / 桌游规则操作系统：

> LLM 负责理解语言与组织表达，程序负责规则查询、判定、流程和动画。

场景：桌游店内每桌一台平板，客人选游戏后由 AI 讲师按讲规动画讲解；客人可随时按住说话打断提问；关闭问答后，如果提问前动画正在播放，会精确恢复到进入问答时的 cue 内位置。

## 对外功能

1. **规则问答**
   - 自然语言提问，程序查结构化规则，LLM 组织成 TTS 友好的回答。
   - 支持概念解释、行动条件、流程顺序、边界上限、外观识别等 relation。
   - 不追踪实时棋盘状态；优先解释判断方法，让玩家对照实物检查，核验具体局面时再按需询问必要信息。

2. **讲规动画**
   - Unity 原生安卓播放器，静态音频 + 数据驱动 2D/2.5D sprite 动画。
   - 口播稿主导，TTS 冻结后再编译动画时间轴。
   - 支持播放、字幕、分段跳转、打断问答后精确恢复当前 cue 内位置。

3. **Android 客户端 / 内容更新**
   - UaaL 原生壳 + Kotlin + Jetpack Compose 控制层。
   - 首页游戏目录、搜索、历史、资源管理；manifest 驱动的本地内容仓库与增量下载 v1。
   - 教程播放器 Compose 控制层、Unity 状态回传、问答面板与 PTT 语音链路已接线，完整真机验收待复测。

4. **流程向导 Flow Guide（下一步，尚未实现）**
   - 程序维护流程游标，条件真交由玩家回答。
   - 不获取实时棋盘状态，不做 CV。
   - 首个目标：Civolution 顶层时代/阶段循环与终局计分助手。

## 核心原则

- **程序确定性优先**：规则查询、校验、判定、编译、播放全部由程序完成。
- **数据驱动**：每款游戏一个 `content/games/{game}/` 目录，新增游戏尽量不改代码。
- **规则结构化**：用 `concepts.json` / `flow.json` 表达，不以 Markdown 规则书作为运行时来源。
- **Agent 时代边界**：代码实现可以快速生成，契约、数据、校验、评测必须清晰。
- **LLM 只做语言层**：不依赖 LLM 记忆规则；所有事实来自程序返回。

## 技术选型

- 后端：C# / .NET 9 Web API（`backend/BoardAI.Api`），默认 `http://localhost:5000`。
- 客户端：Android UaaL 原生壳（`clients/android/`）+ Unity as a Library（`clients/unity/`），URP，2.5D sprite。
- LLM：`ILLMService` 抽象，当前 DeepSeek 兼容接口；system prompt 配置在 `appsettings.json`。
- 检索：Qdrant 独立进程 + `bge-base-zh-v1.5` fp32 ONNX。
- TTS：默认火山豆包标准语音合成小模型 v1（`standard`）；旧语音合成 2.0 通过 `--provider seed2` / `DOUBAO_TTS_PROVIDER=seed2` 兼容。
- 语音：后端 Python 短进程桥（`tools/voice/asr_once.py` / `tts_once.py`），Android 不直接接触火山凭证。
- 部署：Windows 本地主机 + 店内内网；模型、音频、视频、PDF、原始照片、内容 manifest 生成物不进 Git。

## 开发阶段

1. **世界模型 / 本体**：已完成，持续小步扩展。当前 `content/ontology/concepts.json` 有 89 个概念。
2. **Rule DSL**：已完成 Splendor 首版，后续游戏沿用。
3. **Runtime / 意图接口**：已完成；单工具 `execute_plan` + 三层回答 + 向量检索。后端 `GameRulesService` 已完成服务化拆分，121 条 xUnit 全绿；全文/name 索引与 Python CLI 共用身份路径和版本契约，九款真实规则数据回归对齐。
4. **扩游戏与元信息**：进行中；已有 9 款游戏规则数据。catalog 与 manifest v1 仅 Splendor 落地：`content/catalog/splendor.json` 已入 Git，`content/manifests/splendor.json` 为生成物。Android 首页读取 `/api/catalog/games`，内容更新走 manifest。
5. **Tutorial Tree / 讲规动画**：当前重点之一；Splendor full 83 cue（cue id 已语义化）已跑通，终局与真卡身份收口，time_anchors 与 QA 同置已落地；quick 未做，正式视觉验收待定。
6. **Controller / 语音问答**：已有首版能力；后端有一句话 ASR + TTS、Android PTT、回答音频自动播放/重播/继续播放。继续播放会先停回答音频，再按进入问答时记录的 cueId + 位置精确恢复；端到端真机验收仍待复测。
7. **UI / 客户端**：已有首版客户端；UaaL 原生壳、Compose 控制、首页、资源管理、问答面板已落地。店内平板规模验收仍未完成。

## 当前重心（2026-10-07）

- 近期执行：Splendor 终局计分、真卡身份与合并 cue TTS 已收口；当前转向 full 逐 cue 重审、Android/语音端到端验收。
- 后续排列：动画逐 cue 重审与 Quick → Flow Guide；内容 catalog/manifest 扩展和 Android 真机验收可并行。

## 数据流总览

```text
客人语音/文字
  → LLM(AI 接入层) 生成 execute_plan
  → GameRulesService facade → RulesPlanService / 检索 / 规则服务
  → 结构化事实返回
  → LLM 组织成口语回答
  → TTS 播放

讲规动画离线流程：
  script.full.json（口播） + full.anim.json（动画源）
  → compile_tutorial.py（TTS / runtime / compiled）
  → Unity 播放 compiled
```

## 关键目录

- `content/ontology/`：通用本体与 trigger pipeline。
- `content/games/`：各游戏规则数据、素材、教程、动画。
- `content/catalog/`：Android 首页游戏目录（当前只有 splendor）。
- `content/manifests/`：内容同步清单生成物（当前只有 splendor，不入 Git）。
- `backend/BoardAI.Api/`：Runtime 服务。
- `clients/android/`：UaaL 原生 Android 壳与 Compose 控制层。
- `clients/unity/`：Unity 客户端 / Unity as a Library 导出侧。
- `animation/`：动画生产链工具（schema、编译、TTS、动画 QA）。
- `tools/`：跨游戏/跨项目的开发与运维工具（content / indexing / media / qa / ops / voice）。
- `tools/qa/`：检索 gold set 等评测数据。
- `.claude/memory/`：新会话必读的当前记忆。
- `.claude/archive/`：历史日志与旧版长文档，默认不读。
