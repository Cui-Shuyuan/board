---
name: ontology-design
description: 桌游本体 JSON 的设计约定、关键决策和当前进度（截至 2026-07-19，50 个概念）
metadata:
  node_type: memory
  type: project
  originSessionId: fbc4a732-5c5a-482a-a184-077fdaab9f56
---

# 桌游本体设计

## 文件位置
D:\workspace\board\ontology\ontology.json（统一本体，50 个概念）

## 统一本体
最初分为 Structure Ontology（静态结构）和 Procedure Ontology（流程时序）两个文件，后合并。JSON 给程序读，不考虑 LLM 上下文长度。

## 参考文档
项目根目录下两个 v0 文档定义了原始概念列表，是 JSON 本体的权威来源：
- `Board Game Structure Ontology v0.md` — 静态结构概念
- `Board Game Procedure Ontology v0.md` — 流程时序概念

## 基础约定
- 每个概念都有 `id`、`name`（中英双语）、`level`、`abstract`、`definition`、`constraints`
- **字段声明在概念顶层**（不再嵌套在 `fields` 内） 如 `"owner": { "type": "player | null", "default": null, ... }`。字段名即 JSON key，声明内容包括 `type`、`description`、可选的 `default`
- **`constraints.required`** = 子类必须实现的字段 ID 列表。格式为 `[{ "id": "<field_id>", "description": { "zh": "...", "en": "..." } }]`
- **`constraints.optional`** = 子类可选实现的字段 ID 列表，格式同上
- **`id` 字段特殊处理**：由 Object 定义，所有实例隐式拥有。因与概念自身的 `"id"` 元数据冲突，不在顶层声明，仅保留在 Object 的 constraints 中
- `parent` 字段表示继承关系。Level 0 概念无 parent
- `level`: 0 = 基础概念（不可再分），1+ = 游戏概念
- `abstract: true` = 基类，不可直接实例化

## 类型与引用约定
- **type 值三类**：(1) 概念引用 — 使用 `<concept_id>` 格式包裹，可带 `| null`；(2) 泛引用 — `concept_ref`（已弃用）；(3) 原始类型 — string、integer、boolean、any、map<string, any>、enum
- **`<concept_id>` 交叉引用**：所有 definition 和 description 中用 `<concept_id>` 包裹概念引用
- **字段 key 即类型**：当字段 key 本身是 `<concept_id>` 格式时（如 `"<ownership>"`、`"<declaration>"`），不再重复声明 `type` 字段——key 自身即表达了类型。仅当 key 是语义化命名（如 `current_player`）或需要窄化类型（如 `"type": "<gem>[]"`）时才保留 `type`
- **`concept_ref` / `concept_ref[]`** 已弃用，统一改用 `<concept_id>` 格式

## 关键设计决策

### 结构重构：fields → constraints + 顶层字段
最初字段定义放在 `fields.required/optional` 数组中。后重构为：字段声明提到概念顶层（字段名即 key），`fields` 改名 `constraints`，值从定义对象改为字段 ID 字符串。

### constraints 格式升级
从 `["string", ...]` 升级为 `[{ "id": "<field_id>", "description": {...} }]`。每个约束附带解释。

### Piece vs Token 的核心区分
- **Piece**：携带 Effect，可被 Play，改变游戏状态
- **Token**：无 Effect，不可 Play，被动标记事实

### Effect 三件套：Cost / Content / Effect
- **Cost**（extends Property）：Effect 触发前必须履行的代价
- **Content**（extends Property）：Effect 结算时发生的具体事件
- **Effect**（extends Property）：Cost + Content 的完整规格

### Action 三件套：Declaration / Trigger / Action ★ 新增
与 Effect 完全对偶：
- **Declaration**（extends Property）：玩家宣告的选择内容。`params` 为 required 字段
- **Action**（extends Event）：Declaration + Trigger 的完整规格。`actor`、`<declaration>`、`<trigger>` 均为 required
- 因果链：Action → Trigger → 后续 Event（Transfer、State 变更等）

### Event 新增字段 ★
- **precondition**：`<condition>[]`，optional。事件发生前必须满足的条件列表
- **rules**：`map<string, string>`，optional。中英双语描述超越结构化字段的执行规则约束

### Transfer 字段改名 ★
`what` → `<object>`，与其他概念引用 key 统一。

### Ownership 归属模型
Ownership extends State。三种来源：Zone 推导、固有归属、游戏中获取。变更须经 Effect 或 Transfer 触发。

### Event 体系与因果关系链
- **Action**：player 的决策声明，actor 必为 player
- **Trigger**：规则的事件调度器。actor 为 null。每个 Action 必定绑定一个 Trigger
- 因果链：Action → Trigger → 后续 Event
- **Resolve**：将 Effect 的 Content 实例化为真实 Event
- **Shuffle**：重排 Deck 中 Object 顺序

### Zone 体系
```
Zone (L0, abstract)
├── Reserve (L1, abstract) → Supply / Market / Deck / Pool (L2)
├── Discard Pile (L1)
└── Player Zone (L1, abstract)
    ├── Player Holding (L2) → Hand (L3)
    └── Development Area (L2)
```

### Procedure 嵌套模型
Round、Turn、Phase 自由嵌套，无固定层级。Phase 是唯一承载「规则上下文」的 Procedure。

## 当前进度（50 个概念）

### Level 0（7 个）
Object、Zone、State、Property、Event、Condition、Procedure

### Level 1 Structure（5 个）
Player、Resource、Piece、Aid、Token

### Level 1 Procedure（4 个）
Round、Turn、Phase、Transfer

### Level 1 State（2 个）
Ownership、Starting Player

### Level 1 Property（5 个）★ +1
Cost、Content、Effect、Declaration、Information Visibility

### Level 1 Zone（3 个）
Reserve（abstract）、Discard Pile、Player Zone（abstract）

### Level 1 Event（4 个）
Action、Trigger、Resolve、Shuffle

### Level 1 Condition（2 个）
Endgame Condition、Victory Condition

### Level 2 Piece（2 个）
Card、Tile

### Level 2 Reserve（4 个）
Supply、Market、Deck、Pool

### Level 2 Player Zone（2 个）
Player Holding、Development Area

### Level 2 Aid（4 个）
Public Board、Private Board、Player Aid、Rulebook

### Level 2 Action（2 个）
Activation、Play

### Level 2 Content（2 个）
Instant Effect、Continuous Content

### Level 2 Token（1 个）
Starting Player Marker

### Level 3 Player Holding（1 个）
Hand

**Why:** 本体是整个系统的类型系统，后续 Rule DSL、Tutorial Tree、Controller 都建立在它之上。
**How to apply:** 第一阶段世界模型定义基本完成（可持续补充）。当前正在第二阶段：为璀璨宝石（Splendor）编写结构化规则。所有规则表达使用本体中定义的概念和字段。
