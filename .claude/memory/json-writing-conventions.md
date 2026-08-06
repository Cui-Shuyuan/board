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

Action 必须遵循 `condition → cost → target → content` 结构：

```json
{
  "id": "build_farm",
  "specifies": "<ontology::action>",
  "name": { "zh": "建造农场", "en": "Build Farm" },
  "description": { "zh": "...", "en": "..." },
  "<ontology::condition>": {
    "options": [ { "description": { "zh": "谓词 1" } }, { "description": { "zh": "谓词 2" } } ],
    "type": "<ontology::multiple_choice_enum.EXECUTE_ALL>"
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

- **condition**：`options`/`type`/`EXECUTE_ALL` 结构。每条谓词用自然语言描述
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

写一个新 action/trigger 时确认：
- [ ] 有 `name: { zh, en }`（紧跟 id 之后）
- [ ] condition 用了 options/type/EXECUTE_ALL 结构
- [ ] cost 是 instant_cost/continuous_cost；无需支付则省略字段（不写 null）
- [ ] target 是字符串，不是对象
- [ ] content 是 `<ontology::instant_content>` 或 `<ontology::continuous_content>`
- [ ] 所有 type 引用是完整路径 `"<ontology::multiple_choice_enum.XXX>"`
- [ ] 没有 `"type": "<ontology::xxx>"` 这种写法（用 key-as-type）
- [ ] destination 用 `this.target` 而非写死 zone
- [ ] 描述字段叫 `description` 不叫 `definition`
- [ ] 必选/可选：may 就包 CHOOSE_ONE(null, step)

**Why:** 后端索引、查询、语义匹配全部依赖这些约定。违反任何一条都会导致 LLM 搜不到或理解错误。
**How to apply:** 写任何新概念/action/trigger 时逐项对照检查清单。
