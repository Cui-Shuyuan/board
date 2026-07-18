---
name: ontology-design
description: 桌游本体 JSON 的设计约定、关键决策和当前进度（截至 2026-07-18，45 个概念，全部完成）
metadata:
  node_type: memory
  type: project
  originSessionId: fbc4a732-5c5a-482a-a184-077fdaab9f56
---

# 桌游本体设计

## 文件位置
D:\workspace\board\ontology\ontology.json（统一本体，45 个概念）

## 统一本体
最初分为 Structure Ontology（静态结构）和 Procedure Ontology（流程时序）两个文件，后合并。JSON 给程序读，不考虑 LLM 上下文长度。

## 参考文档
项目根目录下两个 v0 文档定义了原始概念列表，是 JSON 本体的权威来源：
- `Board Game Structure Ontology v0.md` — 静态结构概念
- `Board Game Procedure Ontology v0.md` — 流程时序概念

## 基础约定
- 每个概念都有 `id`、`name`（中英双语）、`level`、`abstract`、`definition`、`constraints`
- **字段声明在概念顶层**（不再嵌套在 `fields` 内）。如 `"owner": { "type": "player | null", "default": null, ... }`。字段名即 JSON key，声明内容包括 `type`、`description`、可选的 `default`
- **`constraints.required`** = 子类必须实现的字段 ID 列表。格式为 `[{ "id": "<field_id>", "description": { "zh": "...", "en": "..." } }]`，每个约束附有解释说明为什么需要此字段
- **`constraints.optional`** = 子类可选实现的字段 ID 列表，格式同上
- **`id` 字段特殊处理**：由 Object 定义，所有实例隐式拥有。因与概念自身的 `"id"` 元数据冲突，不在顶层声明，仅保留在 Object 的 constraints 中
- `parent` 字段表示继承关系。Level 0 概念无 parent
- `level`: 0 = 基础概念（不可再分），1+ = 游戏概念
- `abstract: true` = 基类，不可直接实例化

## 类型与引用约定
- **type 值两类**：(1) 概念引用 — 使用 `<concept_id>` 格式包裹（如 `<player>`、`<zone>`、`<cost>`），可带 `| null`；(2) 原始类型 — string、integer、boolean、any、map<string, any>、enum
- **`<concept_id>` 交叉引用**：所有 definition 和 description 中用 `<concept_id>` 包裹概念引用。LLM 可据此识别并沿链查找
- **字段 key 也是 `<concept_id>`**：当字段本身「就是」某个概念时，key 用 `<concept_id>` 格式（如 `"<ownership>"`、`"<information_visibility>"`、`"<cost>"`）。当字段只是「引用」概念表达关系时，用语义化命名（如 `current_player`）
- **`concept_ref` / `concept_ref[]`** 已弃用，统一改用 `<concept_id>` 格式

## 关键设计决策

### 结构重构：fields → constraints + 顶层字段
最初字段定义放在 `fields.required/optional` 数组中。后重构为：字段声明提到概念顶层（字段名即 key），`fields` 改名 `constraints`，值从定义对象改为字段 ID 字符串。约束表达「子类必须/可选实现哪些字段」，自身字段表达「这个概念有什么属性」。

### constraints 格式升级
从 `["string", ...]` 升级为 `[{ "id": "<field_id>", "description": {...} }]`。每个约束附带解释，说明「为什么需要此字段」，而非仅声明存在依赖。

### predicate 字段被删除
State/Property/Event 曾有 `predicate` 字段，后删除。类型层级本身承载分类信息。

### 删掉的 Level 0 概念
- Randomness（随机）：Shuffle 改为 Event 子类，随机性是结果特征不是事件种类
- Score（分数）：待评估，可能拆解为 State + Resource + Victory Condition

### Procedure 嵌套模型
Round、Turn、Phase 自由嵌套，无固定层级。Phase 是唯一承载「规则上下文」的 Procedure。

### Piece vs Token 的核心区分
- **Piece**：携带 Effect，可被 Play，改变游戏状态
- **Token**：无 Effect，不可 Play，被动标记事实

### Resource 与 Token 的关系
Resource 自带 `appearance`，不依赖 Token。避免 1:1 冗余配对。

### Cost / Content / Effect 三件套
- **Cost**（extends Property）：Effect 触发前必须履行的代价
- **Content**（extends Property）：Effect 结算时发生的具体事件
- **Effect**（extends Property）：Cost + Content 的完整规格

### Ownership 归属模型
Ownership extends State。三种来源：Zone 推导、固有归属、游戏中获取。变更须经 Effect 或 Transfer 触发。

### Event 体系与因果关系链
- **Action**：player 的决策声明（「我要做这件事」），actor 必为 player
- **Trigger**：规则的事件调度器——所有状态变更最终由 Trigger 启动。actor 为 null
- 因果链：Action → 引发 → Trigger → 启动后续 Event（Effect 结算等）
- **Resolve**：将 Effect 的 Content 实例化为真实 Event 并应用到 State。依赖 `<content>` 约束
- **Shuffle**：重排 Deck 中 Object 顺序。类型改为 Event（v0 中为 Randomness），actor 可为 player 或 null

### Zone 体系
```
Zone (L0, abstract)
├── Reserve (L1, abstract) → Supply / Market / Deck / Pool (L2)
├── Discard Pile (L1)
└── Player Zone (L1, abstract)
    ├── Player Holding (L2) → Hand (L3)
    └── Development Area (L2)
```
- Supply：无差别物件，无序取用
- Market：各不相同物件，主动挑选
- Deck：有序叠放，固定顶部抽取，支持 Shuffle
- Pool：无序集合，随机盲抽，不支持 Shuffle，支持概率调整

### Aid 子类
Public Board、Private Board、Player Aid、Rulebook。均继承 Aid，自身不加新字段。Aid 不产生 Zone，仅视觉映射。

## 当前进度（45 个概念，全部完成）

### Level 0（7 个）
Object、Zone、State、Property、Event、Condition、Procedure

### Level 1 Structure（5 个）
Player、Resource、Piece、Aid、Token

### Level 1 Procedure（4 个）
Round、Turn、Phase、Transfer

### Level 1 State（1 个）
Ownership

### Level 1 Property（4 个）
Cost、Content、Effect、Information Visibility

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

### Level 3 Player Holding（1 个）
Hand

**Why:** 本体是整个系统的类型系统，后续 Rule DSL、Tutorial Tree、Controller 都建立在它之上。
**How to apply:** 第一阶段世界模型定义完成。后续进入第二阶段 Rule DSL 时，所有规则表达都使用本体中定义的概念和字段。
