---
name: tutorial-concept-binding
description: 动画层 ↔ 本体概念的绑定（2026-09 探索中）：stage 里每个模板/zone 用 concept 挂到 ontology/game 概念，zone 用 contains 声明可存类型；concept_ref.py 解析继承链，validate_cue_anim.py 按概念校验转移与翻面
metadata:
  type: project
---

# 动画 ↔ 本体：概念绑定（2026-09，探索中）

## 用户要解决的问题（原话整理）

> 动画里的那些组件和 ontology 中的概念集合进行一些关联。因为 ontology 是桌游，
> 动画也是桌游，**它们天然就是对等的，用一套世界观合情合理**。

起点是"给每个组件引入状态"这个想法（卡牌：横置/竖置、正面/反面；翻转=改状态、
移动=改位置）。查下来发现：**这套本体里早就定稿了，是动画层没采纳**。

- 本体 `<state>`：*"某个时刻游戏世界中为真的断言……运行时事实，随时可被 effect 改变"*
- 本体 `<state_change>`：*"一个 <object> 的某个属性的值从 A 变为 B（如卡牌的横置→竖置、
  token 的正面→反面）"*，其 `attribute` 字段的说明原文就举了「一张卡同时有 orientation 和 face 两个状态」
- 本体 `<card>.face`：*"face_up = 正面朝上；face_down = 背面朝上。**null 表示不区分正反**"*
- 本体 `<property>`：固有、默认不可变（"是什么"），与 `<state>`（"此刻怎样"）明确分开

**属性靠继承（卡牌 → N 级发展卡），状态是每个实例各有一份。**

## 落地方式：声明 + 校验（不做运行时语义引擎）

沿用 2026-09-15 的裁决（flow/concepts 只当查阅资料，不做运行时解析）。所以：

| 在哪 | 写了什么 |
|---|---|
| `anim/_stage/splendor.table.json` · 模板 | `concept`（字符串）/ `concept_by_palette`（按色板分身份）/ **显式 `null`** = 纯视觉件；`parts`（本体 `<piece>.parts`）；`sample`/`filler` 标记 |
| 同上 · zone | `concept`；`contains`（**本体 `<zone>.contains` 的同名字段**，空数组 = 不限） |
| `scripts/concept_ref.py` | 读 `ontology/concepts.json` + `games/{game}/concepts.json`，沿 `extends` **和** `specifies` 合并字段 |
| `scripts/validate_cue_anim.py` | 绑定完整性 / 概念存在性 / **转移目标区必须允许该概念** / **只有声明 face 的概念才能翻面** |

已绑定的关键映射（璀璨宝石）：

- `gem` 模板按色板分身份：`gem_diamond…onyx` → `<gem>`，**`gem_gold` → `<gold>`**（黄金不是宝石）
- `market_card_{1,2,3}_{color}` → `development_card_level_{1,2,3}` + `parts.bonus = <color>`
- `blank_card_*`（垫牌，永远发不出来）→ 同级发展卡 + `filler: true`；`sample_*` → + `sample: true`
- `deck_level_N` → `development_deck_level_N`；`card_market`/`noble_market`/`gem_supply`/`gold_supply`/`<player_holding>` 同名对应
- `box_*` → `<ontology::game_box>`；`board_*` → `<ontology::public_board>` / `<ontology::player_board>`
- **纯视觉（显式 null）**：`offstage`（镜头外通道）、`showcase*`（展示位）、`glow_zone`（高亮底板）

一组对应关系值得单独记住：**`gem_supply_diamond` 等 6 个 zone，在本体里是同一个概念
`<gem_supply>` 的 6 个实例**（按颜色区分）。这正是"一套世界观"的样子，
也解释了引擎为什么要用 `模板id|色板`（`KindKey`）作实例身份。

## 已验：0 误报 + 注入错误确实被抓到

- 已验收的 7 条 cue 上跑概念检查：**0 错 0 警告**（不是空跑，检查确实在比）
- 注入两个错误后都报出来：
  - `要把 development_card_level_1 移进 deck_level_2，但该区域只允许 ['development_card_level_2']`
  - `对 gem 翻面，但该概念没有 face`
  - 本体的原话就是这条规则的依据：`<zone>.contains`——"**程序校验 `<transfer>` 时以此过滤**"

