---
name: pipeline-model
description: pipeline 模型定稿——pipeline 是唯一结构原语（procedure 也用它）、四原子（options/type/do_after/loop）、候选级 vs 步骤级 condition 语义、skip 模式
metadata:
  type: project
---

# Pipeline 模型（2026-08-08 定稿，2026-08-09 结构统一更新）

## 核心概念

`<pipeline>` 是多选项处理模型。从几个 token 中选一个拿走，从几个 action 中选一个执行，一个流程有好几个步骤按什么顺序进行——这些都是同一问题。

## Pipeline 是唯一的结构原语 ★（2026-08-09 定稿）

**`<round>`、`<turn>`、`<phase>` 的多步骤执行用 `<ontology::pipeline>` 定义；仅干一件事时直接持有对应概念（如 `<ontology::action>`），不套 pipeline**（2026-08-09 修正：pipeline 是描述多 trigger/多步骤的工具，不是必包层；phase 同样不是必包层——只有 1 个 phase 时省略不写）。children 数组、actor、start/end 字段全部废弃：

- 步骤顺序与先后依赖 → options + do_after
- **round/phase 执行次数（口语「进行几轮」）→ 顶层 `loop` 字段**（count 定次 / until 条件，**不在 pipeline 内**——loop 挂 pipeline 会逼单内容 procedure 为挂 loop 而包一层 pipeline，已修正）。缺省无 loop = 1 次 = 每位玩家按座次各行动一轮；「进行 4 轮」= `"loop": { "count": 4 }`。字段名不叫 rounds——「进行 4 轮」的「4」是次数
- 「谁能做某操作」→ 步骤/候选的 `<condition>`（不成立则阻断或排除）
- 行动权轮转 → `<turn>` 的内建语义（按座次推进，无需字段声明）

```json
// round：loop 是顶层字段（不在 pipeline 内），多步骤内容用 pipeline 承载
{
  "id": "era_loop",
  "specifies": "<ontology::round>",
  "loop": { "count": 4 },
  "<ontology::pipeline>": {
    "options": [ phase_1, ..., phase_8 ],
    "type": "<ontology::multiple_choice_enum.EXECUTE_ALL>"
  }
}

// round：单 turn 直接持有 turn + 顶层 loop（无需 pipeline）
{
  "id": "action_turn_cycle",
  "specifies": "<ontology::round>",
  "loop": { "until": "<action_phase_end>" },
  "<ontology::turn>": {
    "id": "player_action_turn",
    "<ontology::pipeline>": {
      "options": ["<activate_module>", "<reset>"],
      "type": "<ontology::multiple_choice_enum.CHOOSE_ONE>"
    }
  }
}

// turn：行动选择即 CHOOSE_ONE pipeline，无 actor
{
  "id": "player_action_turn",
  "specifies": "<ontology::turn>",
  "<ontology::pipeline>": {
    "options": ["<activate_module>", "<reset>"],
    "type": "<ontology::multiple_choice_enum.CHOOSE_ONE>"
  }
}

// 「每位玩家一个 turn 按座次轮转」：round 缺省 1 次即是每人一轮，turn 内建轮转
// ★ 2026-08-09 定稿：次数确定用 count（缺省 1）；直到条件结束才用 loop.until
{
  "id": "goal_choice_round",
  "specifies": "<ontology::round>",
  "<ontology::pipeline>": {
    "options": [ { "id": "player_goal_turn", "specifies": "<ontology::turn>", ... } ],
    "type": "<ontology::multiple_choice_enum.EXECUTE_ALL>"
  }
}
```

**pipeline 也是通用可挂载结构**——任何概念都可以选择持有它：`<trigger>`（action/effect）放在 `<content>.<instant_content>` 内（或直接持有）；`<cost>` 用于多步支付；`<content>` 用于多步骤结算。游戏层引用写 `<ontology::pipeline>`；ontology 内部字段名不带 namespace。

## 步骤级 vs 候选级 condition 语义 ★★（2026-08-09 定稿）

| 层级 | condition 不成立时 |
|---|---|
| **步骤级**（pipeline options 顶层项） | **阻断**——该步骤及 do_after 依赖它的后续步骤全部停止执行 |
| **候选级**（CHOOSE_ONE/CHOOSE_ANY 等的 options 项） | **排除**——该项不可选，不影响其他候选的选择 |

