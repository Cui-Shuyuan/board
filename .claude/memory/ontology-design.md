---
name: ontology-design
description: 桌游本体 JSON 的设计约定、关键决策和当前进度（截至 2026-08-03，66 个概念——Declaration 已弃用）
metadata:
  node_type: memory
  type: project
  originSessionId: fbc4a732-5c5a-482a-a184-077fdaab9f56
---

# 桌游本体设计

## 文件位置
D:\workspace\board\ontology\concepts.json（统一本体，67 个概念）

## 统一本体
最初分为 Structure Ontology（静态结构）和 Procedure Ontology（流程时序）两个文件，后合并。JSON 给程序读，不考虑 LLM 上下文长度。

## 参考文档
项目根目录下两个 v0 文档定义了原始概念列表，是 JSON 本体的权威来源：
- `Board Game Structure Ontology v0.md` — 静态结构概念
- `Board Game Procedure Ontology v0.md` — 流程时序概念

## 基础约定
- **ontology 纯净原则 ★（2026-08-11 用户定稿）**：ontology 是几十上百款游戏的通用基础，定义中不得出现具体游戏的例子或专名（游戏名、游戏专属概念、具体路径如 `<phase_sequence>.<slot>`）。需要举例时用占位符（`<concept>`、`<track>.<part_id>` 等——占位符须在校验脚本 placeholder_refs 内）或纯抽象表述；游戏具体内容一律放游戏层。
- 每个概念都有 `id`、`name`（中英双语）、`abstract`、`definition`、`constraints`
- **字段声明在概念顶层**（不再嵌套在 `fields` 内） 如 `"owner": { "type": "player | null", "default": null, ... }`。字段名即 JSON key，声明内容包括 `type`、`description`、可选的 `default`
- **`constraints.required`** = 子类必须实现的字段 ID 列表。格式为 `[{ "id": "<field_id>", "description": { "zh": "...", "en": "..." } }]`
- **`constraints.optional`** = 子类可选实现的字段 ID 列表，格式同上
- **引用条目 description 非强制 ★（2026-08-11 定稿）**：只要是引用（CONCEPT_REF 或 `<concept_id>` 格式），description 就不是强制性的——同一概念被不同概念引用会变成不同的东西，此时 description 有必要（说明该引用语境下的含义）；同义引用（如 research_card 的 required 列出 `<research_ability>`/`<cost_space>` 等 part）直接写纯引用字符串即可。LOCAL 字段仍用字符串格式（描述在字段定义处）
- **`id` 字段特殊处理**：由 Object 定义，所有实例隐式拥有。因与概念自身的 `"id"` 元数据冲突，不在顶层声明，仅保留在 Object 的 constraints 中
- **`extends` / `specifies` / `instance_of`** 三种层级关系：`extends`=结构扩展（加新字段）、`specifies`=参数绑定（填已有字段）、`instance_of`=具体个体（字段全满）。三者均可链化——extends 和 specifies 可任意深度交替，instance_of 是唯一终端。基础概念无 `extends`
- `abstract: true` = 基类，不可直接实例化

## 类型与引用约定
- **type 值三类**：(1) 概念引用 — 使用 `<concept_id>` 格式包裹，可带 `| null`；(2) 泛引用 — `concept_ref`（已弃用）；(3) 原始类型 — string、integer、boolean、any、map<string, any>、enum
- **`<concept_id>` 交叉引用**：所有 definition 和 description 中用 `<concept_id>` 包裹概念引用
- **字段 key 即类型**：当字段 key 本身是 `<concept_id>` 格式时（如 `"<ownership>"`、`"<declaration>"`），不再重复声明 `type` 字段——key 自身即表达了类型。**数组标记直接写在 key 里**（`"<event>[]"`、`"<condition>[]"`），不用 `"<event>": {"type": "<event>[]"}` 这种重复声明。仅当 key 是语义化命名（如 `current_player`）、需要窄化类型（如 `"<ownership>": {"type": "<player>"}`）时才保留 `type`
- **不写 `| null` 后缀（2026-08-09 修正）**：字段可空性由约束语境表达——`constraints.optional` 或 `default: null` 本身就代表可空，`type` 中不写「`| null`」。此前的旧约定「允许空值（如 `"<cost>": {"type": "<cost> | null"}`）时才保留 type」已废弃
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

