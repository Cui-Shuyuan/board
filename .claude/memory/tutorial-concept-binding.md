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

## 原语改名与字段对齐（2026-09 已落地）

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

**已做**：

- 本体：`<draw>` 改成继承 `<transfer>`（并把重复声明的 source/destination/`<object>`/quantity 删掉），
  `<flip>` 改成继承 `<state_change>`。现在 `<top_draw>` 的字段全部来自 `<transfer>`。
- 引擎与原语：`move` → **`transfer`**；`from` → **`source`**、`take` → **`quantity`**、
  目的地从 `zone` 拆出来叫 **`destination`**（`zone` 只剩"选择器"语义：
  highlight/shuffle/destroy 用它表示"这个区域里的全部"）。
- 朝向从布尔改成**终态**：`flip: true` / `face_up` / `face_down` 全部并成 **`to: "face_up"/"face_down"`**。
  这条把历史上"取反式语义"那个坑（谁最后执行谁赢 → 播完对、重建错）**连根拔掉**。
- 新增 **`realizes`**：说明这个动画事件在规则上是哪个本体事件。校验器用本体继承链检查它 ——
  发牌写 `<top_draw>`（它是 `<transfer>` 的子类，所以挂在 transfer 原语上合法），
  写 `<ontology::shuffle>` 就会被拦下；表现层原语不许写。
- 已迁移 7 个 cue 文件（16 处 source / 12 处 quantity / 12 处 to / 20 处 destination），
  `realizes` 按 flow/concepts 判定后逐个补上（发牌 `<top_draw>`、贵族 `<ontology::random_draw>`、
  洗混 `<ontology::shuffle>`、拿宝石与分发宝石 `<ontology::transfer>`）。

**用户的一个洞见解掉了一处两层矛盾**：`flow.json` 说发牌是 `<ontology::transfer>`，
而本体 `<top_draw>` 说发牌就是抽顶牌、`<draw>` 又写着"与 `<transfer>` 相对"。
用户指出 **draw 本质上也是 transfer** —— 于是把 `<draw>` 改成 `<transfer>` 的子类，
两边就相容了：flow 是粗分类（转移），加 `realizes: "<top_draw>"` 是精确分类。
**不需要二选一，也不需要改 flow。**

## 相关记忆

- [[tutorial-animation-state]] — 状态/zone 模型、踩坑记录
- [[tutorial-animation-todo]] — 当前进度与下一步
- [[ontology-design]] — 本体 JSON 约定（extends / specifies / instance_of）

**Why:** 动画与本体本该是一套世界观；这次把绑定做成可校验的数据，并留下两个"手抄必漏"的教训与一处两层矛盾。
**How to apply:** 改 stage / 加模板时**必须**写 `concept`（纯视觉写 `null`）与 `contains`，
校验器会拦；想读概念语义用 `python3 scripts/concept_ref.py --concept <id>`，不要手抄父类字段。

## 组件状态：逐身份导出 + face/shows 接入对账（2026-09）

用户最初的想法就是"给每个组件引入状态……这样检查起来更直观"。落地结果：

- **采样（`DumpState`）两种粒度都写**：
  - `zones[k]`：区域整体（count / face_up / face_down / shows_face / shows_back / hidden）
  - `zones[k].kinds[身份]`：**逐身份的状态**（几张、几张朝上、实际显示哪一面）
  - `items[]`：**每件组件一行**（id / kind / concept / zone / order / face / shows）；
    `-dumpItems 0` 可关掉（整条轨道会让文件到 MB 级）。`face` 只对**真的有正反面**的件输出
    （判据是它有没有背面贴图），宝石不写 face —— 硬写会让人以为它能翻面。
- **契约（`script/{track}.json`）可以逐身份断言状态**：
  ```jsonc
  "deck_level_1": { "count": 36, "face_down": 36,
                    "kinds": { "一级垫牌": { "count": 36, "face": "down", "shows": "face" } } },
  "card_market":   { "kinds": { "一级绿": { "count": 1, "face": "up", "shows": "face" } } }
  ```
  - `face: "up"/"down"` = 这一身份的件**全都**是那一面（写起来最像人话）
  - `shows: "face"/"back"` = **画面上实际显示**的是哪一面 —— 这条才是"翻面到底成没成"的证据
- **为什么必须做到"身份"这一层**：曾经"契约全 PASS 而画面是错的"——契约只统计
  "朝上几张、朝下几张"，看不出**哪一张**朝上。牌堆里 4 张真牌 + 32 张垫牌，
  最上面那张真牌朝上时，区域级统计依然对得上，只有按身份看才露馅。
