---
name: splendor-progress
description: 璀璨宝石（Splendor）结构化规则定义的当前进度
metadata:
  type: project
---

# 璀璨宝石（Splendor）规则定义进度

## 文件位置
`D:\workspace\board\games\splendor\concepts.json`（概念层，已完成）
`D:\workspace\board\games\splendor\flow.json`（流程层，**已完成**）

## 文件结构
按类别分组，不再混在一个 concepts 数组里：
- `objects`（21 个）——resource / content / card / tile / zone 等静态概念
- `actions`（4 个）——specifies `<ontology::action>`
- `triggers`（4 个）——specifies `<ontology::trigger>`
- `conditions`（13 个）——**所有 condition 统一定义于此**，使用处只写纯引用（trigger 的 `<condition>` 为字符串、precondition 为单元素数组）：gems_available_any / gems_available_same_color / card_purchasable / card_reservable / gold_available / exceed_gem_limit / noble_satisfied / action_declaration_legal（所有内嵌 trigger 共用的通用绑定条件）/ no_action_available（复合：四行动条件取反求与）/ reach_15_prestige（extends `<endgame_condition>`）/ highest_prestige_wins（extends `<victory_condition>`）/ prestige_highest / fewest_development_cards（判胜用的两条单玩家判定式）
- 顶层引用——`<player_holding>` / `<development_area>` / `<hand>` / `<starting_player_marker>`
- `flow.json` 流程层——`procedures` 组（game round → setup / main_gameplay / endgame phases）
- 未来 Procedure 层新增 `procedures` 组

## 当前进度

> **2026-07-26 重构**：`parent` 已拆分为 `extends` / `specifies` / `instance_of` 三种关系。Splendor 中 6 个概念使用 extends（gem、development_card、development_deck、card_market、discount、noble），38 个概念使用 specifies（gem 颜色变体、发展卡等级、所有 action/trigger/condition 等）。后端代码零改动。

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

**Zone 层**（`<zone>` 子类）：
- `gem_supply`（extends `<supply>`，ownership=null，contains=`<gem>[]`）
- `gold_supply`（extends `<supply>`，ownership=null，contains=`<gold>[]`）
- `development_deck` → level_1 / 2 / 3（extends `<deck>`，contains=对应等级发展卡）
- `noble_market`（extends `<market>`，contains=`<noble>[]`，被动触发非主动购买）
- `card_market`（extends `<market>`，contains=`<development_card>[]`，3 组×4 张按等级陈列，买走/保留后从对应牌堆顶部补牌）
- `<game_box>` 已入 ontology（extends `<reserve>`），作为「当前未进入游戏流程的 object 的 zone」，setup 时从中分发组件，游戏中也可能接收被移出流程的 object

**Action 层**：
- `take_gems_different`（extends `<action>`，取 1~3 颗不同色宝石，数量由供应情况决定）
- `take_gems_same`（extends `<action>`，取 2 颗同色宝石，前提是该色存量 ≥4）
- `purchase_development_card`（extends `<action>`，购买发展卡。declaration 含 card+payment；trigger 含 2 个 event：`<transfer>` 支付（gem→gem_supply、gold→gold_supply，逐色 max(0, cost−discount)，gold 百搭）+ `<play>`（卡从 market/hand → development_area，market 来源则补牌））
- `reserve_development_card`（extends `<action>`，保留发展卡。declaration 二选一：card=明面保留 / deck=暗面保留；trigger **ordered=false** 含 2 个 event：卡→hand（market 来源则补牌）+ gold_supply→player_holding 拿 1 gold（precondition：gold_supply 非空，空则跳过）。hand 上限 3）

**Action 层已完成（4 个）**：take_gems_different、take_gems_same、purchase_development_card、reserve_development_card

