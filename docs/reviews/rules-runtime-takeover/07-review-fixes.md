# 规则问答程序接管：五项 review 收口

日期：2026-10-17
分支：`feat/rules-runtime-takeover`
基线：在 `1f6c2ba` 后继续，未换架构

## 结论

五项 review 均已用确定性行为测试收口。真实模型小规模同轮对照本轮**未执行**：当前环境没有可用模型凭据/运行中的 API。因此工具可用性与质量是否退步仍为未知，不能宣称端到端通过。

已执行：

- 后端 xUnit：`266/266` 通过
- Python eval 测试：`10/10` 通过
- 规则内容校验：`0 errors / 72 warnings`
- `git diff --check`：通过

## 1. rule_lookup 改为意图优先排序

修复前：

```text
reference candidates first -> intent candidates appended -> take 3
```

修复后：

- 完整 intent 检索先得到候选及其 score；
- reference 关系只作为召回与同分 tie-breaker；
- 每个 reference candidate 若也在 intent 结果中，使用 intent score，并标记 `intent+reference`；
- 最终统一按 `score desc`、reference tie-break、原始顺序排序，再截取读取预算；
- 目录顺序不再决定前三条正式读取。

新增测试：

`RuleLookup_IntentRanking_IsIndependentOfUnrelatedDirectoryOrder`

- 两条用例分别把真正相关的 `gain_gold` 放在目录第一位和最后一位；
- 三条无关 `pay_gold_*` 规则干扰；
- 两种顺序下 `gain_gold` 都是第一候选，并进入自动正式读取。

## 2. 普通文本不能绕过终态协议

修复前：模型不调用 `submit_answer`、直接返回文本时，程序直接返回并标记 `answer`。

修复后：

- 普通 final 文本在 legacy 规则工具循环内不再被接受；
- 程序追加明确终态协议纠偏，要求下一次调用 `submit_answer`；
- 纠偏次数受既有 `MaxRepeatedFailures` 限制；
- 仍不合法时返回程序生成的 `protocol_error`，不把普通文本计为 answer；
- `submit_answer` 增加 `conversation` 出口：闲聊/非规则回复可以无 evidence 正常结束；
- 因预算/轮次耗尽而进入的总结调用标记为 `unavailable`，不再标记 `answer`。

新增测试：

- `PlainFinalText_IsNotAcceptedAsAnswer_AndFailsBoundedly`
  - 普通文本被拒绝，最终 outcome=`protocol_error`；
  - trace 保留 `plain_final_submission` 和 `terminal_protocol_failed`；
  - 调用次数有界。
- `Conversation_CanExitThroughStructuredTerminalWithoutEvidence`
  - outcome=`conversation`、无 evidence，一次模型调用结束。

同时把既有工具测试改为真实走 `submit_answer`，不再依赖 plain final fallback。

## 3. continuation 有请求内句柄和实际执行路径

修复前：工具返回 `read_evidence`/members offset，但执行器不支持。

修复后：

- `RuleToolResultProjector` 为每个 continuation 生成 `handle`；
- 请求内 `RuleContinuationRegistry` 登记 handle，绑定 operation/entity/scope/offset/limit/include/rules_version；
- 下一次 `execute_plan` 可回传 `continuation_handle`；
- 执行器只接受本请求已登记 handle；伪造 handle 明确报错，不执行任意路径；
- rules_version 变化使 handle 失效；
- 展开到现有查询操作：
  - `members` → `relation=members`，offset/limit 原样传入；
  - `read_evidence` + definition → `relation=explain`；
  - `read_evidence` + catalog/member/flow → 对应 list/members/flow 查询；
  - flow 继续携带 local scope/include。
- 无需复杂 token 系统。

新增测试：

- `MembersContinuation_IsRegisteredAndExecutesNextOffset`
  - 第一页 offset=0/limit=1 返回 handle；
  - 第二次通过 handle 执行，成员 evidence 的 offset=1。