### Action 三件套 → 统一 Trigger 模型 ★★（2026-08-03）

**Declaration 已弃用。** Action 和 Effect 均 specifies Trigger，三者共享完全相同的结构（condition → cost → target → content），区别仅在于点火源和称呼习惯：
- **纯 Trigger**：condition 边沿激活，规则自动执行
- **Effect**：specifies trigger，绑定在 piece 上、由 condition 边沿激活
- **Action**：specifies trigger，不依赖 piece、由 player 决策点燃

判据：不依赖 piece + 由玩家做出 = action；依赖 piece + 由状态边沿点燃 = effect。
Action 的 content 槽位可以嵌套 action/effect/任意 content——嵌套不改变被嵌套概念的类型。

### Event 新增字段 ★
- **precondition**：`<condition>[]`，optional。事件发生前必须满足的条件列表
- **rules**：`map<string, string>`，optional。中英双语描述超越结构化字段的执行规则约束

### Trigger 的递归结构 ★（2026-07-30 重构 + 语义修正）
Trigger 的本质是「**condition + cost + content**」的递归调度器：
- `<condition>`——触发门槛：**对 game state 的谓词**，任意时刻可求值、无副作用。trigger 对它**按边沿评估**（谓词由假变真的瞬间激活）。timing 已融入 condition（如「回合结束时」本身就是一个瞬间谓词）。
- `<cost>`——可选代价（null = 无需支付）。cost 自身含 `<condition>`（支付资格）和 `<event>`（实际执行的 transfer）。
- `<content>`——cost 支付后触发的内容：类型为 **`<trigger> | <content>`**（不再是 `| <event>`），指向另一个 `<trigger>`（递归下一层）或终结的 `<content>`——**`<instant_content>`（一次性 resolve 执行，链条终结）或 `<continuous_content>`（进入生效池按条件电平维持）**。类型上保证递归必然终结。
- `target`——可选，作用目标对象。
- 因果链：Action → Trigger（condition 边沿跳变 → cost 支付 → content 触发）→ 递归或终结于 Content。

### 边沿/电平语义二分 ★★（2026-07-30，光环讨论）
「所有效果都由 trigger 触发」结论被证明武断——光环效果（「只要此牌在发展区，手牌上限+1」）无法用触发解释。修正为：**所有效果由 trigger 结构统一描述，执行语义由终结 content 的类型决定**：
- **`<instant_content>`**（由 `<instant_effect>` 改名）——边沿语义：condition 假→真时经 `<resolve>` 一次性实例化为 `<event>` 执行。event 降为它 resolve 时引用的运行时发生，不再是链条终结类型。
- **`<continuous_content>`**——电平语义：激活后进入「生效池」，按 `active_condition` 维持（条件成立即适用、不成立即停止、再成立再适用）。**派生值 = 基础值 + Σ 生效池命中的修饰，查询时现算**——来源离场无需任何「反向触发」，条件变假修饰自动消失。`active_condition` 为 null 时沿用所属 trigger 的 condition（标准光环坍缩为一个谓词：边沿=「进入」，电平=「在场」）；非 null 覆盖（「打出后永久生效」：condition=打出瞬间谓词，active_condition=永远）。
- 光环的 condition 必须是**状态事实**（「此牌在发展区」），不能绑定 action（「打出此牌」）——否则犯进场枚举错误（被其他效果移入发展区时光环不亮），与离场枚举错误对称。
- **分类判据（2026-07-31）**：「每次 X 发生时……」类效应**不是** continuous_content——「每次」说明内容是离散事件，应建模为 instant_content + 含电平合取项的 trigger condition（武装门电平 + 开火边沿，如「此牌在场 ∧ 其他玩家刚打出一张牌」；已发生的 transfer 作为历史留在 state 中，不因来源离场回滚）。判据：**内容写历史（不可撤销的状态变更）→ instant；参与计算（派生值修饰）→ continuous**。真正的 continuous_content 没有「每次」，只有「只要」。已写入 `<continuous_content>` / `<instant_content>` 定义。

