---
name: ontology-design
description: 桌游本体 JSON 的设计约定、关键决策和当前进度（截至 2026-07-26，66 个概念）
metadata:
  node_type: memory
  type: project
  originSessionId: fbc4a732-5c5a-482a-a184-077fdaab9f56
---

# 桌游本体设计

## 文件位置
D:\workspace\board\ontology\ontology.json（统一本体，66 个概念）

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
- **`extends` / `specifies` / `instance_of`** 三种层级关系：`extends`=结构扩展（加新字段）、`specifies`=参数绑定（填已有字段）、`instance_of`=具体个体（字段全满）。三者均可链化——extends 和 specifies 可任意深度交替，instance_of 是唯一终端。基础概念无 `extends`
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
├── Reserve (abstract) → Supply / Market / Deck / Pool
├── Discard Pile
├── Player Zone (abstract)
│   ├── Player Holding → Hand
│   └── Development Area
└── Track → Score Track
```

### Procedure 嵌套模型
Round、Turn、Phase 自由嵌套，无固定层级。Phase 是唯一承载「规则上下文」的 Procedure。

## 当前进度（核心概念持续扩展中）

本体已从最初的 51 个概念扩展到 **67 个概念**。新增内容部分来自第二款游戏《文明演化》的机制扩展，但 Civolution 专属概念（`<encampment>`、`<site>`、`<favor_test>`、`<terrain>`、`<region>`）已于 2026-07-25 移回游戏层。

### 基础概念（8 个）
Object、Zone、State、Property、Event、Condition、Timing、Procedure

### 枚举概念（1 个）★
Multiple Choice Enum（多选方式：EXECUTE_ALL / CHOOSE_ONE / CHOOSE_AT_LEAST_ONE）

### 结构概念（6 个）★ +Board
Player、Resource、Piece、**Board**、Aid、Token

### 结构扩展（2 个）★ +Marker
Score（extends Resource）、Marker（extends Token）

### Board 扩展（2 个）★ 从 Aid 拆分
Public Board（extends Board）、Player Board（extends Board）

### 流程概念（4 个）
Round、Turn、Phase、Transfer

### 状态概念（2 个）
Ownership、Starting Player

### 属性概念（5 个）
Cost、Content、Effect、Declaration、Information Visibility

### 区域概念（3 个）
Reserve（abstract）、Discard Pile、Player Zone（abstract）

### 区域/轨道扩展（2 个）★ Track 改为 Zone 子类，slots 替代 scale
Track（extends Zone）、Score Track（extends Track）

`<track>` 的 `scale` 可选字段已改为 `slots` 必填字段（`string | slot_spec[]`）。数值轨写取值范围字符串（`"0–12"`），槽位轨写对象数组（每个 slot 含 `name` + `<ontology::effect>`）。

### 事件概念（4 + 2 新增）
Action、Trigger、Resolve、Shuffle、**Lose（新增）**、**Gain（新增）**

### 条件概念（2 个）
Endgame Condition、Victory Condition

### Piece 扩展（2 个）
Card、Tile

### Reserve 扩展（4 个）
Supply、Market、Deck、Pool

Supply 的 public/player 区分由 <ownership> 字段表达（null = 公共，player = 玩家专属）。

### Player Zone 扩展（2 个）
Player Holding、Development Area

### Aid 扩展（2 个）★ 缩窄为纯参考物
Player Aid、Rulebook

注：Public Board 和 Player Board 已从 Aid 拆分至新的 Board 概念。Board 承载游戏状态（上面可放 piece/token、可容纳 zone、可携带自身 content），Aid 则缩窄为纯被动参考物——不承载状态、不放置组件、不提供 zone。详见下方「Board 概念拆分」。

### 背景概念（1 个）★
Setting

### Action 扩展（1 个）
Activation

### Event 扩展（1 + 2 新增）
Play、**Lose（新增）**、**Gain（新增）**

### Trigger 扩展（1 个，由 Event 迁移）
**Upgrade（父类从 `<event>` 改为 `<trigger>`）**

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
- **Reserve 的 public/player 区分由 ownership 表达**：`<supply>`、`<market>`、`<deck>`、`<pool>` 都通过 `<ownership>`（null = 公共，`<player>` = 玩家专属）区分公共区与个人区，不再为 public/player 单独建子类。仓库等已属于玩家的存储区仍应归类为 `<player_holding>`。
- **新增背景设定概念 `<setting>`**：用于描述游戏世界观、时代背景与关键角色，解释风味命名（如 `<favor_of_ager_track>` 中的「阿格拉」），不直接参与规则判定。
- **ontology 概念直接引用，不在游戏层重复封装**：游戏文件里已有通用概念就直接用 `<ontology::concept_id>` 或声明其实例，不新建 `<game_xxx>` 包装。
- **Board 概念拆分 ★**：`<board>` 是从 `<aid>` 中拆出的新基础概念（与 `<aid>`、`<piece>` 同级，均为 `<object>` 的子类）。核心判据：**是否承载游戏状态**。Board 承载状态——上面可以放置 piece 和 token、可以 host zone（zone 由规则定义，board 是物理宿主）、可以携带自身 content（如玩家面板上的活动图标）；Aid 不承载状态——纯被动参考物，收起来也不影响游戏。Board 不可被 transfer（区别于 piece），不可携带 effect 后被 play（同样区别于 piece）。`<public_board>` 和 `<player_board>` 的 extends 已从 `<aid>` 改为 `<board>`。
- **Zone 可由实体承载 ★**：zone 的来源不再仅限于规则——card 和 board 都可以承载 zone。例如 Arkham Horror 地点牌上的线索区、Civolution 初始芯片牌上的目标芯片区（card 承载 zone）、控制台左上角的收入芯片区（board 承载 zone）。实体承载的 zone 生命周期绑定在宿主上——宿主被移除时 zone 随之消失。这只是承认了桌游中已有的物理事实，zone 的独立逻辑定义不受影响。
- **Board 的 zones 字段替代 maps_to**：原 `<aid>` 的 `maps_to` 表达的是"视觉上画出了这些 zone"（单向弱关联）。Board 的 `zones` 表达的是"这些 zone 在我身上"（物理宿主关系），每个 zone 附带 `position` 和 `description`，供系统回答客人"放哪"类问题。
- **Module 是 effect，各等级为独立实例 ★**：模组的本质是 `<effect>`。2026-07-24 重构：每个等级的模组效果改为独立的 `<effect>` 实例——`effect_xxx_lv1`、`effect_xxx_lv2`、`effect_xxx_lv3`，各自由对应物理载体持有（L1/L2 由 tile 正反面持有，L3 由 board 持有）。升级通过 id 前缀（`effect_xxx`）保持模块 identity。
- **升级是 trigger，lose/gain 是 event ★**：`<upgrade>` 的 extends 从 `<event>` 改为 `<trigger>`——核心语义是 level 提升，继承 trigger 的 timing + condition → events[]。是否涉及 `<lose>`/`<gain>` 由游戏层决定：同一载体翻面（L1→L2）仅为 level 变化；载体切换（L2→L3）时旧载体 `<lose>` 旧 effect、新载体 `<gain>` 新 effect。
- **Lose / Gain 新增为 Event 子类 ★**：`<lose>`——`<object>` 失去一个 `<property>`（domain → null）；`<gain>`——`<object>` 获得一个 `<property>`（domain → 新实体）。用于 effect 载体切换等场景。Event 子类列表从 6 个扩充为 8 个。
- **安装是 transfer ★**：将卡牌/芯片安装到控制台 = 从 source zone transfer 到控制台上某个逻辑坐标的 zone。zone 是纯概念，不绑定物理尺寸——所以卡牌可以互相叠压而逻辑上各属各的 zone。控制台每个行列坐标就是一个 zone，有独立的 capacity。
- **实体承载的 zone 不会销毁 ★**：初始芯片牌在 setup 后仍然保有它承载的 zone——只是不再有任何规则引用它。zone 不需要 availability 概念——zone 一直在，只是规则是否引用它的区别。
- **`zones` 字段从 `<card>` 提升至 `<piece>` ★**（2026-07-25）：card、tile 都可能承载 zone，与其各自声明不如在公共父类 `<piece>` 上统一定义为 optional 字段。`<board>` 的 `zones` 独立保留（board 不是 piece）。
- **Piece 的 play 能力由 ownership 决定 ★**（2026-07-25）：并非所有 piece 都由玩家持有——归游戏系统所有（`<ownership>` 为 null）的 piece（如地图板块、遭遇牌库）不可被玩家 play。只有 `<ownership>` 归属于 `<player>` 的 piece 才可被该玩家 play。`<piece>` 定义已更新以反映此规则。
- **Civolution 专属概念移出 ontology ★**（2026-07-25）：`<encampment>`、`<site>`、`<favor_test>`、`<terrain>`、`<region>` 从 ontology 移至 Civolution 游戏层。判据：概念是否引用其他 Civolution 专属概念，或定义是否写死 Civolution 机制细节。其中 terrain/region 经讨论确认：地形类型本质是 zone 子类（`forest extends zone`），无需单独 ontology 概念；且 region 的"连续同色"定义与 Civolution 实际规则（跨板块同色仍算不同区域）矛盾。ontology 从 71 减至 66 概念。
- **Piece.parts — 物理载体与逻辑身份解耦 ★**（2026-07-25）：`<piece>` 新增 `parts` 字段（`any[]`，default null）。一块实体卡/板可能印有多种身份独立的东西——territory zone、cost、discount、prestige_point、site 都是 part。和"一种 token 代表多种资源"是同一模式：物理合一、逻辑分立。用法：纯概念引用直接用字符串 `"<territory>"`；带额外属性（position、description、type）的用对象 `{ "<score_track>": { "position": {...} } }`——概念 ID 直接做 key，无需 `as` 间接引用。替代了原先分散的 `<ontology::zone>[]` 声明——zone 现在只是 part 的一种。Civolution 和 Splendor 两款游戏已全部迁移。
- **Constraints.optional 格式精简 ★**（2026-07-25）：constraints.optional 中，字段若在当前概念自身定义（LOCAL），使用简洁字符串格式 `"optional": ["field_id"]`——描述已在字段定义处，无需重复。字段若无本地定义（CONCEPT_REF，如继承自外部概念），保留对象格式 `"optional": [{ "id": "field_id", "description": {...} }]`——description 是唯一文档来源。全 ontology 36 个 LOCAL 字段已简化，3 个 CONCEPT_REF 保留。
- **Track.slots 替代 scale ★**（2026-07-26）：`<track>` 的 `scale`（可选）改为 `slots`（必填），类型 `string | slot_spec[]`。数值轨写范围字符串（`"0–12"`），槽位轨写对象数组（`name` + `<ontology::effect>`）。统一了计分轨/进程轨与天气轨/阶段流程的表述方式。
- **Effect/Cost/Content 新增 options 字段 ★**（2026-07-26）：`<effect>`、`<cost>`、`<content>` 各新增 `options` 可选字段（`map | null`，default null）。非空时必含 `type`（引用 `<multiple_choice_enum>`）和 `items` 数组。将多选逻辑从使用处的 ad-hoc 结构提升为可复用的字段约定。
- **Multiple Choice Enum ★**（2026-07-26）：新增 `<multiple_choice_enum>` 概念（67 个概念），三个枚举值——`EXECUTE_ALL`（全部执行）、`CHOOSE_ONE`（选择 1 个）、`CHOOSE_AT_LEAST_ONE`（选择至少 1 个）。适用于任何需要多选项的场景，不限于 effect。
- **Parts 格式升级：`as` → 概念 ID 直接做 key ★**（2026-07-27）：`parts` 中带额外属性的条目从 `{ "as": "<concept>", "position": ..., "description": ... }` 改为 `{ "<concept>": { "position": ..., "description": ... } }`——概念 ID 直接做 key，去掉 `as` 间接层。纯概念引用仍用字符串 `"<concept>"`。Civolution 7 个概念 + Splendor 2 个概念共 ~37 个 part 已全部迁移。

## 为《文明演化》扩展本体的计划

第二款游戏《文明演化》的复杂度远高于 Splendor，已完成的 ontology 扩展：

- **背景与世界观**: `<setting>` — 已加入，承载创世技术学院/阿格拉考官故事。
- **轨道机制**: `<track>` — 已改为 `<zone>` 子类，`slots` 字段替代 `scale`。`<score_track>` 继承 `<track>`。
- **选择机制**: `<alternative_cost>` / `<choice>` / `<multiple_choice_enum>` — 已完成。
- **效果多选**: `<effect>`、`<cost>`、`<content>` 的 `options` 字段 — 已完成。

仍需扩展：
- **骰子机制**: `<dice>` / `<die>`、`<die_roll>`、点数修改、创意标记效果
- **升级机制**: `<upgrade>`（trigger），表达模组 level 提升；`<lose>` / `<gain>`（event），用于载体切换时的 effect 所有权转移
- **地点**: `<site>`、`<encampment>` — 已移回 Civolution 游戏层。`<terrain>` 和 `<region>` 确认无需 ontology 概念（地形 = zone 子类）。
- **被动/持续效果**: `<passive_effect>` / `<location_effect>`，表达地点在激活模组时追加的效果

扩展前应先查两个 v0 文档确认是否有对应原始概念；若无，再按当前约定新增。

**Why:** 本体是整个系统的类型系统，后续 Rule DSL、Tutorial Tree、Controller 都建立在它之上。
**How to apply:** 第一阶段世界模型定义已扩展至 67 个概念（可持续补充）。当前正在为第二款游戏《文明演化》扩展本体并编写其结构化规则。所有规则表达使用本体中定义的概念和字段。
