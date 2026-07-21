---
name: ontology-design
description: 桌游本体 JSON 的设计约定、关键决策和当前进度（截至 2026-07-19，51 个概念）
metadata:
  node_type: memory
  type: project
  originSessionId: fbc4a732-5c5a-482a-a184-077fdaab9f56
---

# 桌游本体设计

## 文件位置
D:\workspace\board\ontology\ontology.json（统一本体，51 个概念）

## 统一本体
最初分为 Structure Ontology（静态结构）和 Procedure Ontology（流程时序）两个文件，后合并。JSON 给程序读，不考虑 LLM 上下文长度。

## 参考文档
项目根目录下两个 v0 文档定义了原始概念列表，是 JSON 本体的权威来源：
- `Board Game Structure Ontology v0.md` — 静态结构概念
- `Board Game Procedure Ontology v0.md` — 流程时序概念

## 基础约定
- 每个概念都有 `id`、`name`（中英双语）、`abstract`、`definition`、`constraints`
- **字段声明在概念顶层**（不再嵌套在 `fields` 内） 如 `"owner": { "type": "player | null", "default": null, ... }`。字段名即 JSON key，声明内容包括 `type`、`description`、可选的 `default`
- **`constraints.required`** = 子类必须实现的字段 ID 列表。格式为 `[{ "id": "<field_id>", "description": { "zh": "...", "en": "..." } }]`
- **`constraints.optional`** = 子类可选实现的字段 ID 列表，格式同上
- **`id` 字段特殊处理**：由 Object 定义，所有实例隐式拥有。因与概念自身的 `"id"` 元数据冲突，不在顶层声明，仅保留在 Object 的 constraints 中
- `parent` 字段表示继承关系。基础概念无 `parent`
- `abstract: true` = 基类，不可直接实例化

## 类型与引用约定
- **type 值三类**：(1) 概念引用 — 使用 `<concept_id>` 格式包裹，可带 `| null`；(2) 泛引用 — `concept_ref`（已弃用）；(3) 原始类型 — string、integer、boolean、any、map<string, any>、enum
- **`<concept_id>` 交叉引用**：所有 definition 和 description 中用 `<concept_id>` 包裹概念引用
- **字段 key 即类型**：当字段 key 本身是 `<concept_id>` 格式时（如 `"<ownership>"`、`"<declaration>"`），不再重复声明 `type` 字段——key 自身即表达了类型。**数组标记直接写在 key 里**（`"<event>[]"`、`"<condition>[]"`），不用 `"<event>": {"type": "<event>[]"}` 这种重复声明。仅当 key 是语义化命名（如 `current_player`）、需要窄化类型（如 `"<ownership>": {"type": "<player>"}`）或允许空值（如 `"<cost>": {"type": "<cost> | null"}`）时才保留 `type`
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

### Trigger 的四要素结构 ★
Trigger 的本质是「**timing + condition → events**」，三个 required 字段：
- `<timing>`——触发时机（条件检查钟声）：状态变化瞬间 / 阶段边界 / 某 event 完成后。**满足条件不代表立即触发——时机到且条件满足才触发**；时机未至条件满足只是待命。Timing 是基础概念（与 Condition 平级，带 params 供程序 watcher 侦测）。**timing 只属于 trigger 和 procedure（起止边界），event 不持有 timing**——trigger 触发后 event 序列按依赖关系执行。字段 key 即类型，写作 `"<timing>": {...}` 而非 `"timing": {"type": "<timing>"}`
- `<condition>`——纯状态事实（如「宝石总数 >10」），不含时机描述。每个 trigger 都必须有 condition，即使被 action 触发：「此 action 的 precondition 满足且 declaration 合法」本身就是 condition。trigger 不用 precondition 字段
- `<event>[]`——触发后启动的后续事件列表

**术语约定：trigger 用「触发」，不用「激活」**——激活（Activation）是玩家侧的 action 概念；trigger 不以玩家意志为转移。英文用 fire。