- 候选可用 = 该候选携带的 `<condition>` 成立；无 condition 的候选恒可用
- `_skip` 是唯一「显式选择跳过且视为完成」的哨兵（解决步骤级「可选」问题）
- 「如果能 A 就必须 A，否则 B」：**B 作为带「A 不可用」condition 的候选**（如「跳过」的 condition = 全部行动条件取反），候选 condition 互补覆盖所有情况，CHOOSE_ONE 从可用候选中必选其一

## 步骤的四种形态

options 的元素可以是：

| 形态 | 写法 | 用途 |
|---|---|---|
| 操作步骤 | `{ "id", "name", "specifies": "<ontology::transfer/state_change/push_track/flip/play>", 字段... }` | 单事件直接执行；可带 `<ontology::condition>`（不成立则阻断） |
| 条件步骤 | `{ "id", "name", "specifies": "<ontology::trigger>", "<ontology::condition>": {...}, "<ontology::content>": {...} }` | 需要判定 + 多个子步骤的复合步骤 |
| 字符串引用 | `"<move_tribe>"` | 无参调用其他概念（action/trigger），候选级引用时用 `"<move_tribe>"` 全形式 |
| 子 pipeline | `{ "id", "name", "options": [...], "type": "..." }` | 内嵌步骤序列 |

## 四个原子

| 原子 | 写法 |
|---|---|
| options | 候选项数组。里面装什么由具体场景决定——card、resource、transfer、action、子 pipeline、player、zone、甚至 _skip |
| type | 处理策略，引用 `multiple_choice_enum`：EXECUTE_ALL / CHOOSE_ONE / CHOOSE_AT_LEAST_ONE / CHOOSE_ANY / **MATCH**（2026-08-10 新增：规则匹配执行——由规则/局面事实决定哪个/哪些执行。**结构与 type 同级持有单个 `<ontology::condition>` 描述匹配依据**（不管多少个 options 只写一个 condition，不挂 option 级），规则按该依据从候选中选定执行者，命中数量由局面决定。区别于 CHOOSE_ONE 的玩家决策、EXECUTE_ALL 的全执行。典型场景：板块类别决定计分哪类） |
| do_after | 前置依赖。仅当 do_after 中所有项完成（或跳过）后才可执行 |
| loop | 循环边界（**procedure 顶层字段，不在 pipeline 内**），两种形态对称：`{ "count": N, "counter": "<名>" }` 定次循环（N 为数字或引用；counter 可选，供步骤引用迭代号——「进行 4 轮」= count 4）；`{ "until": <condition> }` 条件循环（until 为字符串引用或内联谓词 { zh, en }）——用于次数未知的规则（如「一直进行到有人 15 分」）。每次迭代重新求值 type 与 do_after |

## 阻断语义（do_after 链）

**do_after 的前置项必须实际结算完毕**——前置步骤因 condition 不成立被阻断 = 未结算，依赖它的步骤**不执行（链条停止）**。

- 因此「驱逐发生才虚弱」只需 `"do_after": ["displace_occupant"]`，**不要**再加「驱逐实际发生」类 condition——所有 pipeline 都这样写就没完没了
- `_skip`（玩家显式选择不做）例外：选中时 do_after 链视为已完成，后续照常执行
- 候选级 condition 不成立 = 排除，**不阻断**（见上）

## _skip = 跳过

`_skip` 表示「什么都不做」（自描述哨兵）。被选中时跳过执行，但在 `do_after` 链上视为已完成。等价于 `CHOOSE_ONE(做, 不做)`。用于「可选步骤」：必做直接写，may 就包 `CHOOSE_ONE(_skip, step)`。

JSON 写法：
```json
{ "id": "_skip", "description": { "zh": "不做（跳过此项）", "en": "Skip this option" } }
```

**不用 `null`**——LLM 读不懂 `null` 的语义，必须用带 `id` 和 `description` 的对象。

## 「能 A 必须 A，否则跳过」模式 ★（2026-08-09）

「跳过」作为带 condition 的候选（如 Splendor `skip_turn`：condition = `no_action_available`，即四个行动条件取反求与），使所有候选 condition 互补覆盖：

```json
"<ontology::pipeline>": {
  "options": [
    "<take_gems_different>",       // condition: gems_available_any
    "<take_gems_same>",            // condition: gems_available_same_color
    "<purchase_development_card>", // condition: card_purchasable
    "<reserve_development_card>",  // condition: card_reservable
    "<skip_turn>"                  // condition: no_action_available（四条件取反求与）
  ],
  "type": "<ontology::multiple_choice_enum.CHOOSE_ONE>"
}
```

