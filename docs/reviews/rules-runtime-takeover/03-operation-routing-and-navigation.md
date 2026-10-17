# 规则问答程序接管 03：操作路由、自动正式读取和有界导航

日期：2026-10-17
依赖：01、02
提交：本阶段独立提交

## 1. 程序实际接管了哪一步

新增了两个实际执行的 relation，并把已有 relation 拆成明确操作：

| 能力 | 状态 | 程序职责 |
|---|---|---|
| `explain` / describe | 已实现 | 精确对象/节点定义正式读取 |
| `condition` | 已实现 | 读 `<ontology::condition>`、`<ontology::cost>`、`target`，并按显式 `<ref>` 展开一层正式定义 |
| `cost` | 已实现 | 只读 `<ontology::cost>` 与显式引用，不把 description 当费用 |
| `target` | 已实现 | 只读 `target` 与显式引用 |
| `rule_lookup` | 已实现 | 保留对象身份，用完整 intent 找规则候选，程序自动正式读取有界候选（默认 3） |
| `compute` | 部分支持 | 复用现有 `RulesFactService` 的容量公式、quantity.numeric、score_table；无支持公式或参数不完整时显式 partial/unsupported |
| `flow` / `members` | 02 已实现 | 局部结构、canonical path、分页成员 |
| `ordering` / `boundary` | 已实现（沿用共享工具） | 顺序、兄弟位置、loop、边界字段 |
| 任意规则自动裁判/公式生成 | 不支持 | 不引入脚本执行，不把自然语言自动编译为裁判 |

`rule_lookup` 的两个事实保持分离：

```text
candidate_read = 正式内容已读到、出处可验证
target_resolved = 当前对象/操作身份已确定
```

- exact 对象身份进入 `ResolvedTargets`；被读取的规则候选不会进入 `ResolvedTargets`。
- 每个候选 payload 带 `role=rule_candidate`、`target_resolved=false`、`read_status=auto_read`、`reference_edges`。
- 候选多出读取预算时返回 `budget_truncated` + continuation；没有静默截断。
- 对象 exact hit 不吞噬规则查找：`rule_lookup` 同时交付对象定义和候选规则正式内容。

导航边只从 schema 字段和显式引用产生：

| 字段 | edge_type |
|---|---|
| `condition` / `<ontology::condition>` | `condition` |
| `cost` / `<ontology::cost>` | `cost` |
| `target` | `target` |
| `content` / `effect` | `effect` |
| `description` | `mention` |

`mention` 只表示文本提到资源；不能据此推导“获得”或“支付”。condition/cost/target 的依赖定义是一层显式读取，最多 8 个；超出附 `budget_truncated`，缺失引用附 `no_rule_coverage`。

## 2. 模型少做了哪一步

模型现在可以一次提交：

```json
{"relation":"rule_lookup","entity":"bird_card","intent":"鸟牌怎么获得"}
```

程序在同一 plan 内完成规则候选检索、最多 3 条候选的正式读取，并保留“读到候选但相关性未确认”的状态。模型不需要：
- 看到对象 exact hit 后就结束查询；
- 再发一次 explain 才能拿到候选正文；
- 自己从工具返回的描述文本组装“获得/花费/提到”的方向；
- 自己猜默认参数做计算；缺公式/缺参数由程序给出 partial/unsupported。

本阶段仍未跑真实模型同轮对照，因此未证明调用数或 token 下降；收益结论为未知。

## 3. 行为测试

新增 `RuleLookupAndNavigationTests`：

- `RuleLookup_PreservesExactObjectIdentity_AndAutoReadsRuleCandidates`
  - `explain` 只返回对象定义；
  - `rule_lookup` 保留 `crystal` 身份，同时自动读取候选；
  - 候选 payload 的 `target_resolved=false`；
  - 候选读取数量=3，超出预算产生 `budget_truncated`。
- `RuleLookup_WithoutExactIdentity_KeepsCandidatesUnresolvedAfterFormalRead`
  - 无对象 exact identity 时，即使候选已正式读取，status 仍为 unresolved、`ResolvedTargets` 为空。
- `CostAndTarget_ReadOnlyDeclaredFields_AndExpandFormalDependencies`
  - cost 只给 Cost，target 只给 Target；
  - 显式引用产生 dependency definition 与 edge；
  - 没有 cost 字段时 coverage=partial + `field_missing`。
- `MissingAndBudgetedDependencies_AreExplicitRatherThanDropped`
  - 缺失依赖 `no_rule_coverage`；
  - 超过 8 个依赖 `budget_truncated` + continuation；
  - 未静默扩大预算。
- `Compute_ReturnsSupportedFormulaOrExplicitUnsupportedAndMissingParameters`
  - 支持公式返回 computed fact；
  - 缺参数 partial；
  - 无支持公式 unsupported。
- `RealGames_RuleLookupAndCost_UseDeclaredStructures`
  - Agricola `build_rooms` cost 与正式依赖；
  - Wingspan `bird_card` rule_lookup 自动读取 `draw_cards_action` 等 flow trigger；
  - Puerto Rico `builder_role` 没有结构 cost 时返回 partial，而不是编造费用。

测试结果：后端 `244/244` 通过。

冻结真实查询输出见 [03-frozen-query-contracts.json](03-frozen-query-contracts.json)：

| 查询 | 结果 |
|---|---|
| Agricola `cost:build_rooms` | exact/complete；对象 + 显式依赖定义；无 omission |
| Wingspan `rule_lookup:bird_card` | exact/partial；自动读 3 条候选 `play_bird/draw_cards_action/refill_bird_tray`；`budget_truncated` |
| Agricola `compute:pasture` | exact/complete；definition + computed_fact |

## 4. 提示与 schema 指纹

- schema 增加 `rule_lookup/cost/target/compute` relation。
- legacy candidate prompt 说明对象 exact 不等于规则操作已完成，并要求 rule_lookup 携带完整原问。
- 当前候选 prompt SHA：`86c223c105232ee4c0094a58871fe22d308a3f2c6914e1d990d5454e826924f7`。
- 当前 tool schema 指纹：`75a96123cdf2`。
- 冻结 `a992cfe/ffac498` prompt SHA 仍是 `859260e5…f1343d5`，未被新候选冒用。

## 5. 剩余不确定性

- `rule_lookup` 自动读取解决“少一次机械读取”，但不解决候选相关性；读取到 2–3 条候选仍可能需要模型语义判断。
- 当前反向引用索引对 flow 只扫描声明为 action/trigger 的节点，避免父流程容器因嵌套引用全部命中；这是保守策略，可能漏掉一些以 flow_node 表示的规则。
- condition/cost/target 只展开一层显式引用，不是完整递归依赖图；更深依赖仍可能需要合法续读。
- `compute` 仍以现有事实服务的公式/正则能力为边界，未做完整参数 schema；缺参数只能标记 partial，尚不能逐项列出所需变量。
- 无真实模型同轮质量、调用次数、延迟数据。
