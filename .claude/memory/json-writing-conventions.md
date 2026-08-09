---
name: json-writing-conventions
description: flow.json 与 concepts.json 编写时必须遵守的格式规则，基于后端代码实际行为总结
metadata:
  type: project
---

# JSON 编写约定

## 所有节点必须有 `name`

flow.json 中每一个有 `id` 的节点（procedure、trigger、pipeline step）都**必须**有 `name` 字段：

```json
{
  "id": "place_boat",
  "name": { "zh": "放置船", "en": "Place Boat" }
}
```

**原因**：索引脚本用 `id + name.zh + description.zh` 拼接搜索文本。没有 `name` 的节点只有英文 id，在中文查询的向量空间里变成噪音吸引子——短英文 token 的泛化向量恰好排在中文本应命中的概念前面。

---

## Action 格式：trigger 模型

**trigger 的底层执行流程不显式声明（2026-08-09）**：标准四步（evaluate_condition → pay_cost → select_target → resolve_content）由 ontology/flow.json 的 `trigger_pipeline` 统一定义，action/effect 的 `<pipeline>` 字段缺省引用它。具体实现**只写参数值**——condition / cost / target / content——不把流程步骤重新声明一遍（如不写 "pay_cost" / "resolve_effect" 步骤）。步骤名是全局可寻址的：scope 路径（如 substitution 的 `"<activate_module>.<ontology::pipeline>.pay_cost"`）经 trigger 概念上的 `<pipeline>` 字段（default 指向 trigger_pipeline）解析。

Action 必须遵循 `condition → cost → target → content` 结构：

```json
{
  "id": "build_farm",
  "specifies": "<ontology::action>",
  "name": { "zh": "建造农场", "en": "Build Farm" },
  "description": { "zh": "...", "en": "..." },
  "<ontology::condition>": {
    "zh": "谓词（单条时直接写 zh/en，不用 options 结构）",
    "en": "Predicate (single condition: write zh/en directly, no options)"
  },
  "target": "用自然语言描述，嵌 <concept_id> 引用",
  "<ontology::content>": {
    "<ontology::instant_content>": {
      "options": [ ... ],
      "type": "<ontology::multiple_choice_enum.EXECUTE_ALL>"
    }
  }
}
```

- **condition**：**单条谓词直接写 `{ "zh": "...", "en": "..." }`**，不用 options 结构；仅当有多条谓词（需并列/组合）时才用 `options`/`type`/`EXECUTE_ALL` 结构，每条谓词用自然语言描述
- **cost**：`"<ontology::instant_cost>": { ... }`（`<ontology::continuous_cost>` 用于状态条件）。**无需支付时省略 cost 字段，不写 null**
- **target**：**纯字符串**（不是对象）。用自然语言描述，嵌入 `<concept_id>` 交叉引用。target 是 trigger 的字段，不是 ontology 概念，不加 `<>` 包在字段名上
- **content**：`<ontology::instant_content>` 或 `<ontology::continuous_content>`，内部用 `options`/`type`/`do_after` 描述步骤

---

## Pipeline 格式：options / type / do_after

```json
{
  "options": [
    { "id": "step_a", "<ontology::transfer>": { ... } },
    { "id": "step_b", "do_after": ["step_a"], ... },
    null
  ],
  "type": "<ontology::multiple_choice_enum.EXECUTE_ALL>"
}
```

- **options**：数组格式，不是对象。每项可以是 step 对象、字符串引用（`"<build_farm>"`）、或 `null`（跳过）
- **type**：必须是完整引用 `"<ontology::multiple_choice_enum.XXX>"`
- **do_after**：数组格式 `["step_a", "step_b"]`。无 do_after = 独立可并行
- **_skip**（不用 null）：表示"不做也是一种合法选择"。必须写成 `{ "id": "_skip", "description": { "zh": "不做（跳过此项）", "en": "Skip this option" } }`。被选中时跳过执行，do_after 链上视为已完成
- 必选步骤直接写，可选步骤包 `"options": [_skip, step], "type": "CHOOSE_ONE"`
- "不干B就不能干C"：B和C捆成子 pipeline，外包 `CHOOSE_ONE(_skip, B→C)`