### Transfer 字段改名 ★
`what` → `<object>`，与其他概念引用 key 统一。

### Ownership 归属模型
Ownership extends State。三种来源：Zone 推导、固有归属、游戏中获取。变更须经 Effect 或 Transfer 触发。

### Event 体系与因果关系链 ★（2026-08-03 重构）
- **Trigger** 是体系核心：condition → cost → target → content 的可执行规格。
- **Effect** specifies Trigger：绑定在 piece 上，由 condition 边沿激活。含 instant_effect 和 continuous_effect。
- **Action** specifies Trigger：不依赖 piece，由 player 决策点燃。自身不声明独有字段。
- **Activation** specifies Action：将 target 窄化为「选哪个 effect」。
- 三者共享同一台机器，区别如同 card 和 tile——称呼习惯不同，本质相同。
- 因果链：Trigger（condition 边沿跳变 → cost 支付 → content 触发）→ 递归或终结于 Content
- **Resolve**：将 content 实例化为真实 Event
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

### Procedure 结构统一 ★（2026-08-09）
Round、Turn、Phase 自由嵌套，无固定层级。Phase 是唯一承载「规则上下文」的 Procedure（**不是必包层——只有 1 个 phase 时省略不写**）。**三个子类结构相同：多步骤执行用 `<ontology::pipeline>` 定义，仅干一件事时直接持有对应概念（如 `<ontology::action>`），不套 pipeline**（2026-08-09 修正：pipeline 是描述多 trigger/多步骤的工具，不是必包层；详见 [[pipeline-model]]）——`children` 数组、`actor`、`start`/`end` 字段全部废弃：顺序与依赖用 options + do_after，「谁能做」用 condition（步骤级不成立 = 阻断，候选级不成立 = 排除），行动权轮转是 turn 的内建语义。
**round 执行次数用 pipeline 的 `loop.count`**（2026-08-09 定稿：与 `loop.until` 对称，均挂在 pipeline 上；缺省无 loop = 1 次 = 每位玩家按座次各行动一轮；「进行 4 轮」= `"loop": { "count": 4 }`。字段名不叫 rounds——「进行 4 轮」的「4」是次数；「进行几轮」不是 round 的属性字段，是 loop 的定次形态）。**次数未知、直到条件才结束的用 `loop.until`**（如 Splendor main_gameplay「一直进行到有人 15 分」）。

## 当前进度（核心概念持续扩展中）

本体已从最初的 51 个概念扩展到 **82 个概念**（2026-08-11：+player_supply/public_supply；删 alternative_cost/choice/temporary_zone——零引用弃用）。

### E11 驱动的 required 归属调整 ★（2026-08-11）

`validate_rules.py` 的 E11 检查（每条继承链至少一个节点实现 required 字段）驱动了 ontology 大调整：

- **zone 回归纯粹**：required `[id, contains, <ownership>, <information_visibility>]` → `[id]`。`<ownership>` 完全移除；contains / `<information_visibility>` 降 optional（有默认语义）
- **新增 `<player_supply>` / `<public_supply>`**（extends `<supply>`）：各声明 `<ownership>` 一次，具体供应堆 specifies 它们——ownership 实现收敛（civolution dice_display/hunting_token_display/hundred_point_token_display + splendor gem_supply/gold_supply 已迁移到 public_supply）
- **appearance**（piece/board/aid/marker/component 的 required）→ optional（外观是描述性信息，全链从未实现）
- **subject**（state）→ optional（ownership 用 owner 表达同一语义）；**domain**（property）→ optional（已被 parts/绑定替代）
- **score_track 补 slots**、**top_draw/random_draw 补 source/destination/`<object>`**（实现父类 required）
- 删除弃用概念：alternative_cost、choice、temporary_zone（ontology）；gold_available（splendor）

### 基础概念（8 个）
Object、Zone、State、Property、Event、Condition、Timing、Procedure

注：`<timing>` 已于 2026-07-30 合并进 `<condition>`——"回合结束时"本身就是一个条件。概念保留但不再独立出现在 trigger 字段中。

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

