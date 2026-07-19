---
name: splendor-progress
description: 璀璨宝石（Splendor）结构化规则定义的当前进度
metadata:
  type: project
---

# 璀璨宝石（Splendor）规则定义进度

## 文件位置
`D:\workspace\board\games\splendor\splendor.json`

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

### 待写

- Trigger 层：贵族自动获取、回合结束弃宝石（>10 颗时）、终局触发
- Procedure 层：回合结构、Setup、终局流程

## 设计约定

- 游戏概念文件中的 `parent` 值用 `<>` 包裹（如 `"parent": "<resource>"`）
- 所有 definition/description 中引用概念用 `<concept_id>` 格式
- 叶子概念（无子类）不需要 `constraints` 块
- 通用概念优先入 ontology，游戏专属概念放在游戏文件中
- ontology 概念的游戏专属值用顶层 key 引用（如 `"<hand>": { ... }`、`"<starting_player_marker>": { ... }`）
- 顶层引用只声明游戏专属值，不重复定义本体已有的字段
- **条件进 precondition，行为内化进 description**——合法性/可执行条件（如「手牌未达上限」「黄金供应堆非空」）写 precondition；事件做什么（含补牌、数量计算、可见性）内化到 event 的 description；`rules` 自由文本字段仅保留给真正无法结构化的约束（当前 Action 层已全部清空 rules）

**Why:** 追踪 Splendor 规则定义的进度，新会话无需重新遍历文件。
**How to apply:** 继续编写 Action 层，然后是 Trigger 层，最后是 Procedure 层。
