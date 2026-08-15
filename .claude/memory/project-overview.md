---
name: project-overview
description: AI桌游讲师项目总览——核心架构、设计理念、技术选型
metadata: 
  node_type: memory
  type: project
  originSessionId: fbc4a732-5c5a-482a-a184-077fdaab9f56
---

# AI 桌游讲师项目

## 核心定位
不是一个 AI 聊天机器人，而是一个规则驱动的 AI Runtime / 桌游规则操作系统（Board Game Rules OS）。

## 核心设计原则
- **LLM 负责理解语言，程序负责执行规则**——规则查询、校验、模拟全由程序完成，LLM 只管把结果翻译成人话
- **数据驱动**：每款桌游就是一个目录（manifest.json + ontology/ + rules/ + tutorial/ + media/），增加桌游不改代码
- **规则用 JSON/DSL 表达，不是 Markdown**——程序需要结构化条件+效果
- **意图分类（Intent）优于规则分类**——七八个核心接口（ExplainConcept、CheckAction、SimulateAction 等）覆盖所有用户问题

## 技术选型
- 后端：C#（状态机友好，未来可接 Unity）
- 前端：PWA / Flutter（平板端）——**2026-08-15 更新：讲规模块客户端定为 Unity 原生安卓 App**（麦克风安全上下文是弃 H5 的关键理由），详见 [[tutorial-module]]
- LLM 层：接口抽象 ILLM，支持 GPT/Claude/Gemini 等切换
- STT/TTS：独立模块，可插拔
- 部署：Windows 主机本地运行，平板走内网 HTTP/WebSocket
- 店内网络：路由器 Guest Network 隔离客人设备

## 应用场景

一家桌游主题小店。每张桌子配一台平板，客人坐下后选择游戏，AI 讲师开始按教程树（Tutorial Tree）讲解规则。客人可以随时按住说话提问（「为什么不能拿两个蓝？」「我下一步最好做什么？」），AI 回答。全程 Push-to-Talk 作为唯一语音输入方式，状态机只在 Idle → Recording → Thinking → Speaking → Idle 之间切换。

## 开发阶段

1. **第一阶段（基本完成）：定义世界模型** — 67 个本体概念已定义，可持续补充。
2. **第二阶段（基本完成）：Rule DSL** — 用结构化 JSON 表达具体游戏规则。首个游戏：璀璨宝石（Splendor），`concepts.json` 与 `flow.json` 已完成。
3. **第三阶段（基本完成）：Runtime / Intent Interface** — `backend/BoardAI.Api` 已跑通：支持客人选择游戏后多轮对话；LLM 通过 `search_concepts` / `get_concept` / `get_action_conditions` 查询规则，程序返回结构化数据，LLM 再组织成 TTS 友好的口语回答。Splendor 验证效果良好。**2026-07-22 升级为向量语义搜索**（Qdrant + BGE-small-zh ONNX），解决中文同义词/近义词检索问题（如"白色骰子" → "白色的六面骰"）。详见 [[vector-search]] 与 [[runtime-architecture]]。
4. **第四阶段（当前重点）：补充更多游戏与游戏元信息** — 在 `games/` 下录入第二款桌游，验证系统在非 LLM 熟知规则上的真实表现；为每款游戏增加 `manifest.json` 供前端选游戏。
5. **第五阶段：Tutorial Tree** — 结构化教程内容，每个节点配 TTS/字幕/关键词。**技术选型已定稿（2026-08-15）：Unity 原生安卓客户端 + 数据驱动 2D/2.5D sprite 动画（50° 固定俯角）；Unity 6 环境已就绪（全落 D 盘），client 骨架与首个原型已跑通**，详见 [[tutorial-module]]
6. **第六阶段：Controller** — 状态机连接 STT → LLM → Rule Engine → TTS。
7. **第七阶段：UI** — PWA/Flutter 前端，平板作为主要交互入口。

## 关键实现现状