### 属性概念（5 个）★ Declaration 已弃用
Cost、Content、Effect、Information Visibility、**Substitution（新增）**

### Field-level 概念（6 个）★ 2026-08-06 补全
Instant Cost（extends Cost）、Continuous Cost（extends Cost）、Instant Effect（extends Effect）、Continuous Effect（extends Effect，含 end_condition）、Instant Content（extends Content）、Continuous Content（extends Content，含 active_condition）

### 求值与检定（2 个）★ 2026-08-06 新增
Evaluate（extends Trigger，产出 result）、Check（specifies Evaluate，result 特化 pass/fail）

### 状态变更（2 个）★ 2026-08-06 新增
State Change（extends Event，subject + to + optional attribute/from）、Flip（specifies State Change，attribute 固定 face）

### 区域（1 个）★ 2026-08-06 新增
Temporary Zone（extends Zone，多步骤瞬时中间态，操作完成后必须清空）

### 区域概念（3 个）
Reserve（abstract）、Discard Pile、Player Zone（abstract）

### 区域/轨道扩展（2 个）★ Track 改为 Zone 子类，slots 替代 scale
Track（extends Zone）、Score Track（extends Track）

`<track>` 的 `scale` 可选字段已改为 `slots` 必填字段（`string | slot_spec[]`）。数值轨写取值范围字符串（`"0–12"`），槽位轨写对象数组（每个 slot 含 `name` + `<ontology::effect>`）。

### 事件概念（4 + 3 新增）
Action、Trigger、Resolve、Shuffle、**Lose（新增）**、**Gain（新增）**、**Push Track（新增）**

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

### State Change 扩展（1 个）
**Upgrade（specifies `<state_change>`，attribute 固定为 level）**

### Content 扩展（2 个）★ 改名
Instant Content（由 Instant Effect 改名）、Continuous Content（新增 active_condition 可选字段）

### Token 扩展（1 个）
Starting Player Marker

### Player Holding 扩展（1 个）
Hand

## 关键设计决策更新

