---
name: splendor-progress
description: 璀璨宝石（Splendor）结构化规则定义的当前进度
metadata:
  type: project
---

# 璀璨宝石（Splendor）规则定义进度

## 文件位置
`D:\workspace\board\games\splendor\concepts.json`（概念层，已完成）
`D:\workspace\board\games\splendor\flow.json`（流程层，结构讨论中）

## 文件结构
按类别分组，不再混在一个 concepts 数组里：
- `objects`（22 个）——resource / content / card / tile / zone 等静态概念
- `actions`（4 个）——parent 为 `<action>`
- `triggers`（3 个）——parent 为 `<trigger>` 的独立 trigger
- `conditions`（13 个）——**所有 condition 统一定义于此**，使用处只写纯引用（trigger 的 `<condition>` 为字符串、precondition 为单元素数组）：gems_available_any / gems_available_same_color / card_purchasable / card_reservable / gold_available / exceed_gem_limit / noble_satisfied / action_declaration_legal（所有内嵌 trigger 共用的通用绑定条件）/ no_action_available（复合：四行动条件取反求与）/ reach_15_prestige（extends `<endgame_condition>`）/ highest_prestige_wins（extends `<victory_condition>`）/ prestige_highest / fewest_development_cards（判胜用的两条单玩家判定式）
- 顶层引用——`<player_holding>` / `<development_area>` / `<hand>` / `<starting_player_marker>`
- 未来 Procedure 层新增 `procedures` 组

## 当前进度

### 已完成

**Resource 层**（`<resource>` 子类）：
- `<gem>` → diamond / sapphire / emerald / ruby / onyx（五色宝石，poker-chip 圆形厚片）
- `<gold>`（黄金，百搭，仅保留卡牌获得）
- `<prestige_point>`（声望点数，unit="分"）

**Content 层**（`<continuous_content>` 子类）：
- `<discount>`（折扣，target=self，gem_color 决定减免颜色）

**Card 层**（`<card>` 子类）：
- `<development_card>`（发展卡，含 appearance/level/cost/discount/prestige_point）
- development_card_level_1 / 2 / 3（三级窄化）

**Tile 层**（`<tile>` 子类）：
- `<noble>`（贵族板块，requirement=发展卡颜色数量条件，固定 3 分）

**Zone 层**：
- `gem_supply`（extends `<supply>`，contains=`<gem>[]`）
- `gold_supply`（extends `<supply>`，contains=`<gold>[]`）
- `development_deck` → level_1 / 2 / 3（extends `<deck>`，contains=对应等级发展卡）
- `noble_market`（extends `<market>`，contains=`<noble>[]`，被动触发非主动购买）
- `card_market`（extends `<market>`，contains=`<development_card>[]`，3 组×4 张按等级陈列，买走/保留后从对应牌堆顶部补牌）

**Action 层**：
- `take_gems_different`（extends `<action>`，取 1~3 颗不同色宝石，数量由供应情况决定）
- `take_gems_same`（extends `<action>`，取 2 颗同色宝石，前提是该色存量 ≥4）
- `purchase_development_card`（extends `<action>`，购买发展卡。declaration 含 card+payment；trigger 含 2 个 event：`<transfer>` 支付（gem→gem_supply、gold→gold_supply，逐色 max(0, cost−discount)，gold 百搭）+ `<play>`（卡从 market/hand → development_area，market 来源则补牌））
- `reserve_development_card`（extends `<action>`，保留发展卡。declaration 二选一：card=明面保留 / deck=暗面保留；trigger **ordered=false** 含 2 个 event：卡→hand（market 来源则补牌）+ gold_supply→player_holding 拿 1 gold（precondition：supply 非空，空则跳过）。hand 上限 3）

**Action 层已完成（4 个）**：take_gems_different、take_gems_same、purchase_development_card、reserve_development_card

**顶层引用**：
- `<hand>`（contains=`<development_card>[]`，capacity=3，private）
- `<player_holding>`（contains=`<gem>[] | <gold>[]`，capacity=10，public）
- `<development_area>`（contains=`<development_card>[] | <noble>[]`，无上限，public）
- `<starting_player_marker>`（游戏专属外观：两个零件拼成钻石形状）

