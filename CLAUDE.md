# Board AI — 桌游规则操作系统

## 项目记忆

**先读取 .claude/memory/ 目录下的所有文件了解项目全貌。** 如需了解原始讨论细节，可读根目录下的 `聊天记录.txt`。

关键文件：
- `.claude/memory/project-overview.md` — 核心架构、应用场景、开发阶段
- `.claude/memory/ontology-design.md` — 本体 JSON 约定、当前进度、待写列表
- `.claude/memory/splendor-progress.md` — 璀璨宝石规则定义进度
- `.claude/memory/user-preferences.md` — 用户的设计偏好与工作方式

## 当前状态

**第一阶段「定义世界模型」已完成。** 桌游本体的 JSON 建模 66 个概念全部就位，可持续补充。

**第二阶段「Rule DSL」进行中。** 首个游戏：璀璨宝石（Splendor），规则文件为 `games/splendor/concepts.json`（概念层已完成：objects/actions/triggers/conditions）和 `games/splendor/flow.json`（流程层，结构讨论中）。

核心文件：
- `ontology/ontology.json` — 统一本体定义，66 个概念
- `games/splendor/concepts.json` — 璀璨宝石概念定义（objects 22、actions 4、triggers 4、conditions 13）
- `games/splendor/flow.json` — 璀璨宝石流程定义（待定）
- `Board Game Structure Ontology v0.md` — 静态结构概念的原始定义（权威参考）
- `Board Game Procedure Ontology v0.md` — 流程时序概念的原始定义（权威参考）

已完成（66 个概念）：
- 基础概念（8）：Object、Zone、State、Property、Event、Condition、Timing、Procedure
- 结构概念（5）：Player、Resource、Piece、Aid、Token
- 流程概念（4）：Round、Turn、Phase、Transfer
- 状态概念（2）：Ownership、Starting Player
- 属性概念（5）：Cost、Content、Effect、Declaration、Information Visibility
- 区域概念（3）：Reserve、Discard Pile、Player Zone
- 事件概念（4）：Action、Trigger、Resolve、Shuffle
- 条件概念（2）：Endgame Condition、Victory Condition
- Piece（2）：Card、Tile
- Reserve（4）：Supply、Market、Deck、Pool
- Player Zone（2）：Player Holding、Development Area
- Aid（4）：Public Board、Player Board、Player Aid、Rulebook
- Action（1）：Activation
- Event（1）：Play
- Content（2）：Instant Effect、Continuous Content
- Token（1）：Starting Player Marker
- Player Holding（1）：Hand

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