- **pipeline 是唯一结构原语 ★（2026-08-09）**：`<procedure>`（round/turn/phase）统一持有 `<ontology::pipeline>` 定义流程；`children`/`actor`/`start`/`end` 废弃。`<pipeline>` 新增 `loop` 字段（`for N + counter` 定次 / `until` 条件，until 可字符串引用或内联谓词）。**步骤级 vs 候选级 condition 语义**：步骤级不成立 = 阻断（do_after 链停止）；候选级不成立 = 排除（不影响其他候选）。「能 A 必须 A，否则跳过」= 跳过作为带「A 不可用」condition 的候选。详见 [[pipeline-model]]。
- **Token 作为 Resource 与 Marker 的物理基类**：`<token>` 是桌游中最常见的小型计数/标记物；`<resource>` 继承 `<token>`（作为可被消耗的价值物），`<marker>` 继承 `<token>`（作为状态/位置指示物）。
- **Track 是 Zone 的子类**：轨道不再属于 `<aid>`，而是一种「有序 zone」，其中 marker 只做内部位置移动，不发生 zone 间 transfer。
- **Score Zone 已移除**：`<score_track>` 自己就是 zone，不再需要单独的 `<score_zone>`。
- **Reserve 的 public/player 区分由 ownership 表达**：`<supply>`、`<market>`、`<deck>`、`<pool>` 都通过 `<ownership>`（null = 公共，`<player>` = 玩家专属）区分公共区与个人区，不再为 public/player 单独建子类。仓库等已属于玩家的存储区仍应归类为 `<player_holding>`。
- **新增背景设定概念 `<setting>`**：用于描述游戏世界观、时代背景与关键角色，解释风味命名，不直接参与规则判定。**ontology 只留抽象定义，游戏背景放游戏层实例**（2026-08-11 修正——ontology 是通用基础，不得出现具体游戏的例子/专名；civolution 背景在 `<civolution_setting>` 实例）。
- **ontology 概念直接引用，不在游戏层重复封装**：游戏文件里已有通用概念就直接用 `<ontology::concept_id>` 或声明其实例，不新建 `<game_xxx>` 包装。
- **Board 概念拆分 ★**：`<board>` 是从 `<aid>` 中拆出的新基础概念（与 `<aid>`、`<piece>` 同级，均为 `<object>` 的子类）。核心判据：**是否承载游戏状态**。Board 承载状态——上面可以放置 piece 和 token、可以 host zone（zone 由规则定义，board 是物理宿主）、可以携带自身 content（如玩家面板上的活动图标）；Aid 不承载状态——纯被动参考物，收起来也不影响游戏。Board 不可被 transfer（区别于 piece），不可携带 effect 后被 play（同样区别于 piece）。`<public_board>` 和 `<player_board>` 的 extends 已从 `<aid>` 改为 `<board>`。
- **Zone 可由实体承载 ★**：zone 的来源不再仅限于规则——card 和 board 都可以承载 zone。例如 Arkham Horror 地点牌上的线索区、Civolution 初始芯片牌上的目标芯片区（card 承载 zone）、控制台左上角的收入芯片区（board 承载 zone）。实体承载的 zone 生命周期绑定在宿主上——宿主被移除时 zone 随之消失。这只是承认了桌游中已有的物理事实，zone 的独立逻辑定义不受影响。
- **Board 的 zones 字段替代 maps_to**：原 `<aid>` 的 `maps_to` 表达的是"视觉上画出了这些 zone"（单向弱关联）。Board 的 `zones` 表达的是"这些 zone 在我身上"（物理宿主关系），每个 zone 附带 `position` 和 `description`，供系统回答客人"放哪"类问题。
- **Module 是 effect，各等级为独立实例 ★**：模组的本质是 `<effect>`。2026-07-24 重构：每个等级的模组效果改为独立的 `<effect>` 实例——`effect_xxx_lv1`、`effect_xxx_lv2`、`effect_xxx_lv3`，各自由对应物理载体持有（L1/L2 由 tile 正反面持有，L3 由 board 持有）。升级通过 id 前缀（`effect_xxx`）保持模块 identity。
- **升级是 state_change ★**（2026-08-08 修正）：`<upgrade>` specifies `<state_change>`——核心语义是 object 的 level 属性从 A 变为 B（subject=被升级对象，attribute 固定为 level，to=目标等级）。曾两次调整父类：最初 extends `<event>`，2026-07-24 因「level 提升由规则自动触发」改为 extends `<trigger>`；引入 `<state_change>` 后确认升级本质是属性值变更（与 `<flip>` 同类——翻转是 face 变更、升级是 level 变更），最终改为 specifies `<state_change>`。是否涉及 `<lose>`/`<gain>` 由游戏层决定：同一载体翻面（L1→L2）仅为 level 变化；载体切换（L2→L3）时旧载体 `<lose>` 旧 effect、新载体 `<gain>` 新 effect。物理操作（翻面、放回游戏盒）是 level 变化的物理后果，由游戏层表达（游戏层 `upgrade_main_module` 已改为 specifies `<ontology::upgrade>`）。
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

### Substitution — 替代/视为语义 ★（2026-08-09 新增）

`<substitution>` specifies `<property>`——声明对象 X 在特定语境中充当对象 Y：物理身份不变、scope 内且满足 condition 时行为等同于 Y、离开语境恢复自身。**与 `<piece>.parts` 互补**：parts = 静态身份声明（载体上印着什么身份），substitution = 使用时的身份借用（动态替代）。