- **采样格式变更要能自证**：契约要 face/shows 而采样里没有状态字段时，
  对账会明确说"采样文件是旧格式，重跑 `scripts/dump_states.sh`"，
  而不是退化成"期望 1 实际 0"那种看起来像画面错了一样的报法。
- `ListZone` 以前把每件清单算出来却只打了"共 N 件"；现在一行一件打印
  （order / id / kind / concept / face / shows）—— 排查"到底哪一件不对"用这个。

**已用 fixture 验证过对账逻辑**（真实采样仍要在 Windows 侧跑）：正常 fixture PASS；
把牌堆里那批垫牌改成正面朝上，立刻报
`deck_level_1.kinds[一级垫牌].face: 期望全部背面朝上（face_down），实际 36 件朝上 / 0 件朝下`。

## `what`：用本体语言引用组件（2026-09 已落地）

动画数据不再写"本作专用素材名"，而是说"规则上这是哪一张"：

```json
{"at":3.58,"dur":0.34,"action":"transfer","realizes":"<top_draw>",
 "source":["deck_level_1"],"quantity":1,"destination":"card_market",
 "what":{"concept":"development_card_level_1","parts":[{"key":"bonus","value":"<emerald>"}]},
 "to":"face_up","order":0,"slot":0}
```

**为什么字段名不是 `<object>`**：本体的字段名是 `<object>`，值可以是
`{"<concept>": {属性}}`（概念 id 做 key）。但 **JsonUtility 只按固定字段名反序列化，
不支持动态键**，那种写法引擎一个字都读不到；`"<object>"` 这个键名在 C# 里也没法做字段名。
所以摊平成固定形状，字段名取**本体自己散文里用的词**：`<zone>.contains` 写的是
"若 **what** 的类型不在 contains 中，`<transfer>` 非法"。

**反查表从绑定推导**，不另存一份：
`concept` / `concept_by_palette[].concept` / `parts` →（模板, 色板）。
- 引擎：`ZoneStore.BuildConceptIndex` + `ResolveConcept`
- 校验器：`validate_cue_anim.py` 的 `concept_index` + `resolve_what`
- **两边是镜像，改一边要改另一边**（各自的注释里都写了对方在哪）

**找不到、或不唯一，都报错，绝不猜**。已注入验证：
- 属性值写错 → `这个概念下有 8 个候选 […]，要写 parts 才能说清是哪一张`
- 去掉 parts → `概念+属性 development_card_level_1| 对应多个模板
  [('sample_card_1', …), ('sample_back_1', …), ('blank_card_1', …)] —— 说不清要哪一张`

**分工是刻意的**：`what` 说"规则上这是什么"（一级发展卡、绿宝石、贵族），
`template` 说"用哪张素材"（样本卡 / 垫牌 / 真卡），后者只留给 `create` / `stack` ——
本体里**根本没有"出现/消失"这类事件**，那本来就是实现层。校验器会对
"transfer 里写 template"报警。

## 仍未做

- **牌堆的 create vs transfer**：stage 的 `initial` 里牌堆的 40/30/20 张牌**不在盒子里**
  （`box_level_*` 是空的），cue12 是用 `stack` create 出来的；而 flow 说
  `prepare_level_N_deck: game_box → development_deck_N`。两者要统一。
- **orientation（横置/竖置）**：本体还没有这一维（用户：以后用到再加）。本作卡牌不许旋转 = 约束。

## 字段归属审计：每个字段都要能说出自己属于哪一层（2026-09）

用户要求"去掉所有单独声明的变量"。做法不是删字段，而是**让每个字段都有归属**，
没有归属的字段就是"单独声明的变量"，校验器直接报错。

`python3 scripts/validate_cue_anim.py --fields` 打印这张表。四个层：

| 层 | 谁在里面 | 怎么来的 |
|---|---|---|
| **本体概念字段** | `source` / `destination` / `quantity` / `<object>`(写 `what`) / `target` / `to` / `ownership_change` / `actor` / `attribute` / `subject` / `from` / `id` / `rules` | **现场从 `realizes`（或原语的默认概念）沿 extends/specifies 推导**，代码里不抄一遍 |
| **复合字段** | `to` | 一个动画事件其实实现了两个本体事件（transfer + state_change）。**审计第一次跑就挖出来的**：12 个发牌事件上的 `to` 找不到家，因为 `<transfer>` 没有 `to` |
| **表现层** | `grow` / `peak_alpha` / `scale` / `scale_mode` / `to_alpha` / `angle` / `on` / `picture` / `amount` / `stagger` / `group` / `fade_in` | 本体没有也不该有（不改组件状态） |
| **实现层** | `order` / `slot` / `template` / `palette` / `count` / `plain` / `capacity` / `real_templates` / `pad_template` / create·stack 的 `destination` | 本体没有"出现/消失"事件，也没有"第几格"字段 |