**顶层引用**：
- `<hand>`（contains=`<development_card>[]`，capacity=3，private）
- `<player_holding>`（contains=`<gem>[] | <gold>[]`，capacity=10，public）
- `<development_area>`（contains=`<development_card>[] | <noble>[]`，无上限，public）
- `<starting_player_marker>`（游戏专属外观：两个零件拼成钻石形状）

**Trigger 层**（event 顺序统一用 `do_after` 表达依赖，无 `do_after` 的事件互相独立）：
- `discard_excess_gems`（extends `<trigger>`，独立 trigger 不绑定 action。timing=使宝石总数变化的 event 结算完成的瞬间（**超限时立即触发**，非回合结束），condition=holding 中 gem+gold 总数 >10；单 transfer 将超出部分返回供应堆，颜色由该玩家自选）
- `attract_noble`（extends `<trigger>`。condition=回合刚结束且 noble_market 中至少一枚 noble 的 requirement 被 development_area 满足；单 transfer noble→development_area，多枚满足时玩家择一、每回合一枚）
- `enter_endgame`（extends `<trigger>`。timing=回合结束时；condition 引用 `reach_15_prestige`；event 为描述性「进入终局流程」，具体流程留给流程文档）
- `skip_turn`（extends `<trigger>`。timing=回合开始、宣告 action 前；condition 引用 `no_action_available`；event=回合直接结束进入下一玩家，回合末 trigger 照常检查）
- `purchase_development_card` 的内嵌 trigger：`pay_cost` event 与 `play_card` event，`play_card` 声明 `do_after: ["pay_cost"]`
- `reserve_development_card` 的内嵌 trigger：`reserve_card` event 与 `take_gold_bonus` event，二者互相独立，均无 `do_after`

**Trigger 层已完成（4 个）**：discard_excess_gems、attract_noble、enter_endgame、skip_turn

**Conditions 层**（13 个核心条件 + 2 个 flow 控制条件）：
- `gems_available_any` / `gems_available_same_color` / `card_purchasable` / `card_reservable` / `gold_available` / `exceed_gem_limit` / `noble_satisfied` / `action_declaration_legal`
- `no_action_available`（复合：四行动条件取反求与）
- `reach_15_prestige`（extends `<endgame_condition>`，任意玩家声望 ≥15）
- `highest_prestige_wins`（extends `<victory_condition>`，形式化为 `"<condition>": ["<prestige_highest>", "<fewest_development_cards>"]`）
- `prestige_highest` / `fewest_development_cards`（判胜用的两条单玩家判定式）
- `all_players_acted_this_round`（本轮每个玩家都已完成一个 turn，用于 player_turns 循环边界）
- `no_remaining_players`（本轮尚未行动的玩家已全部补完 turn，用于终局 final_turns 循环边界）

### 已完成（flow.json）

**流程层结构**（方案 C：声明式流程树 + `loop until` 原语 + `do_after` 依赖）：
- `game`：整局游戏作为一个 `<round>`
- `setup`：全自动 leaf phase，使用 `do_after` 表达事件依赖——无 `do_after` 的事件互相独立，可任意顺序或并行
  - 发展卡：从 `<game_box>` 准备 deck → shuffle → deal market（三个等级互相独立）
  - 贵族：从 `<game_box>` 随机取 `player_count + 1` 枚到 `<noble_market>`
  - 分发 gem/gold、决定起始玩家：与其他事件无依赖
- `main_gameplay`：loop until `<reach_15_prestige>`，每次循环产生一个 `turn_cycle` round
- `turn_cycle` → `player_turns` phase → loop until `<all_players_acted_this_round>` → 每个玩家一个 `player_turn` → 内嵌 `action_phase`
- `endgame`：包含 `final_turn_cycle` round → `final_turns` phase → loop until `<no_remaining_players>`（给本轮未行动玩家各补一个 turn），然后 `scoring` phase 按 `<highest_prestige_wins>` 判胜

