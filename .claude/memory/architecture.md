---
name: architecture
description: 当前整体架构——本体/规则层、Runtime、检索、Android/语音接口；讲规动画见 tutorial-animation.md
metadata:
  type: project
---

# 现行架构

## 1. 分层总览

```text
L0 本体层：content/ontology/concepts.json + content/ontology/flow.json
L1 游戏规则层：content/games/{game}/concepts.json + flow.json + instances.json
L2 Runtime 层：backend/BoardAI.Api（Chat + Rules + Catalog + Content + ASR/TTS）
L3 教案/动画源：script.full.json + full.anim.json + _stage/*.stage.json
L4 运行产物：full.runtime.json + full.compiled.json + TTS 音频 + content manifests
L5 客户端：Android UaaL 原生壳（Kotlin + Compose） + Unity as a Library
```

## 2. 本体与规则层

- `content/ontology/concepts.json`：通用概念。当前 89 个。
- `content/ontology/flow.json`：通用 `trigger_pipeline`。
- `content/games/{game}/concepts.json`：游戏概念，按 `objects / actions / triggers / conditions / top_level_refs` 分组。
- `content/games/{game}/flow.json`：具体游戏流程。
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

### 规则服务拆分

`GameRulesService` 在 `204ce35` 后是薄 facade，只负责构造协作类、public wrapper、`Dispose`、`ClearDerivedCaches`。业务协作类：

- `RulesContentStore`：统一解析 ontology / 游戏 concepts / flow / instances 路径并读取；内部持有 `RulesDocumentStore`。
- `RulesDocumentStore`：按文件 `Length + LastWriteTimeUtc` 自动失效；文档变化时回调清派生缓存。
- `RulesConceptCatalog`：概念类型、列表、概念详情、action conditions。
- `RulesNameIndexService`：id → 中文名映射与精确名称/别名查找，按 game 缓存。
- `RulesSearchService`：关键词/向量混合检索、查询切分、名称短路。
- `RulesIndexService`：从概念目录、实例、顶层引用、flow 提取索引条目并触发向量索引重建。
- `RulesFlowService`：flow 节点 id → 祖先链、同级顺序、位置、最近 loop，按 game 缓存。
- `RulesReferenceService`：概念引用注解、一层 related 扩展、Plan ok 结果的引用扩展。
- `RulesFactService`：数量/容量/计分结构化事实，负责 score_table 等缓存。
- `RulesPlanService`：`execute_plan` 的实体解析、relation 导航与结果构造，只缓存 relation 类型映射。

### 规则与周边接口

- `GET /api/rules/games`
- `GET /api/rules/games/{game}/types`
- `GET /api/rules/games/{game}/concepts?type=...`
- `GET /api/rules/games/{game}/concepts/{id}`
- `GET /api/rules/games/{game}/actions/{actionId}/conditions`
- `GET /api/rules/games/{game}/search?q=...`
- `POST /api/rules/games/{game}/execute-plan`
- `POST /api/rules/admin/rebuild-index/{game}`
- `POST /api/rules/admin/rebuild-all`
- `GET /api/catalog/games`（读取 `content/catalog/*.json`，Android 首页游戏目录）
- `GET /api/content/games/{game}/manifest`（读取 `content/manifests/{game}.json`）
- `GET /api/content/games/{game}/files/{version}/{**filePath}`（版本化内容文件）
- `GET /api/content/games/{game}/files/{**filePath}`（旧客户端兼容路由）
- `POST /api/asr/once`（一次性 WAV 转文字）
- `POST /api/tts`（一次性文本转 MP3）

### 单工具 `execute_plan`

LLM 只输出查询计划，search/get_concept 由程序执行：

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
- `RulesDocumentStore` 以文件 `Length + LastWriteTimeUtc` 自动失效；改规则 JSON 无需重新启动 API 服务。
- 规则文档变化时，`ClearDerivedCaches()` 清名称索引、Plan 类型缓存、Flow 位置缓存、Fact score 缓存。
- 语义检索仍需重建 Qdrant 索引（`POST /api/rules/admin/rebuild-index/{game}` / `rebuild-all` 或 `tools/indexing/rebuild_index.py`）。
- 模型配置：`LLM:Thinking` 当前 `low`。

## 4. 检索架构

- Qdrant：独立 Windows 进程，按游戏分 collection `board_{gameId}`。
- Embedding：`bge-base-zh-v1.5` fp32 ONNX，768 维；模型目录 `ml_models/` 不进 Git。
- 索引内容：ontology 概念、游戏 concepts、flow 递归节点。
- 重建：`tools/indexing/rebuild_index.py`，默认增量同步；模型或提取逻辑大改时 `--full`。
- 搜索路由：向量优先 + 关键词降级。
- 短查询（≤2 字）走名称索引，长查询/整句走完整索引。
- namespace：`ontology::concept_id` 限定本体概念，避免与游戏层重名冲突。
- 检索回归门禁：`tools/qa/retrieval_gold.jsonl` + `tools/indexing/eval_retrieval.py`。

## 5. 交互模型

- Runtime 不追踪棋盘状态、不做 CV；Flow Guide 是后续产品方向。
- 对依赖状态的问题，LLM 向客人反问所需状态，程序只做规则解释。
- LLM 负责：理解语言、生成查询计划、组织答案。
- 程序负责：实体解析、规则查询、条件/顺序/边界判断。
- 回答风格：一到两句、汉字数字、无 markdown、TTS 友好。
- Android 问答面板已有 PTT → ASR → `/api/chat` → 回答 TTS 的代码路径；完整真机端到端验收仍待复测。

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