- **后端服务**：`backend/BoardAI.Api/`，.NET 9，监听 `http://localhost:5000`
- **规则查询**：`Controllers/RulesController.cs` + `Services/GameRulesService.cs`，按游戏目录读取 `ontology/concepts.json`、`games/{game}/concepts.json`、`games/{game}/flow.json`
- **对话接口**：`Controllers/ChatController.cs` + `Services/ChatOrchestratorService.cs`，无状态设计，请求带 `game_id` + `messages` 历史，返回 `{ "reply": "..." }`
- **工具调用**：LLM 可调 4 个工具——`search_concepts`（语义搜索，返回带元数据的 Top-K 结果）、`get_concept`、`get_action_conditions`、`list_concept_ids`（穷举全量 ID+名称，极轻量）。`search_concepts` 已升级为**向量语义搜索**（BGE-small-zh ONNX + Qdrant），向量优先、关键词降级
- **向量检索**：Qdrant（独立进程，按游戏分 collection）+ `EmbeddingService`（ONNX 推理）+ `scripts/rebuild_index.py`（Python 离线重建脚本）。索引涵盖 ontology、concepts.json、flow.json。详见 [[vector-search]]
- **Prompt 管理**：`appsettings.json` 中的 `LLM:SystemPrompt` 是唯一来源，支持 `{game_name}` 占位符；`LLMOptions.cs` 中默认 prompt 为空。经过多次迭代，已去掉冗余工具列表，加入"查到足够信息就停"等约束。
- **日志**：控制台输出每轮 reasoning、工具调用参数与结果，JSON 已美化且中文正常显示；每次请求打印 `[Chat] game: xxx, question: xxx, count: N`

## 当前重点

第三阶段 Runtime 已通过 Splendor 验证，回答质量达到可用水平（简洁、TTS 友好、支持多轮上下文、能拒绝非桌游问题）。

**第二款游戏《文明演化》（Civolution）正在进行 Phase A**。已完成：
- `games/civolution/concepts.json` 对象清单层（169 个对象），已完成多项重构：`private_board` → `player_board` 重命名、supply 公共/玩家区统一用 `<ownership>` 表达、进程/流程版图改为 `<progress_board>` / `<sequence_board>` 引用、新增 `<favor_of_ager_track>`、新增 `<ontology::setting>`
- 模块升级模型重构（2026-07-24）：采用 Lose + Gain 模型——15 个主模块各拆为 3 个独立 effect 实例（45 个），新增 15 个 module tile，删除 `module.level` 字段。`<upgrade>` 改为 trigger，`<lose>`/`<gain>` 新增为 Event 子类
- `games/civolution/flow.json` 流程骨架（Setup、4 时代 × 8 阶段、终局计分）
- ontology namespace 方案确认（`<ontology::concept_id>`）并完成后端查询支持
- 明确设计约定：ontology 已有概念直接引用，不在游戏层重复封装
- **`<piece>.parts` 统一机制**：物理载体与逻辑身份解耦，替代 `<ontology::zone>[]` 和分散的 piece 字段。Civolution 8 概念 + Splendor 2 概念已迁移完成
- **图片提取已完成**：用 PyMuPDF + OpenCV 投影分析法从 PDF 规则书第 4-5 页（组件目录）成功提取 126 个组件裁切。脚本：`scripts/extract_components_cv.py`。结果在 `games/civolution/media/`，标注图在 `games/civolution/page-*_600dpi_annotated.jpg` 供人工审核

**当前阻塞**：剩余 supply、deck、piece/token、骰子、研究牌、芯片等组件待 review；flow.json 占位 action（`<activate_module>`、`<reset>` 等）已补全，仅剩 `<action_phase_end>` 一个悬空引用（until 条件未定义）。详见 [[civolution-progress]]。

短期仍需为每款游戏补 `manifest.json` 供前端选游戏。

**讲规模块（第五阶段）已开工（2026-08-15）**：client 项目位于 `client/`（Unity 6 + URP 模板，全部落 D 盘），运行时搭景播放器 + 占位素材已跑通首个原型（50° 俯角 + 三圆片演示动画）；观感迭代待用户用多模态模型发截图。详见 [[tutorial-module]]。

## 项目路径
D:\workspace\board

## 原始讨论
项目根目录下的 `聊天记录.txt` 包含了项目起始时的完整讨论，涵盖架构推演、技术选型、部署方案等全部细节。

**Why:** 这是项目的根本定位和架构基础。后续所有设计决策都以此为出发点。
**How to apply:** 遇到架构选择时，优先考虑"程序确定性"而非"LLM 智能性"。