---

## Procedure 结构统一 ★（2026-08-09）

**round/turn/phase 用 `<ontology::pipeline>` 定义多步骤执行；仅干一件事时直接持有对应概念（如 `<ontology::action>`），不套 pipeline**。children 数组、actor、start/end 字段全部废弃：

```json
// round：执行次数（进行几轮）用 count 字段，不写 loop
{
  "id": "era_loop",
  "specifies": "<ontology::round>",
  "count": 4,
  "<ontology::pipeline>": {
    "options": [ phase_1, ..., phase_8 ],
    "type": "<ontology::multiple_choice_enum.EXECUTE_ALL>"
  }
}

// turn：多行动选择即 CHOOSE_ONE，无 actor
{
  "id": "player_action_turn",
  "specifies": "<ontology::turn>",
  "<ontology::pipeline>": {
    "options": ["<activate_module>", "<reset>"],
    "type": "<ontology::multiple_choice_enum.CHOOSE_ONE>"
  }
}

// turn：单行动直接持有 action，不包 pipeline
{
  "id": "player_extra_find_turn",
  "specifies": "<ontology::turn>",
  "<ontology::action>": "<extra_find>"
}
```

- **loop 是 procedure 顶层字段 ★（2026-08-09 定稿）**：`loop` 挂在 `<round>`/`<phase>` 顶层（**不在 pipeline 内**——曾因 loop 挂 pipeline 而被迫为单内容 round 包 pipeline 层，已修正）。两种形态对称：`"loop": { "count": N }` 定次（「进行 4 轮」= count 4，N 可为数字或引用；缺省无 loop = 1 次 = 每位玩家恰好一个 turn）、`"loop": { "until": <condition> }` 条件（直到条件才结束——如 Splendor `main_gameplay` 的 `until <reach_15_prestige>`、行动阶段 `until <action_phase_end>`）。字段名不叫 rounds——「进行 4 轮」的「4」是次数，round 的属性不该是「轮」
- **单内容直接持有 ★（2026-08-09 定稿）**：pipeline 是描述多 trigger/多步骤的工具，**单内容一律直接持有下一层概念，不包 pipeline**——phase 只有 1 个 round → 持有 `<ontology::round>`；round 只有 1 个 turn → 持有 `<ontology::turn>`；turn 持有 `<ontology::action>`（civolution 的 phase_3_extra_find 已内联为 phase→round→turn→action 四层无 pipeline；Splendor 的 player_turns/action_phase 包装层已删）
- **options/type 是通用选择结构 ★（2026-08-09 用户定稿）**：options/type **只是 pipeline 的一块构件**（multiple_choice_enum 本就是「适用于任何需要多选项场景」的通用字段）——一个动作（即使是多候选二选一）不配叫 pipeline，直接把 options/type 挂在概念上：`"<ontology::action>": { "options": ["<a>", "<b>"], "type": "CHOOSE_ONE" }`（单候选直接字符串引用 `"<ontology::action>": "<extra_find>"`）。候选自带 condition（候选级排除），_skip 哨兵同样可用
- **「谁能做」写 condition**：步骤级 condition 不成立 = **阻断**（该步骤及 do_after 依赖项停止）；候选级 condition 不成立 = **排除**（不影响其他候选）。「终局补完回合」= round 缺省 1 次 + turn 带「本轮尚未行动」condition（已行动玩家阻断跳过）
- **「能 A 必须 A，否则跳过」**：把「跳过」做成带「A 不可用」condition 的候选（如 Splendor `skip_turn`，condition = 全部行动条件取反），候选 condition 互补覆盖，CHOOSE_ONE 从可用候选中必选其一

