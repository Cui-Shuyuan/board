---
name: architecture
description: 当前整体架构——本体/规则层、Runtime、检索、交互模型；讲规动画见 tutorial-animation.md
metadata:
  type: project
---

# 现行架构

## 1. 分层总览

```text
L0 本体层：ontology/concepts.json + ontology/flow.json
L1 游戏规则层：games/{game}/concepts.json + flow.json + instances.json
L2 Runtime 层：backend/BoardAI.Api（Chat + Rules + 检索）
L3 教案/动画源：script.full.json + full.anim.json + _stage/*.stage.json
L4 运行产物：full.runtime.json + full.compiled.json + TTS 音频
L5 客户端：Unity 6 安卓 App
```

## 2. 本体与规则层

- `ontology/concepts.json`：通用概念。当前 89 个。
- `ontology/flow.json`：通用 `trigger_pipeline`。
- `games/{game}/concepts.json`：游戏概念，按 `objects / actions / triggers / conditions / top_level_refs` 分组。
- `games/{game}/flow.json`：具体游戏流程。
- 关键关系：
  - `extends`：结构扩展，增加父概念没有的字段。
  - `specifies`：参数绑定，填充父概念已有字段。
  - `instance_of`：具体个体，字段全填。
- 规则核心模型：
  - Action、Effect、Trigger 统一为 `condition → cost → target → content` 的递归调度器。
  - `instant_content` 是边沿语义，`continuous_content` 是电平语义。
  - `<pipeline>` 是流程唯一结构原语，负责 options、do_after、loop。
- 详细编写规范见 `conventions.md`；历史设计推演见 `.claude/archive/memory/2026-09-23/ontology-design.md`。

## 3. Runtime 服务

位置：`backend/BoardAI.Api`，.NET 9，默认 `http://localhost:5000`。

### 对话接口

`POST /api/chat`

```json
{
  "game_id": "splendor",
  "messages": [
    { "role": "user", "content": "贵族怎么获得？" }
  ]
}
```

返回 `{ "reply": "..." }`。

- 无状态：前端维护完整 `messages` 历史。
- `game_id` 必填，用于限定规则数据。

### 规则接口

- `GET /api/rules/games`
- `GET /api/rules/games/{game}/types`
- `GET /api/rules/games/{game}/concepts?type=...`
- `GET /api/rules/games/{game}/concepts/{id}`
- `GET /api/rules/games/{game}/search?q=...`
- `POST /api/rules/games/{game}/execute-plan`（调试/评测用）
- `POST /api/rules/admin/rebuild-index/{game}` / `rebuild-all`

### 单工具 `execute_plan`

LLM 不再直接调用 search/get_concept，只输出查询计划：

- `relation`：`explain / condition / ordering / boundary / flow / list / identify`
- `entity`：概念 id、准确中文名或自然语言描述
- 一个 plan 可包含多个 queries，一次拿全。

程序负责实体解析、语义/关键词检索、概念闭包、条件/顺序/边界计算。好处：
- LLM 不选工具，不会幻觉工具名；
- 查询路径确定，可评测；
- 返回结果统一注解 `<concept_id>(中文名)`，避免 LLM 自译英文 id。

### 三层回答

- `tier1`：精确命中，直接返回规则事实。
- `tier2`：名称语义检索补候选（`Status=unresolved` + Candidates）。
- `tier3`：无可信结果，`Status=no_match`，显式说明规则库查不到，回答日志打 tier 标记。

### 配置与缓存

- system prompt 唯一来源：`appsettings.json` 的 `LLM:SystemPrompt`，支持 `{game_name}`。
- API key 只走环境变量，不写仓库。
- `GameRulesService.LoadJson` 按路径缓存且不失效：改规则文件后必须重启 API。
- 模型配置：`LLM:Thinking` 当前 `low`。

## 4. 检索架构

- Qdrant：独立 Windows 进程，按游戏分 collection `board_{gameId}`。
- Embedding：`bge-base-zh-v1.5` fp32 ONNX，768 维；模型目录 `ml_models/` 不进 Git。
- 索引内容：ontology 概念、游戏 concepts、flow 递归节点。
- 重建：`scripts/rebuild_index.py`，默认增量同步；模型或提取逻辑大改时 `--full`。
- 搜索路由：向量优先 + 关键词降级。
- 短查询（≤2 字）走名称索引，长查询/整句走完整索引。
- namespace：`ontology::concept_id` 限定本体概念，避免与游戏层重名冲突。
- 检索回归门禁：`qa/retrieval_gold.jsonl` + `scripts/eval_retrieval.py`。

## 5. 交互模型

- 第一阶段 Runtime 不追踪棋盘状态、不做 CV。
- 对依赖状态的问题，LLM 向客人反问所需状态，程序只做规则解释。
- LLM 负责：理解语言、生成查询计划、组织答案。
- 程序负责：实体解析、规则查询、条件/顺序/边界判断。
- 回答风格：一到两句、汉字数字、无 markdown、TTS 友好。

## 6. 讲规动画

独立文档见 `tutorial-animation.md`。一句话架构：

```text
口播脚本 → TTS 冻结 → 动画源(state_ops/camera_ops/clips + time_anchors)
→ compile_tutorial.py → runtime + compiled → Unity 播放
```

## 7. 关键约定

- 已有 ontology 概念直接引用 `<ontology::concept_id>`，不在游戏层重复封装。
- JSON 是程序运行前提，写完必须过 `validate_rules.py`。
- 程序不能从结构化规则得出的事实，不交给 LLM 硬猜。
- 改动前先读本文件与 `conventions.md`；历史原因查归档。