**Event 顺序语义（do_after）**：
- 每个 `<event>` 可声明 `id` 和 `do_after: ["<event_id>", ...]`，表示必须等被引用的 event 完成后才能执行
- 无 `do_after` 的 event 互相独立，可任意顺序或并行执行
- 如购买发展卡：`pay_cost` 无依赖，`play_card` 声明 `do_after: ["pay_cost"]`——若颠倒，新卡的 discount 会对本次购买生效
- 如保留发展卡：两个 event 互相独立，均不写 `do_after`，AI 应表述为「同时进行 / 不分先后」
- 单 event trigger 无需 `id` 和 `do_after`

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
Zone (abstract)
├── Reserve (abstract) → Supply / Personal Supply / Market / Deck / Pool
├── Discard Pile
├── Player Zone (abstract)
│   ├── Player Holding → Hand
│   └── Development Area
└── Track → Score Track
```

### Procedure 嵌套模型
Round、Turn、Phase 自由嵌套，无固定层级。Phase 是唯一承载「规则上下文」的 Procedure。

## 当前进度（核心概念持续扩展中）

### 基础概念（8 个）
Object、Zone、State、Property、Event、Condition、Timing、Procedure

### 结构概念（5 个）
Player、Resource、Piece、Aid、Token

### 结构扩展（2 个）★ +Marker
Score（extends Resource）、Marker（extends Token）

### 流程概念（4 个）
Round、Turn、Phase、Transfer

### 状态概念（2 个）
Ownership、Starting Player

### 属性概念（5 个）
Cost、Content、Effect、Declaration、Information Visibility

### 区域概念（3 个）
Reserve（abstract）、Discard Pile、Player Zone（abstract）

### 区域/轨道扩展（2 个）★ Track 改为 Zone 子类
Track（extends Zone）、Score Track（extends Track）

### 事件概念（4 个）
Action、Trigger、Resolve、Shuffle

### 条件概念（2 个）
Endgame Condition、Victory Condition

### Piece 扩展（2 个）
Card、Tile

### Reserve 扩展（5 个）★ +Personal Supply
Supply、Personal Supply、Market、Deck、Pool

### Player Zone 扩展（2 个）
Player Holding、Development Area

### Aid 扩展（4 个）
Public Board、Player Board、Player Aid、Rulebook

### Action 扩展（1 个）
Activation

### Event 扩展（1 个）
Play

### Content 扩展（2 个）
Instant Effect、Continuous Content

### Token 扩展（1 个）
Starting Player Marker

### Player Holding 扩展（1 个）
Hand

## 关键设计决策更新

- **Token 作为 Resource 与 Marker 的物理基类**：`<token>` 是桌游中最常见的小型计数/标记物；`<resource>` 继承 `<token>`（作为可被消耗的价值物），`<marker>` 继承 `<token>`（作为状态/位置指示物）。
- **Track 是 Zone 的子类**：轨道不再属于 `<aid>`，而是一种「有序 zone」，其中 marker 只做内部位置移动，不发生 zone 间 transfer。
- **Score Zone 已移除**：`<score_track>` 自己就是 zone，不再需要单独的 `<score_zone>`。
- **个人供应堆与公共供应堆分离**：新增 `<personal_supply>`，按玩家划分、专属取用，但其中的物件尚未归该玩家所有；`<supply>` 明确为公共供应堆，所有玩家均可取用。仓库等已属于玩家的存储区应归类为 `<player_holding>`。

## 为《文明演化》扩展本体的计划

第二款游戏《文明演化》的复杂度远高于 Splendor，现有核心概念无法直接覆盖以下机制，已获准扩展 ontology：

- **轨道机制**: `<track>` 已在 ontology 中改为 `<zone>` 子类，`<score_track>` 继承 `<track>`。具体游戏的进程轨、恩惠轨、天气轨、阶段序列等作为游戏级概念定义，不入统一本体。
- **骰子机制**: `<dice>` / `<die>`、`<die_roll>`、点数修改、创意标记效果
- **升级机制**: `<upgrade>`，表达模组从等级一翻至等级二、替换为等级三
- **区域与地点**: `<region>`（大陆板块上的连续同色区域）、`<terrain>`（森林/草原等七种类型）、`<campsite>`、`<location>`（地点板块，extends `<tile>`）
- **选择与替代**: `<alternative_cost>` / `<choice>`，用于「支付资源或满足条件」「二选一行动」
- **被动/持续效果**: `<passive_effect>` / `<location_effect>`，表达地点在激活模组时追加的效果

扩展前应先查两个 v0 文档确认是否有对应原始概念；若无，再按当前约定新增。

**Why:** 本体是整个系统的类型系统，后续 Rule DSL、Tutorial Tree、Controller 都建立在它之上。
**How to apply:** 第一阶段世界模型定义基本完成（可持续补充）。当前正在第二阶段：为璀璨宝石（Splendor）编写结构化规则，并准备为第二款游戏《文明演化》扩展本体。所有规则表达使用本体中定义的概念和字段。
