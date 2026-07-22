---
name: runtime-architecture
description: BoardAI.Api 后端 Runtime 的具体架构、接口设计与关键实现决策
metadata:
  type: project
---

# Runtime 架构

## 服务位置
`D:\workspace\board\backend\BoardAI.Api/`，.NET 9 Web API，默认监听 `http://localhost:5000`。

## 核心组件

| 文件 | 职责 |
|---|---|
| `Controllers/ChatController.cs` | `POST /api/chat` 入口 |
| `Controllers/RulesController.cs` | 规则查询 HTTP 入口 |
| `Services/ChatOrchestratorService.cs` | ReAct 式工具循环，对接 LLM 与规则服务 |
| `Services/GameRulesService.cs` | 读取 `ontology/ontology.json`、`games/{game}/concepts.json`、`games/{game}/flow.json` |
| `Services/ILLMService.cs` / `DeepSeekLLMService.cs` | LLM 抽象与 DeepSeek v4 Pro 实现 |
| `Models/ChatMessages.cs` | OpenAI 兼容的消息/工具/响应模型 |

## 接口设计

### 对话接口
`POST /api/chat`

请求：
```json
{
  "game_id": "splendor",
  "messages": [
    { "role": "user", "content": "贵族该怎么获得？" },
    { "role": "assistant", "content": "..." },
    { "role": "user", "content": "游戏怎么结束？" }
  ]
}
```

响应：
```json
{ "reply": "有人声望分达到十五分时..." }
```

**无状态**：历史由前端维护，后端只在请求到达时把 `messages` 拼到 system prompt 后面。这样客人退出再进入，只要前端把历史带回来，上下文就不会丢。

### 规则查询接口
- `GET /api/rules/games`
- `GET /api/rules/games/{game}/types`
- `GET /api/rules/games/{game}/concepts?type=objects|actions|triggers|conditions|top_level_refs|flow|ontology`
- `GET /api/rules/games/{game}/concepts/{id}` → 返回所有匹配的 concept 数组；支持 `ontology::concept_id` 限定只查 ontology，不带 namespace 时同时查当前游戏与 ontology
- `GET /api/rules/games/{game}/actions/{actionId}/conditions`
- `GET /api/rules/games/{game}/search?q=...` → 支持 `ontology::concept_id` 限定搜索范围

`{game}` 对应 `games/` 下的目录名。新增游戏只需新建目录放 JSON，代码不改动。

## 关键决策

### 1. 无状态优于有状态
早期实现过 `session_id` + 服务端内存保存历史，但会带来两个问题：
- 前端退出再进入，session 丢失，历史就没了
- 服务重启，所有会话清空

改为前端传完整 `messages` 后，服务端更简单、更可靠，也更贴合 OpenAI/DeepSeek 的原生 API 形态。

### 2. Prompt 配置驱动
`LLMOptions.cs` 中默认 prompt 为空，`appsettings.json` 中的 `LLM:SystemPrompt` 是唯一来源。system prompt 支持 `{game_name}` 占位符，运行时被替换为实际 `game_id`。

好处：
- 调 prompt 不用重新编译
- 游戏名动态注入，prompt 本身保持通用

### 3. 工具集合最小化
当前只暴露 3 个工具给 LLM（具体 schema 由代码在每次请求时随 `tools` 参数传入，不在 system prompt 中重复描述）：
- `search_concepts(game_id, query)`
- `get_concept(game_id, concept_id)`
- `get_action_conditions(game_id, action_id)`

所有工具的第一个参数都是 `game_id`，保证多游戏场景下不会查错数据。

### 4. 顶层引用动态检测
`GameRulesService` 不再硬编码 `"<hand>"`、`"<player_holding>"` 等 Splendor 专属顶层键，而是从 `games/{game}/concepts.json` 的顶层对象属性中自动检测。不同游戏可以有不同顶层引用。

### 5. 搜索质量优化：向量检索
`search_concepts` 已升级为**向量优先 + 关键词降级**双路由：
- 向量检索：BGE-small-zh ONNX 模型（512 维）→ Qdrant 余弦相似度 → top-10
- 关键词降级：原有的分词 + 子串匹配（向量不可用或无结果时自动切换）

Qdrant 按游戏分 collection（`board_{gameId}`），索引涵盖 ontology、concepts.json（objects/actions/triggers/conditions/top_level_refs）、flow.json（递归展开全流程树）。

向量索引通过 Python 脚本 `scripts/rebuild_index.py` 独立管理，不依赖 .NET。启动服务时不再重建索引（已持久化在 Qdrant 磁盘），改规则后需手动跑脚本。

详见 [[vector-search]]。

### 6. Namespace 支持
为避免 ontology 概念与游戏自定义概念重名（如 Civolution 中游戏自有的 `activity` 与 ontology 的 `action`），规则查询支持 `ontology::concept_id` 前缀：
- `get_concept(game_id, "ontology::resource")` 只返回 ontology 中的 `resource`
- `get_concept(game_id, "resource")` 同时搜索当前游戏与 ontology，返回所有匹配结果数组
- `search_concepts` 同样支持 `ontology::...` 限定范围

工具描述已更新，提示 LLM 可以按需带 namespace 查询。

### 7. 日志策略
控制台输出：
- 每轮 LLM 的 `reasoning_content`
- 每轮调用的工具名与参数
- 每个工具的返回结果（JSON 已美化、中文不转义）
- 最终回答

便于在命令行调试时观察 LLM 的查询链。

### 8. 回答风格约束
通过 system prompt 约束：
- 简洁，一到两句话
- 汉字数字
- 无 markdown
- 无填充语气词
- 不用本体术语（"发展区" → "自己面前"）
- TTS 友好（不用加号、引号、括号）
- 查到足够信息就停止，不要过度查询
- 工具返回中的 `<concept_id>` 引用可继续查

System prompt 中不再重复列出可用工具（工具 schema 已通过 `tools` 参数单独传递），以减轻 prompt 负担。

## LLM 调用次数
`MaxToolRounds` 设为 `int.MaxValue`，不再限制 LLM 为一题调几次工具，方便观察复杂问题上的真实查询深度。实际生产时可根据成本和延迟再收紧。

## 前端流程建议
1. 启动时或选游戏前调 `GET /api/rules/games` 拿到游戏列表
2. 客人选定一款游戏，前端记住 `game_id`
3. 对话期间所有 `/api/chat` 请求都带同一个 `game_id`
4. 前端本地维护 `messages`，每次把完整历史发过去，收到回答后追加到本地历史

## 与 [[interaction-model]] 的关系

[[interaction-model]] 定义了 LLM 与程序的**分工边界**（什么交给 LLM、什么交给程序）。本文档定义了**具体实现方式**（HTTP 接口、消息格式、工具设计、无状态策略）。二者共同构成当前 Runtime 层。

**Why:** 把 Runtime 实现细节从概念文档中拆出来，避免 [[project-overview]] 和 [[splendor-progress]] 过于膨胀，也便于后续接新游戏或换前端时快速查阅。
**How to apply:** 新增游戏时按本文档接口接入；调整 prompt、工具或搜索逻辑时同步更新本文档与 [[splendor-progress]]。
