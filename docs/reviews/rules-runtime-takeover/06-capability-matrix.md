# 规则问答程序接管 06：内容接入能力检查表

由 `python3 tools/eval/rules_capability_report.py --out ...` 从当前内容确定性生成，完整 JSON 见 [06-capability-matrix.json](06-capability-matrix.json)。

| 游戏 | scoped flow | 成员依据 | condition/cost/target | score_table | unresolved refs | duplicate flow ids | actionable gaps |
|---|---:|---:|---:|---:|---:|---:|---|
| agricola | 是 | 是 | 是 | 是 | 5 | 否 | unresolved refs |
| ark-nova | 是 | 是 | 否 | 是 | 28 | 否 | unresolved refs, no structured cost/condition |
| brass-birmingham | 是 | 是 | 是 | 否 | 5 | 否 | unresolved refs, no score_table |
| castles-of-burgundy | 是 | 是 | 是 | 是 | 4 | 否 | unresolved refs |
| civolution | 是 | 是 | 否 | 是 | 104 | 是 | duplicate flow ids, unresolved refs, no structured cost/condition |
| puerto-rico | 是 | 是 | 否 | 是 | 21 | 否 | unresolved refs, no structured cost/condition |
| seasons | 否 | 是 | 否 | 否 | 0 | 否 | missing flow file, no structured condition/cost/score_table |
| splendor | 是 | 是 | 是 | 否 | 7 | 否 | unresolved refs, natural-language-only rules |
| wingspan | 是 | 是 | 否 | 否 | 17 | 是 | duplicate flow ids, unresolved refs, no structured cost/condition/score_table |

## 解释

- `scoped flow` 只表示 flow.json 有可识别节点；不等于每个节点都有完整分支/循环语义。
- `成员依据` 表示存在显式 `members/items/options/slots` 或 schema `specifies/extends/instance_of` 关系。即使有依据，也不证明未录入成员存在。
- `condition/cost/target` 是结构化字段探测；没有字段的规则仍可从完整自然语言描述解释，但不能被 `compute` 当确定性裁判。
- `unresolved refs` 是当前 `<...>` 引用中无法在游戏概念或 ontology 概念中解析的数量，需人工区分 schema 枚举值、占位符和真实缺失引用。
- `duplicate flow ids` 不是失败；规范路径可区分身份，但不能再用局部 id 当唯一键。
- 这些是 actionable content gaps，不是要求一次性补齐九款游戏数据。