## Key-as-type（不用 `"type"` 字段）

表达"这是什么类型的 content/event/transfer"时，用概念 ID 直接做 key：

```json
// 正确
"<ontology::instant_content>": {
  "<ontology::transfer>": { "source": "...", "destination": "..." }
}

// 错误
"type": "<ontology::instant_content>",
"<ontology::event>": {
  "type": "<ontology::transfer>"
}
```

同样适用于 `parts` 数组：

```json
// 概念引用 → 用 key
{ "<card_name>": { "position": {...} } }

// 纯标签（无 <>）→ 用 id
{ "id": "chip_name", "position": {...} }
```

**不写 `| null` 后缀（2026-08-09）**：字段可空性由 `constraints.optional` 或 `default: null` 表达，`type` 中不写「`| null`」（如 `"type": "<pipeline> | null"` 是过时写法）。key 已为 `<concept_id>` 格式的字段也不再写 type。

---

## Substitution 挂载格式（2026-08-09）

替代/视为规则声明在**对象定义处**，不在使用处：

```json
{
  "id": "planning_marker",
  "specifies": "<ontology::resource>",
  "<ontology::substitution>": {
    "target": "<activation_die>",
    "<ontology::condition>": { "zh": "点数与所需一致", "en": "value matches" },
    "scope": "<activate_module>.<ontology::pipeline>.pay_cost"
  }
}
```

- `target` / `scope` 是 substitution 内部字段（不带 namespace）；`<ontology::condition>` 复用 ontology 概念（key-as-type）
- **scope 是路径式定位**（概念.字段.步骤）：`"<activate_module>.<ontology::pipeline>.pay_cost"` 指向 activate_module 的 pipeline 中 id 为 pay_cost 的步骤。**路径中的概念引用遵循 namespace 约定**——游戏层上下文里 ontology 概念必须写全 `<ontology::pipeline>`。步骤 id 要与 ontology/flow.json `trigger_pipeline` 的标准步骤名一致（evaluate_condition / pay_cost / select_target / resolve_content）
- 使用处（如 activate_module 的 pay_cost 步骤）不重复声明替代，只引用 cost

---

## per_player vs round+turn ★（2026-08-09）

「每位玩家」的两种表达（定义见 ontology `<per_player>` 概念）：

- **`"<ontology::per_player>": true`**（挂在 event 上）：**程序化分发**——每位玩家各获得 N 个，不重视排他性和行动顺序。用于 setup 个人准备（player_setup_*、轮抽的抽牌步骤——规则书是「分发/秘密选择」无座次轮次）
- **`round` + `turn`**：**顺序轮转**——强调「轮到谁」。**「从起始玩家开始，每位玩家…」「按座次依次…」一律 round+turn**：round 的 pipeline 写 `loop: { until: <所有玩家已行动> }`，options 里一个 `<turn>` 节点，轮转由 turn 的内建语义推进（无 actor 字段）；轮抽的「从右手边开始逆时针」等顺序差异写在 round 的 description 里

---

## Transfer 的 `<ontology::object>` 两种形式 ★（2026-08-08）

1. **纯引用字符串**：`"<ontology::object>": "<event_card>"`
2. **带属性设定的对象**（概念 ID 做 key + 属性值）：对象以该属性状态进入 destination，与 `parts` 的写法一致：

```json
// 正确：以背面朝上状态放置
"<ontology::object>": { "<event_card>": { "face": "face_down" } }

// 错误：游离的 face_down 字段（transfer 概念无此字段）
"<ontology::object>": "<event_card>",
"face_down": true
```

对象属性（face、level 等 piece 状态）统一挂在对象引用上，不新增游离字段。

---

## 支付 destination 的语义区分

