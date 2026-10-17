# 规则问答程序接管 05：请求账本、终态协议和有界调度

日期：2026-10-17
依赖：01–04
提交：本阶段独立提交

## 1. 程序实际接管了哪一步

### 请求账本

单次聊天请求新增 `RuleRequestLedger`：

- key 包含当前规则版本、工具名和规范化后的 execute_plan JSON；
- 参数对象按 key 排序后规范化，但数组顺序保留，避免把不同 query 顺序/不同参数误合并；
- 完全相同计划再次出现时复用已有工具结果，重新插入一条 tool message 以保持当前模型可见，但不重新执行底层查询/读取；
- 不同 scope、不同 entity、不同分页参数不会复用；
- 记录实际工具执行数、复用数、交付证据 id/字符数、候选自动读取数量、协议错误数；
- 同一证据在 evidence collector 中只记一次，避免 tier/evidence 数量被重复 plan 放大。

### 终态提交

新增 `submit_answer` 工具，和 `execute_plan` 在同一工具循环、同一模型调用中工作：

```json
{
  "outcome": "answer | clarify | unavailable",
  "text": "...",
  "evidence_ids": ["ev_..."],
  "clarification": { "missing_kind": "...", "requested_information": "..." },
  "unavailable_reason": "no_rule_coverage | unresolved_identity | budget_exhausted"
}
```

程序校验：

- outcome 合法；
- answer 至少引用一个本请求实际交付的 evidence_id；
- 伪造/未交付/不存在 evidence_id 返回明确错误，进入有界修复；
- clarify 可无引用，但 clarification 字段若提供则必须完整；
- unavailable 必须有合法原因；
- submit_answer 是终态：校验通过直接返回文本，不再追加检查模型调用；校验失败才进入修复，受既有 `MaxRepeatedFailures` 和总轮次约束。

### 删除候选兜底门禁

- 不再因为“存在 unresolved candidate”就强制模型再发一次同 ID 查询。
- 纯指代歧义可由模型选择 `clarify` 结束。
- 已 auto_read / 有 evidence_id 的候选正文可直接用于相关性判断；formal read 仍不等于 target_resolved。
- 旧 plain final message 保留为兼容 fallback，但新提示要求规则答案走 submit_answer。

## 2. 模型少做了哪一步

模型不再必须：

- 为清空程序内部 pending 再确认一次；
- 为证明“候选已读”再发同 ID explain；
- 对相同 query 反复触发底层检索和读取；
- 在终态提交后再承担一次“检查答案”的模型调用。

本阶段用模拟 LLM 路径证明调用/复用行为；真实模型是否遵循 submit_answer、能否减少端到端调用，仍为未知。

## 3. 同轮模拟轨迹

见 [05-orchestration-traces.json](05-orchestration-traces.json)：

| 场景 | 模型调用 | 实际执行工具 | 复用 | 交付证据 | 终态 |
|---|---:|---:|---:|---:|---|
| auto_read_then_answer | 2 | 1 | 0 | 1 | answer |
| duplicate_plan_reuse | 3 | 1 | 1 | 1 | answer |
| clarify_without_evidence | 2 | 1 | 0 | 0 | clarify |

三条轨迹的 `TimelineAccounting.IsReconciled=true`。

关键结论：

- 同文重复 plan 的模型调用仍发生，但程序实际执行工具从 2 降到 1；缓存命中的完整工具消息再次出现在当前上下文中，不是只返回旧 evidence ID。
- 终态 answer 在第二个模型调用内完成，没有额外 summary/检查调用。
- clarify 无 evidence 且不会被协议修复拒绝。

## 4. 预算

沿用并显式记录已有默认值，未照搬 program 固定四次：

| 预算 | 默认 |
|---|---:|
| MaxToolRounds | 6 |
| MaxToolCallsPerRound | 4 |
| MaxTotalToolCalls | 12 |
| MaxRepeatedFailures | 3 |
| MaxRequestSeconds | 60 |
| MaxEvidencePackChars | 60000 |

新增协议错误只使用 `MaxRepeatedFailures` 和既有总轮次；没有新增无限重试。`RepeatedInvalidTerminalSubmissions_StopAtHardLimit_WithoutInfiniteLoop` 验证重复非法终态在硬上限内退出。

## 5. 行为测试

新增 `RequestLedgerAndTerminalProtocolTests`：

- `AutoReadCandidates_ThenSubmitAnswer_NeedsNoExtraConfirmationRound`
  - 结果 + 一条正式证据 → submit_answer；2 次模型调用、无候选确认纠正。
- `LegalClarify_WithNoEvidence_IsAcceptedWithoutRepairRound`
  - 合法 clarify 无 evidence 也可终态结束，2 次调用。
- `ForgedEvidenceId_GetsBoundedProtocolRepair_ThenValidSubmissionCompletes`
  - `ev_forged` 被拒；模型收到明确错误后有一次修复机会；合法 ID 后 answer。
- `ExactDuplicatePlan_IsReused_WithoutSecondBottomExecutionOrEvidenceDuplication`
  - 底层工具执行 1 次、复用 1 次；QueryEvidence 不重复；重复 tool message 正文相同。
- `DifferentScope_IsNotReusedAsSameQuery`
  - 同 relation 不同局部 scope 不复用。
- `SelectionQuestion_CompletesWithoutUnrelatedPlayerCountClarification`
  - 选择问题正常 answer，不额外注入人数追问。
- `RepeatedInvalidTerminalSubmissions_StopAtHardLimit_WithoutInfiniteLoop`
  - 非法终态不无限循环。

旧的“只要 unresolved 候选就强制确认”测试改为：
`ProcessAsync_UnresolvedCandidate_NoLongerForcesMechanicalConfirmationRound`。

测试结果：后端 `256/256` 通过；Python eval 脚本 `8/8`。

## 6. 提示与指纹

- legacy candidate prompt 说明 execute_plan + submit_answer 两个工具、已 auto_read 候选不重复确认、answer 必须引用已交付 evidence_ids、clarify/unavailable 的出口。
- 当前候选 prompt SHA：`bae4a8d7d9c9ef44dc27b95e4cb0ef4fc53ccc66278679037b84d26397f749a0`。
- 当前 tool schema 指纹（含 submit_answer）：`6cdb474ff0d2`。
- 冻结基线 prompt SHA `859260e5…f1343d5` 仍是历史基线，未被当前提示冒用。
- Android `QaAnswerEvidence` 已经解析 `orchestrationOutcome`，answer/clarify/unavailable 均按字符串兼容；本阶段未改 Android 协议，因此未另跑 JVM。该运行项为未执行，不是通过。

## 7. 剩余不确定性

- 没有真实模型同轮运行；模型可能继续用 plain final，导致 submit_answer 结构化校验未生效。需要 06 的端到端实验判断。
- 工具结果复用只发生在同一请求内；跨请求缓存/可见性未实现。
- continuation 仍是 omission 中的结构化续读指令，尚未做成不可伪造 token 并由服务端执行；这仍是已知缺口。
- 当前没有 provider token 级对账；证据字符数、模型 usage 和最终延迟仍需 06 汇总。
- `submit_answer` 的 evidence ID 校验只证明出处存在和已交付，不能证明文本被该来源蕴含，答案质量仍靠离线评审。
