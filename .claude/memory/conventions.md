---
name: conventions
description: 规则 JSON、本体、pipeline、命名与 QA 的现行规范（精简自旧 memory）
metadata:
  type: project
---

# 规则编写规范

## 1. 校验是前提

写完任何 `concepts.json` / `flow.json` / `ontology/concepts.json` 后必跑：

```bash
python scripts/validate_rules.py [--game <game>] [--errors-only]
```

当前全库 0 errors / 72 warnings。被 hook 使用时只关心 ERROR。JSON 是程序运行前提，不是给人看的草稿。

JSON 格式：
- UTF-8 无 BOM，LF 换行，2 空格缩进，末尾换行，无行尾空白。
- 需要规范化时用 `scripts/normalize_json.py`。
- `id` 唯一；中文名精确重复会被 W06 警告，可能造成实体解析歧义。

## 2. 本体结构

文件：`ontology/concepts.json`。

每个概念包含：
- `id`、`name`（zh/en）、`abstract`、`definition`、`constraints`。
- 字段声明在顶层：字段名即 JSON key，如 `"owner": { ... }`。
- `constraints.required` / `optional` 是子类需实现的字段 ID 列表。
- `id` 字段由 Object 定义，所有实例隐式拥有，不在概念顶层声明。

三种层级关系：
- `extends`：结构扩展，增加父概念没有的字段。
- `specifies`：参数绑定，只填充父概念已有字段。
- `instance_of`：具体个体，字段全满，是终端。

三者可链化，`extends` / `specifies` 可任意深度交替。

## 3. 类型与引用

- 概念引用统一写 `<concept_id>`，可带 namespace：`<ontology::concept_id>`。
- 数组标记直接写进 key：`"<event>[]"`。
- 字段 key 本身是 `<concept_id>` 时，不再重复写 `type`；只有语义化 key 或需要窄化类型时才写 `type`。
- 可空性由 `constraints.optional` 或 `default: null` 表达，`type` 不再写 `| null`。
- `concept_ref` 已弃用，统一用 `<concept_id>`。
- `definition` 和 `description` 中用 `<concept_id>` 标交叉引用。
- 引用条目是纯引用且语义相同时可省略 description；不同语境下含义不同时才写 description。

## 4. ontology 纯净与复用

- ontology 是几十上百款游戏的通用基础，定义中不得出现具体游戏专名/例子/具体路径。
- 需要举例用占位符，如 `<concept>`、`<track>.<part_id>`。
- 已有 ontology 概念时，游戏层直接引用 `<ontology::concept_id>` 或声明实例，不要新建 `<game_xxx>` 包装。
- 只有 ontology 确实无法覆盖的机制才向 ontology 扩展。
- 已在 `flow.json` 中定义的 action/trigger/phase，不重复写进 `concepts.json`。

## 5. Trigger / Effect / Action

- 三者共享同一结构：`condition → cost → target → content`。
- 纯 Trigger：condition 边沿激活，规则自动执行。
- Effect：specifies trigger，绑定在 piece 上，由 condition 激活。
- Action：specifies trigger，不依赖 piece，由玩家决策点燃。
- `Activation` specifies Action，将 target 窄化为“选哪个 effect”。
- `content` 类型是 `<trigger> | <content>`，递归必然终结于：
  - `instant_content`：边沿语义，一次性 resolve 成 Event。
  - `continuous_content`：电平语义，进入生效池按 `active_condition` 维持。
- 判据：内容写历史（不可撤销的状态变更）→ instant；参与计算（派生值修饰）→ continuous。
- 真正的 continuous_content 没有“每次”，只有“只要”。

## 6. Pipeline / 流程

- `<pipeline>` 是唯一流程结构原语，round/turn/phase 多步骤时都用它。
- 单内容不套 pipeline；直接持有对应概念。
- Pipeline 四要素：`options`、`type`、`do_after`、`loop`。
- 步骤级 condition 不成立 = 阻断；候选级 condition 不成立 = 排除。
- `loop` 挂在 procedure 顶层：
  - `{ "count": N }`：定次循环。
  - `{ "until": <condition> }`：条件循环。
- Round 缺省无 loop = 1 次 = 每位玩家按座次各行动一轮。
- 一个动作不叫 pipeline；直接写 `"<ontology::action>": { "options": [...], "type": "CHOOSE_ONE" }`。
- `children` / `actor` / `start` / `end` 已废弃。

## 7. 常出现的结构

- 终结形态平级并列：`content` 下 `instant_content` + `continuous_content` 可平级并列；cost/effect 同理。
- 多选：`options` 必含 `type`（引用 `<multiple_choice_enum>`）和 `items`。
- 轨道：
  - `track` extends zone，`slots` 必填（数值轨写范围字符串，槽位轨写对象数组）。
  - `linear_track` 有终点，挂 overflow/underflow compensation。
  - `circular_track` 无终点，挂 lap_event。
  - `score_track` extends circular_track。
- 替代/视为：用 `<substitution>`，声明在对象定义处，scope 写路径式定位，不在使用处重复。
- 升级：`<upgrade>` specifies `<state_change>`，attribute 固定为 level。
- Lose / Gain：对象失去/获得 property，作为 Event 子类。
- 实体承载 zone：card / board 可声明 `zones`，zone 生命周期绑定宿主。

## 8. Effect 生命周期

四阶段：部署 arm → 监听 monitor → 触发 fire → 结算 cost → target → content。

- `instant_effect`：部署即触发，一次机会。
- `continuous_effect`：先部署后监听，condition 满足时反复触发。
- 禁用组合：`continuous_effect` + `continuous_content`（监听 + 电平结构空洞）。
- 效果归属：附属 piece（随来源离场）vs 独立注册（永久存在），由挂载位置隐式表达。

## 9. QA 与 description

- FAQ 只当测试，不当真理来源。好坏程序不需要 FAQ 也应能推理出正确答案。
- 数据口径以规则书 + 用户裁决为准。
- 禁止为了 FAQ 通过把答案口径塞进 description（过拟合）。
- QA 答错时只修真实数据 bug（有规则书支持）或检索层（aliases/正则）。
- FAQ 与规则书/用户冲突时，FAQ 可能错，以规则书/用户为准。

## 10. 动画 cue 的 Board API 合法性问答（人工步骤）

- 每改一个真的改状态的 cue，必须由 AI/人根据改动点**手写最小事实问题**，问运行中的 Board API `POST /api/chat`，判断这条 cue 的状态前提、动作和结果是否合法。
- 一 cue 一事，只带判定需要的事实；不用教程自造词，不整桌抄状态。规则自动发生的事就说成自动。
- 问题提前写进该 cue 的 `qa` 字段（历史问题可继续放 `_qa/questions.json`）；`scripts/qa_anim_ask.py` 支持直接从 `full.anim.json` 提取这类问题并自动发送/留档；脚本只负责问答与日志，**不负责生成问题**。
- 本地 `validate_anim_rules*` 是精确算术层；Board API 问答是独立裁判层。两层都过，这一步才算站得住。
- `offstage` / 可见性隐藏只用于引擎确实需要的隐藏；不得用来把非法棋盘状态“画成合法”。任何隐藏后，每色宝石、黄金、卡牌实物总数仍必须守恒。

## 11. 相关旧文档

详细推演和学习笔记已归档：
- `.claude/archive/memory/2026-09-23/ontology-design.md`
- `.claude/archive/memory/2026-09-23/json-writing-conventions.md`
- `.claude/archive/memory/2026-09-23/pipeline-model.md`
- `.claude/archive/memory/2026-09-23/ontology-direct-use.md`