- 结构三要素：`target`（充当的对象）、`scope`（生效范围）、`<condition>`（适用条件，复用已有概念）。target/scope 是 substitution 的内部字段（不做顶层概念）
- **scope 用路径式定位**：`"<activate_module>.<ontology::pipeline>.pay_cost"`（概念.字段.步骤，路径中 ontology 概念必须带 namespace）——步骤 id 全局可寻址，指向 trigger 执行流程（ontology/flow.json 的 `<trigger_pipeline>`）或游戏层 pipeline 中的具体步骤。null = 全场合
- **挂载位置在对象定义处，不在使用处**：如 civolution 的 `planning_marker`/`focus_marker` 各自声明「支付激活模组费用时可视为 `<activation_die>`」；使用处（activate_module 的 pay_cost 步骤）只引用模组 cost，替代由 substitution 提供。一条规则一处定义，不在每处重复
- **游戏层差异按需组装**：替代候选是游戏层声明，不进使用处——其他游戏没有替代物就不写
- `<trigger>` 概念新增顶层 `<pipeline>` 字段（default 引用 trigger_pipeline），标准执行流程四步：evaluate_condition → pay_cost → select_target → resolve_content。此前 trigger 只在 description 文字提到「按 pipeline 依次结算」，未结构化引用——已补
- 触发词识别：描述「视为」「相当于」「可替代」「当作」的规则都是 substitution 的候选场景（如 Splendor gold 的百搭语义）

**Why:** 替代/视为是跨游戏通用机制（wild/百搭/替代物），不进 ontology 就会在每款游戏里以自然语言散落（description 化残留），LLM 无法结构化理解。
**How to apply:** 写替代类规则时：声明在对象定义处（`<ontology::substitution>` 字段），scope 写路径式定位指向具体步骤，condition 写适用限制。不在使用处重复。

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
- **被动/持续效果**: 已通过边沿/电平语义二分处理——continuous content 进入生效池按 active_condition 电平维持（见上方「边沿/电平语义二分」）。passive_effect 已删除，统一为 trigger 模型。

扩展前应先查两个 v0 文档确认是否有对应原始概念；若无，再按当前约定新增。

**Why:** 本体是整个系统的类型系统，后续 Rule DSL、Tutorial Tree、Controller 都建立在它之上。
**How to apply:** 第一阶段世界模型定义已扩展至 67 个概念（可持续补充）。当前正在为第二款游戏《文明演化》扩展本体并编写其结构化规则。所有规则表达使用本体中定义的概念和字段。

## effect 生命周期模型（2026-08-12 用户定稿）

**四阶段**：部署（arm）→ 监听（monitor）→ 触发（fire）→ 结算（cost → target → content）。

**instant vs continuous 的真正区别**：部署与触发是否同一时刻——
- `<instant_effect>`：**部署即触发**——武装的那一刻就是触发机会（一次机会，错过不再；content 的结算结果如电平不随 effect 消失）
- `<continuous_effect>`：**先部署后触发**——武装时开始监听（此刻不触发）；此后每次 condition 满足才触发——走完整 trigger 流程（cost → target → content，拥有自己的 cost 与 target），直到 end_condition / 来源离场 / 外部移除

**触发方式 × 结算方式的组合约束**：
- 激活式电平（一次触发 + `<continuous_content>`）——主流持续效果形态：部署时电平即生效，之后不再触发，active_condition 判定适用性
- 监听式事件（持续触发 + `<instant_content>`）——条件满足反复开火结算
- **「监听 + 电平」结构性空洞**（`<continuous_effect>` 挂 `<continuous_content>`）：电平不需要监听（激活 + 条件判定就够），监听只对事件有意义——此组合禁用

**效果归属维度**（与触发/结算正交）：附属于 piece（随来源生命周期，来源离场/沉默即失效）vs 独立注册（不随来源，注册到效果表后永久存在）——由挂载位置隐式表达（挂 piece 的 parts vs 独立定义）。

**讨论例子（炉石传说的映射，仅供理解，勿入 ontology）**：
- 战场指挥官（光环：其他随从 +1/+1，离场失效）＝ 附属 + 激活 + 电平（active_condition = 来源在场 & 其他随从）
- 手牌上限 +1 ＝ 附属 + 激活 + 电平（无条件）
- 盔甲（受攻击时支付 1 体力减免 1 伤害）＝ 附属 + 监听 + 事件——打出盔甲 = 部署（开始监听），受攻击 = 触发（cost → content）
- 你受到的伤害 -1 ＝ 附属 + 监听 + 事件（每次受伤害开火结算）
- 首席管理官奥丁（战吼：本局内获得护甲后当回合等量攻击力）＝ 独立 + 监听 + 事件（condition 含「已战吼」状态，不挂来源所以离场不影响）
