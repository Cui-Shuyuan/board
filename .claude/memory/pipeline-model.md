---
name: pipeline-model
description: pipeline 模型定稿——pipeline 位于 trigger 的 content.instant_content 内，步骤四种形态、do_after 语义、五个典型场景模版
metadata:
  type: project
---

# Pipeline 多选项处理模型（2026-08-08 定稿）

## 核心概念

`<pipeline>` 是多选项处理模型。从几个 token 中选一个拿走，从几个 action 中选一个执行，一个流程有好几个步骤按什么顺序进行——这些都是同一问题。

## Pipeline 的位置 ★（2026-08-08 定稿）

**pipeline 是 `<content>` 的内部结构，不单独持有**：

- **`<trigger>`（action/effect 均 specifies trigger）**：结构为
  `condition（门槛）→ cost（代价）→ target（this.target 供步骤引用）→ <ontology::content>.<ontology::instant_content>.{ options, type }`
  ——pipeline 的 options/type/do_after 放在 `<instant_content>` 内部
- **`<phase>`**：持有 `<pipeline>` 顶层字段（程序化阶段，如 setup）——phase 不是 trigger，无 condition/cost/content

```json
{
  "id": "resolve_migration_triggers",
  "specifies": "<ontology::action>",
  "name": { "zh": "...", "en": "..." },
  "description": { "zh": "...", "en": "..." },
  "<ontology::content>": {
    "<ontology::instant_content>": {
      "options": [ ... ],
      "type": "<ontology::multiple_choice_enum.EXECUTE_ALL>"
    }
  }
}
```

历史沿革（避免重蹈覆辙）：最初 action 用 `"type": "<ontology::event>"` + events[] 数组；后改为 `specifies <ontology::pipeline>` + 顶层 options；2026-08-08 一度给 trigger 加 `<pipeline>` 顶层字段，最终定稿为 **pipeline 收进 content.instant_content**——action/effect 是 trigger，执行内容必须在 content 槽位里。

## 步骤的四种形态 ★

options 的元素可以是：

| 形态 | 写法 | 用途 |
|---|---|---|
| 操作步骤 | `{ "id", "name", "specifies": "<ontology::transfer/state_change/push_track/flip/play>", 字段... }` | 单事件直接执行；可带 `<ontology::condition>`（不成立则跳过） |
| 条件步骤 | `{ "id", "name", "specifies": "<ontology::trigger>", "<ontology::condition>": {...}, "<ontology::content>": {...} }` | 需要判定 + 多个子步骤的复合步骤 |
| 字符串引用 | `"<move_tribe>"` | 无参调用其他概念（action/trigger），do_after 引用时用 `"<move_tribe>"` 全形式 |
| 子 pipeline | `{ "id", "name", "options": [...], "type": "..." }` | 内嵌步骤序列 |

## do_after 语义 ★（2026-08-08 明确）

**do_after 的前置项必须实际结算完毕**——前置步骤因 condition 不成立被跳过 = 未结算，依赖它的步骤不执行。

- 因此「驱逐发生才虚弱」只需 `"do_after": ["displace_occupant"]`，**不要**再加「驱逐实际发生」类 condition——所有 pipeline 都这样写就没完没了
- `_skip`（玩家显式选择不做）例外：选中时 do_after 链视为已完成，后续照常执行
- 条件不成立 = 步骤自动跳过，等同「未结算」

## 三个原子

| 原子 | 写法 |
|---|---|
| options | 候选项数组。里面装什么由具体场景决定——card、resource、transfer、action、子 pipeline、player、zone、甚至 _skip |
| type | 处理策略，引用 `multiple_choice_enum`：EXECUTE_ALL / CHOOSE_ONE / CHOOSE_AT_LEAST_ONE / CHOOSE_ANY |
| do_after | 前置依赖。仅当 do_after 中所有项完成（或跳过）后才可执行 |

## _skip = 跳过

`_skip` 表示「什么都不做」（自描述哨兵）。被选中时跳过执行，但在 `do_after` 链上视为已完成。等价于 `CHOOSE_ONE(做, 不做)`。

JSON 写法：
```json
{ "id": "_skip", "description": { "zh": "不做（跳过此项）", "en": "Skip this option" } }
```

**不用 `null`**——LLM 读不懂 `null` 的语义，必须用带 `id` 和 `description` 的对象。

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

## 实际使用示例

### 可选步骤（build_boat 的登船奖励）

规则书 "you **may** immediately move one of your strong tribes onto the boat"。

```json
{
  "id": "board_tribe",
  "do_after": ["place_boat"],
  "options": [
    { "id": "_skip", "description": { "zh": "不做（跳过此项）", "en": "Skip this option" } },
    { "<ontology::transfer>": { "source": "<territory>", "destination": "<boat>", "<object>": "<tribe>", "quantity": 1 } }
  ],
  "type": "<ontology::multiple_choice_enum.CHOOSE_ONE>"
}
```

### 必选步骤（build_farm 的创意标记奖励）

规则书 "**immediately gain** an idea marker"——没有 may，必做，直接写 step 无需 _skip 包装。

```json
{
  "id": "gain_idea",
  "do_after": ["place_farm"],
  "<ontology::transfer>": { "source": "<ontology::supply>", "destination": "<idea_space>", "<object>": "<idea_marker>", "quantity": 1 }
}
```

## 设计约定

- 不加新字段（不引入 `optional`、`required` 等）。只用 `options` / `type` / `do_after` + `_skip` 哨兵。
- `_skip` 表示跳过，语义为「不做也是一种合法选择」。JSON 中必须写成带 `id` 和 `description` 的对象，不用 `null`。
- 必选步骤直接写，可选步骤包 `CHOOSE_ONE(_skip, step)`。
- 跳过即完成：`_skip` 被选中时 `do_after` 链不阻断。
- `options` 不限定类型——由具体场景决定里面装什么。

**Why:** 这是 pipeline 的唯一正确写法模版。新会话写流程时直接参考，不用重新讨论。
**How to apply:** 写任何多步 action/trigger 的 content 时，先判断每步是 must 还是 may，may 就包 CHOOSE_ONE(_skip, ...)。互斥选用 CHOOSE_ONE，任意组合用 CHOOSE_ANY，全做用 EXECUTE_ALL。