**Trigger 层**：
- `discard_excess_gems`（extends `<trigger>`，独立 trigger 不绑定 action。timing=使宝石总数变化的 event 结算完成的瞬间（**超限时立即触发**，非回合结束），condition=holding 中 gem+gold 总数 >10；单 transfer 将超出部分返回供应堆，颜色由该玩家自选）
- `attract_noble`（extends `<trigger>`。condition=回合刚结束且 noble_market 中至少一枚 noble 的 requirement 被 development_area 满足；单 transfer noble→development_area，多枚满足时玩家择一、每回合一枚）
- `enter_endgame`（extends `<trigger>`。timing=回合结束时；condition 引用 `reach_15_prestige`；event 为描述性「进入终局流程」，具体流程留给流程文档）
- `skip_turn`（extends `<trigger>`。timing=回合开始、宣告 action 前；condition 引用 `no_action_available`；event=回合直接结束进入下一玩家，回合末 trigger 照常检查）
- `no_action_available`（extends `<condition>`，复合条件：gems_available_any / gems_available_same_color / card_purchasable / card_reservable 四者取反求与，params 声明 op=and + not operands）
- `reach_15_prestige`（extends `<endgame_condition>`，conditions 组。任意玩家声望 ≥15，params={threshold:15}）
- `highest_prestige_wins`（extends `<victory_condition>`，conditions 组。形式化为 `"<condition>": ["<prestige_highest>", "<fewest_development_cards>"]`——按序满足的玩家为胜者，允许多平局）

**Trigger 层已完成（4 个）**：discard_excess_gems、attract_noble、enter_endgame、skip_turn

### 待写

- Procedure 层：回合结构、Setup、终局流程（**先写专门的流程文档**，用形式化语言描述一局游戏从开始到结束的完整流程，再回头落到 `procedures` 组）

## 设计约定

- 游戏概念文件中的 `parent` 值用 `<>` 包裹（如 `"parent": "<resource>"`）
- 所有 definition/description 中引用概念用 `<concept_id>` 格式
- 叶子概念（无子类）不需要 `constraints` 块
- 通用概念优先入 ontology，游戏专属概念放在游戏文件中
- ontology 概念的游戏专属值用顶层 key 引用（如 `"<hand>": { ... }`、`"<starting_player_marker>": { ... }`）
- 顶层引用只声明游戏专属值，不重复定义本体已有的字段
- **条件进 precondition，行为内化进 description**——合法性/可执行条件写 precondition（并引用 conditions 组）；事件做什么（含补牌、数量计算、可见性）内化到 event 的 description；`rules` 自由文本字段仅保留给真正无法结构化的约束（当前 Action 层已全部清空 rules）
- **所有 condition 抽入 conditions 组统一定义，使用处只留纯引用**——trigger 的 `<condition>` 写 `"<condition>": "<x>"` 字符串引用；precondition 写 `"precondition": ["<x>"]` 单元素数组（ontology 中 Event.precondition 类型为 `<condition>[]`）。所有细节（declaration 合法性、跳过语义、上限说明）都写进 conditions 组的具体 condition 定义里，引用处零描述
- **内嵌 trigger 用「此`<action>`」表述**——不点名具体 action（避免复制粘贴隐患）。timing=「此 action 执行完毕时」并注明 trigger 绑定此 action 的 declaration、二者合起来构成完整 action；condition=「此 action 的 precondition 满足且 declaration 合法」。独立 trigger 的 timing/condition 描述具体游戏事实
- **trigger 的激活时机由 `<timing>` 字段显式表达，condition 只写纯状态事实**——如 discard_excess_gems：`<timing>`=「使宝石总数变化的 event 结算完成的瞬间」+ condition=「总数 >10」；attract_noble：`<timing>`=「回合结束时」+ condition=「noble requirement 被满足」。时机不同保证不会同时触发；内嵌 trigger 的 `<timing>`=「此 action 执行完毕时」+ 绑定说明，condition=「此 action 的 precondition 满足且 declaration 合法」

**Why:** 追踪 Splendor 规则定义的进度，新会话无需重新遍历文件。
**How to apply:** Action 层和 Trigger 层已完成。下一步：先写流程文档（形式化描述一局游戏从 setup 到终局判胜的完整流程），再落 Procedure 层到 `procedures` 组。