## 我自己犯的两个错（都只有工具能发现，手抄一定漏）

1. **只跟 `extends`**：游戏层大量用 `specifies`（"只填父类槽位、不加字段"），
   而它同样是 IS-A。只跟 extends 会把 `development_card_level_1` 看成孤儿、
   继承不到 `<card>.face`——正好把用户要的"继承卡牌的状态"判错。
2. **按"有 id 和 description"递归收概念**：`constraints.required/optional` 里的字段槽位
   长得一模一样，于是 `face`、`source`、`contains`、`<condition>[]` 都成了"概念"，
   校验器会把字段名当概念放行（**假阴性**）。现在规则很笨但正确：
   顶层数组的值是概念、以 `<` 开头的键的值是概念。

## 下一刀：原语改名与字段对齐（未做，待定）

用户明确要求：`move` 应该改成 `transfer` 与本体对等，`flip` 同理。查证后更准确的方案：

| 动画原语 | 本体对应 | 说明 |
|---|---|---|
| `move` | `<transfer>`（source / destination / `<object>` / quantity / ownership_change） | flow 的 `distribute_gems`、`prepare_level_N_deck` 都是 `<ontology::transfer>` |
| `move`（从暗堆/盲抽） | `<top_draw>` / `<random_draw>` | flow 的 `deal_nobles specifies <ontology::random_draw>` |
| `flip` | `<flip>`（attribute 固定 face，to = face_up/face_down） | 本体有 `<flip>`，但它 `extends=None`（本体自己该修成 `<state_change>` 的特例） |
| `shuffle` | `<shuffle>` | 已对齐 |
| `rotate` | 无（orientation 暂不加，用户定） | 璀璨宝石里卡牌不许旋转，是约束不是状态 |
| `create` / `destroy` / `stack` | **无对应事件** | 本体只有 `<object>` 本身；`<gain>`/`<lose>` 动的是 property 归属，不是存在 |
| `fade`/`highlight`/`scale`/`wait`/`showbox` | 无 | **纯表现层**，本就不该有本体概念 → 该显式标注 |

**发现一处两层矛盾（"一套世界观"的第一个实际收益）**：
`flow.json` 说 `deal_card_market_level_N specifies <ontology::transfer>`，
而本体 `<top_draw>` 的原话是"从 `<deck>` 顶部抽最上面 1 张……**用于翻牌、发牌**、暗面抽牌"，
且 `<draw>` 明确与 `<transfer>` 对立（"抽取前对象不可见，与 `<transfer>`（移动已知对象）相对"）。
按本体自己的判据（**移动前身份是否未知**），洗过的牌堆发牌应该是 `<top_draw>`，flow 那三条该改。

方向建议：**原语名对齐机制（`move`→`transfer`），另加 `as` 字段说明它在规则上是哪个事件**
（`<transfer>` / `<top_draw>` / `<random_draw>`）——机制与语义分开，两边都不将就。
`flip` 顺带从"取反"改成本体写法 `to: "face_up"`，**这是把历史上"取反式语义"那个坑连根拔掉**
（见 [[tutorial-animation-state]] 的"朝向有两套相反的定义"）。字段名对齐
（`from`→`source`、`zone`→`destination`、`take`→`quantity`）是最贵也最值钱的一步。

## 相关记忆

- [[tutorial-animation-state]] — 状态/zone 模型、踩坑记录
- [[tutorial-animation-todo]] — 当前进度与下一步
- [[ontology-design]] — 本体 JSON 约定（extends / specifies / instance_of）

**Why:** 动画与本体本该是一套世界观；这次把绑定做成可校验的数据，并留下两个"手抄必漏"的教训与一处两层矛盾。
**How to apply:** 改 stage / 加模板时**必须**写 `concept`（纯视觉写 `null`）与 `contains`，
校验器会拦；想读概念语义用 `python3 scripts/concept_ref.py --concept <id>`，不要手抄父类字段。
