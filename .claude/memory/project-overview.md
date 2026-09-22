---
name: project-overview
description: Board AI 项目定位、功能、阶段与技术选型（当前版）
metadata:
  type: project
---

# Board AI — 桌游规则操作系统

## 定位

不是聊天机器人，而是规则驱动的 AI Runtime / 桌游规则操作系统：

> LLM 负责理解语言与组织表达，程序负责规则查询、判定、流程和动画。

场景：桌游店内每桌一台平板，客人选游戏后由 AI 讲师按讲规动画讲解；客人可随时按住说话打断提问，回答后从当前小节重播。

## 对外功能

1. **规则问答**
   - 自然语言提问，程序查结构化规则，LLM 组织成 TTS 友好的回答。
   - 支持概念解释、行动条件、流程顺序、边界上限、外观识别等 relation。
   - 不追踪实时棋盘状态；需要状态的问题由 LLM 反问客人。

2. **讲规动画**
   - Unity 原生安卓播放器，静态音频 + 数据驱动 2D/2.5D sprite 动画。
   - 口播稿主导，TTS 冻结后再编译动画时间轴。
   - 支持播放、字幕、分段跳转、打断问答后重播当前 cue。

3. **流程向导 Flow Guide（下一步，尚未实现）**
   - 程序维护流程游标，条件真交由玩家回答。
   - 不获取实时棋盘状态，不做 CV。
   - 首个目标：Civolution 顶层时代/阶段循环与终局计分助手。

## 核心原则

- **程序确定性优先**：规则查询、校验、判定、编译、播放全部由程序完成。
- **数据驱动**：每款游戏一个 `games/{game}/` 目录，新增游戏尽量不改代码。
- **规则结构化**：用 `concepts.json` / `flow.json` 表达，不以 Markdown 规则书作为运行时来源。
- **Agent 时代边界**：代码实现可以快速生成，契约、数据、校验、评测必须清晰。
- **LLM 只做语言层**：不依赖 LLM 记忆规则；所有事实来自程序返回。

## 技术选型

- 后端：C# / .NET 9 Web API（`backend/BoardAI.Api`），默认 `http://localhost:5000`。
- 前端：Unity 6 原生安卓（`client/`），URP，2.5D sprite。
- LLM：`ILLMService` 抽象，当前 DeepSeek 兼容接口；system prompt 配置在 `appsettings.json`。
- 检索：Qdrant 独立进程 + `bge-base-zh-v1.5` fp32 ONNX。
- TTS：火山豆包语音合成 2.0（`scripts/tts_doubao.py`）。
- 部署：Windows 本地主机 + 店内内网；模型、音频、视频、PDF、原始照片不进 Git。

## 开发阶段

1. **世界模型 / 本体**：已完成，持续小步扩展。当前 `ontology/concepts.json` 有 89 个概念。
2. **Rule DSL**：已完成 Splendor 首版，后续游戏沿用。
3. **Runtime / 意图接口**：已完成；单工具 `execute_plan` + 三层回答 + 向量检索。
4. **扩游戏与游戏元信息**：进行中；已有 9 款游戏数据，`manifest.json` 待补。
5. **Tutorial Tree / 讲规动画**：当前重点；Splendor full 已跑通编译链，quick 未做。
6. **Controller**：未开始；负责 STT → LLM → Rule Engine → TTS 状态机与 PTT。
7. **UI**：未开始；最终交互入口以安卓平板为主。

## 数据流总览

```text
客人语音/文字
  → LLM(AI 接入层) 生成 execute_plan
  → GameRulesService 解析实体 + 检索规则
  → 结构化事实返回
  → LLM 组织成口语回答
  → TTS 播放

讲规动画离线流程：
  script.full.json（口播） + full.anim.json（动画源）
  → compile_tutorial.py（TTS / runtime / compiled）
  → Unity 播放 compiled
```

## 关键目录

- `ontology/`：通用本体与 trigger pipeline。
- `games/`：各游戏规则数据、素材、教程、动画。
- `backend/BoardAI.Api/`：Runtime 服务。
- `client/`：Unity 客户端。
- `scripts/`：校验、索引、TTS、动画编译、QA 工具。
- `qa/`：检索 gold set 等评测数据。
- `.claude/memory/`：新会话必读的当前记忆。
- `.claude/archive/`：历史日志与旧版长文档，默认不读。