任意行动可用 → skip 被排除，必选其一；全不可用 → skip 是唯一可用候选。不引入 else/_fallback 等新字段。

## 五个典型场景

前提：A → B → C 的顺序关系。

### 1. 干完A后，不干B就不能干C

B 和 C 捆一起，外面包 `CHOOSE_ONE(_skip, B→C)`。

```json
[
  { "id": "a" },
  {
    "do_after": ["a"],
    "options": [
      { "id": "_skip", "description": { "zh": "不做（跳过此项）", "en": "Skip this option" } },
      {
        "options": [
          { "id": "b" },
          { "id": "c", "do_after": ["b"] }
        ],
        "type": "<ontology::multiple_choice_enum.EXECUTE_ALL>"
      }
    ],
    "type": "<ontology::multiple_choice_enum.CHOOSE_ONE>"
  }
]
```

### 2. 干完A后必须干B，但可以不干C

B 无包装（必须），C 包 `CHOOSE_ONE(_skip, C)`。

```json
[
  { "id": "a" },
  { "id": "b", "do_after": ["a"] },
  {
    "do_after": ["b"],
    "options": [
      { "id": "_skip", "description": { "zh": "不做（跳过此项）", "en": "Skip this option" } },
      { "id": "c" }
    ],
    "type": "<ontology::multiple_choice_enum.CHOOSE_ONE>"
  }
]
```

### 3. 干完A后，B干不干都行，但C必须干

B 包可选项，C 的 `do_after` 挂 A 不挂 B。

```json
[
  { "id": "a" },
  {
    "do_after": ["a"],
    "options": [
      { "id": "_skip", "description": { "zh": "不做（跳过此项）", "en": "Skip this option" } },
      { "id": "b" }
    ],
    "type": "<ontology::multiple_choice_enum.CHOOSE_ONE>"
  },
  { "id": "c", "do_after": ["a"] }
]
```

### 4. 干完A后可以不干B，但不干B就必须干C，干了B就不能干C

B 和 C 互斥，且不能都不干。

```json
[
  { "id": "a" },
  {
    "do_after": ["a"],
    "options": [{ "id": "b" }, { "id": "c" }],
    "type": "<ontology::multiple_choice_enum.CHOOSE_ONE>"
  }
]
```

### 5. 干了B则C必须跟着干；不干B则C可选

两条独立路径，`CHOOSE_ONE(B→C, _skip|C)`。

```json
[
  { "id": "a" },
  {
    "do_after": ["a"],
    "options": [
      {
        "options": [
          { "id": "b" },
          { "id": "c", "do_after": ["b"] }
        ],
        "type": "<ontology::multiple_choice_enum.EXECUTE_ALL>"
      },
      {
        "options": [
          { "id": "_skip", "description": { "zh": "不做（跳过此项）", "en": "Skip this option" } },
          { "id": "c" }
        ],
        "type": "<ontology::multiple_choice_enum.CHOOSE_ONE>"
      }
    ],
    "type": "<ontology::multiple_choice_enum.CHOOSE_ONE>"
  }
]
```

## 设计约定

- 不加新字段（不引入 `optional`、`required`、`else`、`fallback` 等）。只用 `options` / `type` / `do_after` / `loop` + `_skip` 哨兵。
- `_skip` 表示跳过，语义为「不做也是一种合法选择」。JSON 中必须写成带 `id` 和 `description` 的对象，不用 `null`。
- 必选步骤直接写，可选步骤包 `CHOOSE_ONE(_skip, step)`。
- 「能 A 必须 A，否则跳过」：把「跳过」做成带「A 不可用」condition 的候选（候选级排除 + 互补覆盖）。
- 跳过即完成：`_skip` 被选中时 `do_after` 链不阻断。
- `options` 不限定类型——由具体场景决定里面装什么。

**Why:** 这是 pipeline 的唯一正确写法模版。新会话写流程时直接参考，不用重新讨论。
**How to apply:** 写任何多步 action/trigger/procedure 的流程时，先判断每步是 must 还是 may，may 就包 CHOOSE_ONE(_skip, ...)。互斥选用 CHOOSE_ONE，任意组合用 CHOOSE_ANY，全做用 EXECUTE_ALL，循环用 loop。