**关键实现约定**：
- 每个 `<turn>` 都显式包含一个 child `<phase>`（action_phase），不省略。
- `start`/`end` 字段采用默认值（`<on_entry>`、`<when_children_done>`、`<when_events_done>`、`<when_action_resolved>`），仅在需要时覆盖。
- trigger 的 `<timing>` 保持宽泛文本，不引用 flow 命名边界。
- setup 阶段使用 `do_after` 表达事件依赖：无 `do_after` 的事件互相独立，引擎可自由安排顺序或并行执行。
- setup 中 2/3/4 人差异只体现在贵族数量（`player_count + 1`）和宝石数量（按人数内联）两处。
- 发展卡在 setup 中先从 `<game_box>` 移入 `<development_deck>` 形成 deck，再 shuffle，再发 market；贵族直接从 `<game_box>` 随机选取，不使用 deck。

## Runtime 交互模型（已实现并验证）

璀璨宝石的规则层完成后，已验证「LLM + 程序 Runtime」的交互模式。针对 Splendor 的关键结论：

- **不做状态追踪和图像识别**：程序不读取 board、手牌或牌堆。
- **LLM 只做概念识别与语言组织**：把客人问题映射到 action / trigger / condition，然后调用规则接口。
- **状态依赖问题由 LLM 反问**：例如「我现在能买这张卡吗？」→ 反问客人当前宝石、金币、已买折扣卡；客人回答后，程序做判定，LLM 组织答案。
- **问题统一为「解释某个概念/行动/触发器的条件与效果」**：不预写 FAQ，答案由 LLM 根据结构化规则自由组织。

## 后端实现

`backend/BoardAI.Api/` 已提供最小可用服务：

- `POST /api/chat`：请求体 `{ "game_id": "splendor", "messages": [...] }`，返回 `{ "reply": "..." }`
- 无状态设计：历史由前端维护，后端只在 system prompt 前拼接
- LLM 可调工具：
  - `search_concepts(game_id, query)`
  - `get_concept(game_id, concept_id)`
  - `get_action_conditions(game_id, action_id)`
- `GameRulesService` 动态读取 `games/{game_id}/concepts.json` 和 `flow.json`，顶层引用键也动态检测
- Prompt 单一来源：`appsettings.json` 中的 `LLM:SystemPrompt`，含 `{game_name}` 占位符
- 已验证效果：回答简洁、TTS 友好（汉字数字、无 markdown、无加号/引号/括号）、能处理多轮上下文、能拒绝非桌游问题
- 典型可用回答示例：
  - "贵族不用买，回合结束时如果自己面前的卡满足贵族条件，贵族自动加入你，给你三分。"
  - "不需要正好十五分。任意玩家声望达到十五或超过十五就会触发终局，这轮打完后比声望，平手比谁的卡更少。"

## 下一阶段

1. **录入第二款桌游**：已选定《文明演化》（Civolution），位于 `games/civolution/`。该游戏复杂度远高于 Splendor，已完成可行性评估与分阶段计划（详见 [[civolution-progress]]）。推进前需补充卡牌/地点图像或文字牌表，并确认等级二/三模组效果与研究牌能力。
2. **游戏元信息**：为每款游戏增加 `manifest.json`（显示名称、玩家人数、时长、封面等），供前端选游戏界面使用。
3. **前端会话绑定**：前端选游戏后固定 `game_id`，并维护 `messages` 历史。
4. **意图细分**：当前所有问题都走「概念解释」路径，后续可增加「状态判定」「行动合法性检查」「最佳行动建议」等 Intent。
5. **Tutorial Tree**：从规则解释过渡到结构化教程流程。

## 设计约定

- 游戏概念文件中的层级关系用三种键表达：`extends`（结构扩展）、`specifies`（参数绑定）、`instance_of`（具体个体）。概念引用用 `<>` 包裹（如 `"extends": "<ontology::resource>"`）
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
**How to apply:** 概念层与流程层均已完成。后端 Runtime 已通过 Splendor 验证，下一步重点是换一款游戏做真实压力测试，并补充前端选游戏与会话管理。
