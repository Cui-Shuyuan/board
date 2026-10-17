# 规则问答程序接管：续读分页与自身耗时收口

日期：2026-10-17
分支：`feat/rules-runtime-takeover`
前置提交：`33aefce`

## 结论

两项 review 已收口：

1. `rule_lookup` continuation 现在真正读取下一页，并保存原始 intent；
2. 时间线保留包含子调用的 inclusive duration，同时新增扣除子调用后的 `SelfDurationMs` 用于成本分析；协议修复成为独立标记，即使修复调用最终成功提交 answer 也会被统计。

后端 xUnit：**270/270 通过**；Python eval：**10/10 通过**。真实模型小规模同轮对照仍未执行。

## 1. rule_lookup 续读分页

### 修复

- `ExecutePlanQueryAsync` 将 `offset`、`limit` 与 `hasExplicitLimit` 传入 `ExecuteRuleLookupAsync`。
- 第一次调用未显式写 limit 时仍默认读 3 条；continuation 显式带 limit 时按 continuation 页大小读取。
- `ExecuteRuleLookupAsync` 从固定 `Take(3)` 改为：
  ```text
  ruleCandidates.Skip(pageOffset).Take(pageSize)
  ```
- continuation 的下一页 offset 使用 `pageOffset + pageSize`，limit 使用当前 page size。
- 没有更多候选时不再生成 rule_lookup continuation。
- `QueryContinuation` / `ProjectedContinuation` / `ContinuationRegistration` 新增原始 `intent`。
- continuation handle 的 hash 包含 intent，因此同一对象的“如何获得”和“如何支付”会得到不同句柄。
- registry 展开 `rule_lookup` continuation 时使用登记的 intent，不依赖模型第二次重新填写。

### 测试

- `RuleLookup_ContinuationOffset_ReadsNextPage_AndStopsAtEnd`
  - 六个候选；
  - 第一页读 1–3；
  - continuation offset=3/limit=3 读 4–6；
  - 第二页与第一页无交集；
  - 读完第二页后不再有 rule_lookup continuation。
- `RuleLookupContinuation_PreservesEachOriginalIntent_WhenModelOmitsIntent`
  - 同一对象、三类“如何获得金币”和三类“如何支付金币”交替排列；
  - 获得意图第一页只取获得规则，其续读取支付规则剩余页；
  - 支付意图第一页只取支付规则，其续读取获得规则剩余页；
  - 两个续读请求都没有重新填写 intent，仍保持各自原候选集合与排序。

## 2. 自身耗时与协议修复独立标记

### 修复

- `ChatTimelineSpan` 新增：
  - `SelfDurationMs`；
  - `ProtocolRepair`。
- `ChatTimelineAccountingCalculator.ComputeSelfDurations`：
  - 对每个 span，找出完全包含在其区间内的子 span；
  - 用区间并集扣减，不把嵌套 span 直接重复相加；
  - inclusive `DurationMs` 与 self `SelfDurationMs` 同时保留。
- `ChatOrchestrationTrace` 新增：
  - `ProtocolRepairModelCallCount`
  - `ProtocolRepairMs`
  - `ProtocolRepairSelfMs`
- 模型调用 span 现在区分：
  - `model_rule_planning`
  - `model_answer_submission`
  - `model_unstructured_final`
  - `model_protocol_repair_planning`
  - `model_protocol_repair_answer_submission`
  - `model_protocol_repair_unstructured_final`
  - `model_final_answer`
- 协议纠偏调用带 `ProtocolRepair=true`；即使该调用最终成功 `submit_answer`，仍计入纠偏调用数/耗时。
- `compare_versions.py` 同时提取：
  - inclusive `*_span_ms`
  - self `*_self_ms`
  - `protocol_repair_model_call_count` / `protocol_repair_ms` / `protocol_repair_self_ms`
- `analyze_compare.py` 报告同时展示 inclusive 与 self 均值，并以 self 作为程序成本分析口径；协议修复率与修复耗时独立报告。

### 测试

- `SelfDurations_SubtractNestedSpans_InsteadOfDoubleCountingThem`
  - parent 100ms；两个子 span 40ms+20ms；
  - parent self=40ms；三 span self 总和=100ms，没有把 parent 的 100ms 与子调用重复当成独立总成本。
- `LegacyRequest_RecordsModelAndToolSpans_ThatReconcileWithTotalElapsed`
  - 实际 trace 中 plan span 有 self duration；
  - search/read/assembly/serialization/model span 仍存在且时间线可对账。
- `ProtocolRepair_ThatSuccessfullySubmitsAnswer_IsStillMarkedAndCounted`
  - 第一次 plain final 被拒 → 第二次协议修复调用成功 submit_answer；
  - `ProtocolRepairModelCallCount=1`；
  - trace 中存在 `ProtocolRepair=true` 且名称为 `model_protocol_repair_answer_submission` 的 span。

## 3. 下一轮小规模真实对照（未执行）

环境当前无可用模型凭据/API，因此以下只登记，不宣称通过：

- 基线：`ffac498`
- 候选：本轮最终提交
- 开发集：现有 31 题
- 每版重复 3 次，同轮交错
- 计划聊天数：31 × 2 × 3 = **186**
- 优先指标：
  - 答案质量与双评共识；
  - 协议纠偏率/修复耗时；
  - continuation 是否取得新证据，而不是重复第一页；
  - 独立 self time 口径下的模型/搜索/读取/装配/序列化耗时。
- 新 36 题继续独立封存，不进入这轮调试；若用其结果调参，该批题转为开发集。

## 4. 当前未知

- 模型是否实际正确使用 `continuation_handle`，仍需真实调用验证；
- 186 次小规模对照尚未执行，质量是否退步未知；
- self duration 只有在实际 trace 上才能说明真实成本占比；
- Android/TTS 仍受本机 Android SDK 缺失阻塞。