- `ReadEvidenceContinuation_ReexecutesRegisteredTarget_AndDeliversUntruncatedEvidence`
  - 预算裁剪产生 `read_evidence` handle；
  - 第二次通过 handle 正式读取被省略目标，长正文完整交付。
- `UnregisteredContinuationHandle_IsRejectedWithoutExecutingArbitraryTarget`
  - 伪造 handle 不执行查询，返回明确错误。

## 4. 最终证据预算严格兜底

修复前：预算判断后还会加入 evidence ID、candidates、omissions，最终只记录长度，可能超限。

修复后：

- projector 在最终构建后再次按实际序列化长度收紧；
- 依次移除证据并增加显式 budget_truncated omission 和 continuation handle；
- candidates/omissions/evidence ID 元信息也进入预算；
- 仅元信息就超限时返回有界 compact fallback；
- legacy 工具边界在 `AnnotateReferences` 之后再次断言实际长度，必要时降低 projector budget 重试；
- 仍无法满足时返回短小显式 `evidence_budget_exceeded`，不发送截断规则。

新增测试：

- `Projector_FinalSerializedPayload_RespectsBudget_WithManyCandidates`
  - 80 个候选 + 两条长证据，最终序列化长度 ≤ 1800。
- `Projector_MetadataOnlyOverBudget_ReturnsBoundedExplicitFallback`
  - 200 个候选、无证据，预算 1000；返回 bounded fallback 和 budget omission。
- `LegacyToolMessage_RespectsFinalSerializedBudget_AtToolBoundary`
  - 捕获实际发给模型的 tool message，长度 ≤ 1200。

## 5. 耗时统计细分为搜索/读取/导航/装配/序列化/协议

新增请求内 `RuleRuntimeTelemetry`：

- `search`：检索入口；
- `read`：正式概念读取；
- `navigation`：flow index 构建；
- `assembly`：query plan、rule candidate selection、BuildOkResult、evidence projection；
- `serialization`：最终 tool result 序列化+引用注解；
- `protocol`：终态协议纠偏 span；
- 模型 span 区分：
  - `model_rule_planning`
  - `model_answer_submission`
  - `model_unstructured_final`
  - `model_protocol_repair`
  - `model_final_answer`

`tools/eval/compare_versions.py` / `analyze_compare.py` 现在提取 search/read/assembly/serialization/protocol span 均值，并保留 timeline overlap/covered/unaccounted 对账。

新增/更新测试：

- `LegacyRequest_RecordsModelAndToolSpans_ThatReconcileWithTotalElapsed`
  - rule_lookup 请求的 trace 同时包含 search、read、assembly、serialization、model_answer_submission；
  - timeline 仍对账。
- `PlainFinalText_IsNotAcceptedAsAnswer_AndFailsBoundedly`
  - trace 含 protocol span 和 model_unstructured_final。

## 6. 留出集表述纠正

采纳纠正：没有在实现前建立，不等于永远不能建立有效新测试集。

正确流程应为：

1. 先冻结当前候选提交（含本轮五项修复）；
2. 独立依据规则书、真实使用场景和未见输出设计 36 题；
3. 写清语义判分标准、可接受条件式答案和证据组合后封存；
4. 再运行候选 vs 冻结基线的同轮实验；
5. 若根据这批题调参，该批题立即转为开发集，不能再称留出集。

因此下一轮应先生成真正独立的新题集，再做小规模真实同轮对照；不能把当前实现阶段当作永久不能建集。

## 7. 当前指纹与剩余不确定性

- legacy candidate prompt SHA：`679afb5b71f81b1cd11128dd012bc40b2e9a9cb957377e1cf3becf58aaf18dc8`
- tool schema 指纹（含 submit_answer/continuation_handle）：`aa308e55197d`

未知项：

- 无真实模型运行：模型是否遵守 submit_answer/continuation_handle 未知；
- 质量是否退步未知；
- 本轮新增 span 的实际端到端占比只能在真实调用后测；
- 新 36 题尚未建立；
- Android/TTS 仍未跑（环境缺 Android SDK）。

这些未知项不影响确定性门禁通过，但不能被写成端到端通过。