- **激活骰支付**（模块激活费用）：`source: <activation_dice_area>` → `destination: <player_holding>`——激活骰属于玩家，支付后放回玩家保留区（重置时拿回），**不写 `<ontology::supply>`**（那是公共版图供应堆，会造成误解）
- **计划标记支付**：放回 `<ontology::supply>`（planning_marker 定义中已注明）
- **通用标记发放**：从供应堆拿的是 `<octagonal_pillar>`（通用标记），进入食物格/创意格/钱币格/骰子格才「成为」`<food>`/`<idea_marker>`/`<money>`/`<planning_marker>`——transfer 的 object 写八角柱，具名在 description 里说明

---

## `this.target` 引用

transfer 的 destination 应该引用 action 自己的 target，不要写死 zone 名：

```json
// 正确
"<ontology::transfer>": {
  "source": "<farm_supply>",
  "destination": "this.target"
}

// 错误
"<ontology::transfer>": {
  "source": "<farm_supply>",
  "destination": "<continent>"
}
```

---

## 描述文本统一用 `description`

所有概念和节点的中英文描述都用 `description`，**不用** `definition`：

```json
{
  "id": "farm",
  "description": { "zh": "...", "en": "..." }
}
```

---

## Cost 有内容时用 `instant_cost`

```json
// 一次性支付
"<ontology::cost>": {
  "<ontology::instant_cost>": {
    "<ontology::transfer>": { "source": "<storage_area>", "destination": "<ontology::supply>", ... }
  }
}

// 无需支付：省略 cost 字段，不写 "<ontology::cost>": null
```

---

## 索引规则

每一条被索引的概念的搜索文本拼接方式为：
```
id + name.zh + name.en + description.zh + description.en
```

**任何没有 `name.zh` 的节点，在中文查询下都是噪音。**

---

## 检查清单

写一个新 action/trigger/procedure 时确认：
- [ ] 有 `name: { zh, en }`（紧跟 id 之后）
- [ ] procedure（round/turn/phase）多步骤时持有 `<ontology::pipeline>`，单内容时直接持有概念（如 `<ontology::action>`），不用 children/actions/actor/start/end
- [ ] **1 个动作不叫 pipeline**：多候选动作直接用 `"<ontology::action>": { "options": [...], "type": "CHOOSE_ONE" }`（options/type 是通用选择结构，挂在概念上即可）
- [ ] **round/phase 执行次数用顶层 `loop`（count 定次 / until 条件），不在 pipeline 内**；缺省无 loop = 1 次 = 每人一轮；turn 内建轮转，顺序差异写 description
- [ ] phase 不是必包层：只有 1 个 phase 时省略不写
- [ ] 步骤级 condition 不成立 = 阻断；候选级 condition 不成立 = 排除（二者语义不同）
- [ ] 无行动可选的兜底：把「跳过」做成带「全部行动条件取反」condition 的候选
- [ ] condition 单条写 zh/en，多条才用 options/type/EXECUTE_ALL
- [ ] cost 是 instant_cost/continuous_cost；无需支付则省略字段（不写 null）
- [ ] target 是字符串，不是对象
- [ ] content 是 `<ontology::instant_content>` 或 `<ontology::continuous_content>`
- [ ] 所有 type 引用是完整路径 `"<ontology::multiple_choice_enum.XXX>"`
- [ ] 没有 `"type": "<ontology::xxx>"` 这种写法（用 key-as-type）
- [ ] type 中不写 `| null`（可空由 optional/default 表达）
- [ ] key 已为 `<concept_id>` 格式时不写 type
- [ ] destination 用 `this.target` 而非写死 zone
- [ ] 描述字段叫 `description` 不叫 `definition`
- [ ] 必选/可选：may 就包 CHOOSE_ONE(_skip, step)

**Why:** 后端索引、查询、语义匹配全部依赖这些约定。违反任何一条都会导致 LLM 搜不到或理解错误。
**How to apply:** 写任何新概念/action/trigger 时逐项对照检查清单。
