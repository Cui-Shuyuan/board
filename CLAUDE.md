# Board AI — 桌游规则操作系统

## 项目记忆

**先读取 memory/ 目录下的所有文件了解项目全貌。** 如需了解原始讨论细节，可读根目录下的 `聊天记录.txt`。

关键文件：
- `memory/project-overview.md` — 核心架构、应用场景、开发阶段
- `memory/ontology-design.md` — 本体 JSON 约定、当前进度、待写列表
- `memory/user-preferences.md` — 用户的设计偏好与工作方式

## 当前状态

**第一阶段「定义世界模型」已完成。** 桌游本体的 JSON 建模 45 个概念全部就位，覆盖 Level 0 ~ Level 3。下一阶段：Rule DSL。

核心文件：
- `ontology/ontology.json` — 统一本体定义，45 个概念（Level 0 ~ Level 3）
- `Board Game Structure Ontology v0.md` — 静态结构概念的原始定义（权威参考）
- `Board Game Procedure Ontology v0.md` — 流程时序概念的原始定义（权威参考）

已完成（45 个概念）：
- Level 0（7）：Object、Zone、State、Property、Event、Condition、Procedure
- Level 1 Structure（5）：Player、Resource、Piece、Aid、Token
- Level 1 Procedure（4）：Round、Turn、Phase、Transfer
- Level 1 State（1）：Ownership
- Level 1 Property（4）：Cost、Content、Effect、Information Visibility
- Level 1 Zone（3）：Reserve、Discard Pile、Player Zone
- Level 1 Event（4）：Action、Trigger、Resolve、Shuffle
- Level 1 Condition（2）：Endgame Condition、Victory Condition
- Level 2 Piece（2）：Card、Tile
- Level 2 Reserve（4）：Supply、Market、Deck、Pool
- Level 2 Player Zone（2）：Player Holding、Development Area
- Level 2 Aid（4）：Public Board、Private Board、Player Aid、Rulebook
- Level 2 Action（2）：Activation、Play
- Level 3 Player Holding（1）：Hand

## JSON 结构约定

概念的自身字段声明在顶层（字段名即 key），`constraints.required/optional` 是子类需实现的字段 ID 列表：

```json
{
  "id": "supply",
  "level": 2,
  "parent": "reserve",
  "abstract": false,
  "definition": { "zh": "...", "en": "..." },
  "owner": { "type": "player | null", "default": null, "description": "..." },
  "visibility": { "type": "information_visibility", "default": "public", "description": "..." },
  "constraints": { "required": [], "optional": [] }
}
```

- `type` 可以是概念引用（`player`、`zone`）、泛引用（`concept_ref`）、或原始类型
- `definition` 和 `description` 中用 `<concept_id>` 标记交叉引用
- `id` 字段特殊：由 Object 定义，所有实例隐式拥有，不在概念顶层声明

## 工作约定

- 新增概念前先查两个 v0 文档确认原始定义（我们现在是做翻译工作，不是从零开始写）
- 每个满意节点用 git commit
- 代码/数据优先考虑程序确定性，LLM 只做语言理解
- 中文为主语言，英文备选
- 所有概念在 `ontology/ontology.json` 中统一定义，不再分拆文件