**当前状态：没有归属的字段 = 0。** 全轨道实际用到的字段逐个都有归属。

### 审计暴露出的三个真问题（都还没解决）

1. **`target` 的值是引擎实例 id**（`gem#1` / `noble#2` / `sample_back_1#1`）。
   字段有归属（`<event>.target`），但**值的写法不是本体语言**，
   而且 id 是"第几个被创建"的产物 —— 顺序播放（不用 `start.set` 预置）时拿到的 id 完全不同。
   要换成本体语言的选法（哪个概念 + 在哪个 zone + 第几位）。
2. **`order`/`slot` 是实现层**：用户说"位置也是状态"，但本体 `<zone>` 目前只有
   `capacity` / `contains` / `information_visibility`，**没有有序表字段**。
   要让"第几位"成为可断言的状态，本体得补。
3. **create/stack 的 `destination` 是实现层**：同名字段在 transfer 上是本体字段、
   在 create/stack 上只是"摆哪儿" —— 这就是"牌堆该 create 还是 transfer"那个待决问题。

### 顺带补齐的脚本

`anim/full.json`（那时还叫 `script/full.json`）现在 17 条契约，把**有动画但没脚本**的三条补上了：
`bg.intro.001.1`（盒面 + 开局实物清单）、`setup.nobles.001.2`（贵族 3 块正面朝上）、
`action.take.different.001`（拿三种不同宝石）；外加 `setup.nobles.001.1` ——
它承担"宝石改回 2 人局 4 枚"那一步（用户裁决），enter 7 枚/色、exit 4 枚/色 + 盒里 3 枚。

**已知短板**：画面根状态（当前显示哪张整幅图）不在采样里 —— 采样只导出 zone 里的组件，
而盒面挂在画面根下。所以 `bg.intro.001.1` 契约现在只能声明"可见区域全空 + 清单在此"。

## 选件：`what`（概念+位置）取代引擎实例 id（2026-09 已落地）

**依据**：本体 `<transfer>` 的 `<object>` 字段说明原文——"被移动的 `<object>`。
**同时承担 `<event>` 中 target 的语义**"。所以"点名一件组件"的正规写法就是 `what`，
`target` 那个槽位本来就是它。

```json
{"action":"highlight",
 "what":{"concept":"gem","parts":[{"key":"color","value":"<diamond>"}]},
 "zone":"gem_supply_diamond","order":0,"grow":1.2}
```

- **`what` + `zone` 不是二选一**：what 说"哪一种"，zone 说"在哪找"，order 说"第几位"。
  只有 `target` / `container` / `what` 三者互相冲突（优先级 target > container > what > zone）。
- **解析成**候选集**不是唯一解**：样本卡、卡背样本、垫牌在本体里**都是"一级发展卡"**
  （它们确实都是），全局说不清；**在某个 zone 里**往往唯一。所以：
  - 候选为空 → 报错（概念名/属性写错）
  - 候选 >1 且没给 zone/order → 报错（真说不清）
  - 候选 >1 但给了 zone/order → **允许**（这是正常用法），到底选中几件由**引擎在真实状态下**报
    （静态检查判断不了"这个 zone 里唯一不唯一"）。
- 已迁移 13 处（3 个贵族高亮、4 个样本销毁、3 个宝石高亮、3 个发牌），**数据里 0 个 `target` id**。
- `start.set` 的预置也改成 `what`（预置是凭空造，**候选必须唯一**）；顺带把
  `action.take.different` 的 `expand_to: 7` 改成 **4** —— 那是 4 人局的数，与整篇 2 人局口径不一致
  （顺序播放时 `start.set` 不生效，所以只影响单条预览，但预览与真实播放不一致本身就是坑）。

### 还没做的（诚实记一笔）

**zone 仍然用本作的 id 引用**（`gem_supply_diamond`、`card_market`…）。它们已经绑定了概念，
但"跨游戏复用同一份动画数据"还做不到 —— 要复用，zone 也得能用概念+属性引用
（例如"某个玩家的持有区"）。对象那一侧已经做到了，zone 这一侧是下一个前沿。
