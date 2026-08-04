---
name: pipeline-model
description: pipeline 多选项处理模型——options/null/type/do_after 的用法、五个典型场景的写法模版
metadata:
  type: project
---

# Pipeline 多选项处理模型

## 核心概念

`<pipeline>` 是多选项处理模型。从几个 token 中选一个拿走，从几个 action 中选一个执行，一个流程有好几个步骤按什么顺序进行——这些都是同一问题。

## 三个原子

| 原子 | 写法 |
|---|---|
| options | 候选项数组。里面装什么由具体场景决定——card、resource、transfer、action、子 pipeline、player、zone、甚至 null |
| type | 处理策略，引用 `multiple_choice_enum`：EXECUTE_ALL / CHOOSE_ONE / CHOOSE_AT_LEAST_ONE / CHOOSE_ANY |
| do_after | 前置依赖。仅当 do_after 中所有项完成（或跳过）后才可执行 |

## null = 跳过

`null` 表示「什么都不做」。被选中时跳过执行，但在 `do_after` 链上视为已完成。等价于 `CHOOSE_ONE(做, 不做)`。

## 五个典型场景

前提：A → B → C 的顺序关系。

### 1. 干完A后，不干B就不能干C

B 和 C 捆一起，外面包 `CHOOSE_ONE(null, B→C)`。

```json
[
  { "id": "a" },
  {
    "do_after": ["a"],
    "options": [
      null,
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

B 无包装（必须），C 包 `CHOOSE_ONE(null, C)`。

```json
[
  { "id": "a" },
  { "id": "b", "do_after": ["a"] },
  {
    "do_after": ["b"],
    "options": [null, { "id": "c" }],
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
    "options": [null, { "id": "b" }],
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

两条独立路径，`CHOOSE_ONE(B→C, null|C)`。

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
        "options": [null, { "id": "c" }],
        "type": "<ontology::multiple_choice_enum.CHOOSE_ONE>"
      }
    ],
    "type": "<ontology::multiple_choice_enum.CHOOSE_ONE>"
  }
]
```

## 实际使用示例

### 简单可选步骤（build_boat 的登船奖励）

规则书 "you **may** immediately move one of your strong tribes onto the boat"。

```json
{
  "id": "board_tribe",
  "do_after": ["place_boat"],
  "options": [
    null,
    { "<ontology::transfer>": { "source": "<territory>", "destination": "<boat>", "<object>": "<tribe>", "quantity": 1 } }
  ],
  "type": "<ontology::multiple_choice_enum.CHOOSE_ONE>"
}
```

### 必选步骤（build_farm 的创意标记奖励）

规则书 "**immediately gain** an idea marker"——没有 may，必做，直接写 step 无需 null 包装。

```json
{
  "id": "gain_idea",
  "do_after": ["place_farm"],
  "<ontology::transfer>": { "source": "<ontology::supply>", "destination": "<idea_space>", "<object>": "<idea_marker>", "quantity": 1 }
}
```

## 设计约定

- 不加新字段（不引入 `optional`、`required` 等）。只用 `options` / `type` / `do_after` + `null` 哨兵。
- `null` 表示跳过，语义为「不做也是一种合法选择」。
- 必选步骤直接写，可选步骤包 `CHOOSE_ONE(null, step)`。
- 跳过即完成：`null` 被选中时 `do_after` 链不阻断。
- `options` 不限定类型——由具体场景决定里面装什么。

**Why:** 这是 pipeline 的唯一正确写法模版。新会话写流程时直接参考，不用重新讨论。
**How to apply:** 写任何多步 action/trigger 的 content 时，先判断每步是 must 还是 may，may 就包 CHOOSE_ONE(null, ...)。互斥选用 CHOOSE_ONE，任意组合用 CHOOSE_ANY，全做用 EXECUTE_ALL。
