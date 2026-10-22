> 历史路径说明（2026-10-23）：本文保留当时本地 scratch 路径 `.tmp/...`，这些不是当前运行依赖。新实验请使用 `$BOARD_RUNTIME_ROOT/tmp/...`；2026-10-23 之前的原始 agent/eval 产物已压缩到 `$BOARD_RUNTIME_ROOT/archive/agent-workspace-evidence-20261023-004025.tar.zst`，需要时解压后按原相对路径读取。

# Program 问答第二轮：事实包修复、基线隔离与分阶段诊断

日期：2026-10-17。执行对象：DeepSeek V4.1 Flash。对应计划：`archive/plans/2026-10-17/program-qa-evidence-repair-and-diagnosis-plan.md`。

## 1. 结论

- **默认仍为 legacy。** 31 题诊断集 × 3 次（186 次聊天、双盲评委保守共识）中，legacy 严格回答通过率 68.8%，修复 program 37.6%，点估计差 -31.2pp，95% 配对 Bootstrap CI [-43.0, -19.4]pp。未达到“不差于同轮 legacy”的门槛，因此不进入全量对照、不切换默认。
- **确定性修复与基线隔离已完成。** legacy 提示逐字节恢复为 `a992cfe` 原文（SHA-256 `859260e5…f1343d5`）；program 使用独立提示和阶段工具；非法 `OrchestrationMode` 启动即失败。后端 xUnit 220/220 通过。
- **证据身份、去重、公平预算、投影和可对账 trace 已落地。** 修复后 gold-013（Agricola，非 Splendor）冻结计划的事实包从旧构建器 3 个规范路径/16,029 字符，变为 22 个路径/44,751 字符；旧包没有 expected target `harvest_feeding`，修复包有。固定新包只跑 Respond 时，回答直接给出“收获第二阶段、每人两份、新生儿一份、缺粮拿乞讨标记”等结算内容；固定旧包则明确说没有读到顺序。
- **当前主要失败阶段不是“事实包身份”本身。** 30 个 program unavailable 运行中，17 个去重问题；可观察 trace 的最早阶段提示为 plan/retrieval 23、retrieval→pack 1、respond/证据充分性 1、unknown 5。正例中检索候选规范路径命中仅 50.0%，正式装包命中 71.4%，引用命中 69.0%，答案通过 38.1%。retrieval/计划仍是第一瓶颈，Respond 也仍有独立损失。
- program 调用数下降（均值 2.29，P50 2），但 P50 延迟仍比同轮 legacy 高约 1.2–1.5 秒；本轮不宣称性能已过门槛。没有改 Thinking=low，也没有为了通过测试重跑 798 题或调评分标准。

## 2. 基线与配置隔离（R1）

### 实现

- `LLMOptions` 删除含糊的共享 `SystemPrompt`，拆为 `LegacySystemPrompt` 与 `ProgramSystemPrompt`。
- `appsettings.json` 默认 `OrchestrationMode=legacy`。
- legacy prompt 从 `git show a992cfe:backend/BoardAI.Api/appsettings.json` 原样恢复；测试对原始字符串做 SHA-256 断言：
  - legacy prompt hash：`859260e5ca6ed8f5154b63dde2fbdad61ad1e1e3bd97261108f7339f7f1343d5`
  - program prompt hash：`f5dc50e0b77c5e9beaa61bd8d1676bafc29ed6dee0519e9d288302245b8bb7ff`
- 路径选择显式：只接受 `legacy`/`program`，非法值构造服务时抛出 `InvalidOperationException`，不静默回退。
- 运行时日志记录 mode、提示指纹、配置指纹、模型与 Thinking 档位；不打印密钥或完整提示词。
- program 的公共身份提示与 Understand/Respond 阶段 system 指令分别组装；Understand 阶段只暴露 `submit_understanding`，Respond 阶段只暴露 `submit_answer`。测试捕获实际 messages 和 tool schema，断言两阶段互不串用，且 program 不出现 `execute_plan`。
- 与 `a992cfe` 对比，legacy 路径的 `BuildTools`、trim history、execute_plan schema、上下文消息组装没有本轮之外的改动；本轮只把提示来源改成分字段并增加指纹日志。记忆文档与后端 README 已同步。

### 验证

- `backend/BoardAI.Api.Tests/PromptIsolationTests.cs`：legacy 只发 legacy prompt + `execute_plan` schema；program 两阶段工具隔离；非法模式拒绝；默认 legacy；legacy prompt 精确 hash。
- 真实服务冒烟：legacy 5001、program 5002 均返回 200；日志显示 program 指纹 `f5dc50e0b77c`、配置指纹 `1239a5897762`、model `deepseek-v4-flash`、thinking `low`。

## 3. 证据身份与关联（R2）

### 实现

- 新增 `ConceptPathHit`/`ConceptReadResult` 与 `ReadConceptWithExpansionWithPaths`，正式读取结果携带解析后的 canonical path 和原始节点。
- `related` 使用自己的规范路径；同时携带引用它的父路径，按 `(parent_path, canonical_path)` 保留关联边。正文仍可按内容去重，不丢 relation。
- 直接 matched 路径不再从用户措辞、角色或 evidence 序号生成；构建器会按实际 catalog 命中节点反查 canonical path。找不到或存在多义时会拒绝伪造，并记录 omission。
- owner.slot 直接查询使用完整 `owner.slot`；两个 owner 下同名 slot 隔离。
- 证据去重键为 `game + rules_version + canonical_path + content_hash + relation_projection`。同一路径不同内容产生独立 evidence ID 并写 warning；同一内容不同 relation 投影不互相覆盖。
- 预算满后，已存在正文仍可增加新的父关联边，不重复消耗唯一正文额度。
- `evidence_catalog` 为全局正文表，`questions[].evidence_bindings` 只表达角色/父路径/阅读绑定，不再被当作引用身份边界。Respond 可引用事实包内任一真实 `evidence_id`；这避免了同一条目因不同子问题有重复 ID 时，正确证据被程序误判为越界。

### 验证

- 两个不同 related 各自保留内容与路径。
- 同一 related 从两个父节点读取：正文只 1 条，父关联 2 条。
- 同一扩展中两个 matched 父节点引用同一子内容：保留两个父路径绑定。
- owner 下同名 slot 隔离；直接 slot 的 related 不继承 slot 路径。
- 总内容预算用满后，重复父绑定仍可落包。
- 同 target 不同 relation 保留不同投影；直接与候选路径读取得到相同 Condition/Cost/Target/Source 关键字段。
- 无规范路径的 matched 不入包。

## 4. 读取预算与公平覆盖（R3）

### 实现

- Fill 分阶段：先给每个子问题保证一个 primary 绑定；primary 余项轮询；最后才分配 related。所有阶段都先按读取顺序/角色/rank，不按 evidence_id 字典序。
- 预算计数拆开并写入 trace/stats：
  - `read_attempts`
  - `successful_reads`
  - `unique_contents`
  - `binding_reuses`
  - `pack_items`
  - `omitted`
- 重复内容复用不占 unique-content 额度；预算失败和未读取都写 explicit omission，不伪装成已装包。
- 补充查询后再次执行同一 per-question / total / char 预算；补充绑定合并时若超过子问题条数上限，保留原先绑定并记录 `per_question_budget`。
- 每题的 `coverage_status` 明确为 `evidence_available` / `partial_evidence` / `no_evidence` / `no_match` / `budget_insufficient_metadata` 等。

### 验证

- 首题大量 related 时，第二/第三题仍先获得 primary。
- 多子问题紧预算下每问至少一个绑定，并产生 omitted trace。
- 同一 related 被多个父引用时不重复占正文条目。
- 补充后不超过 per-question 上限。
- 重复预算处理不重复累计 omitted。

## 5. 字符预算、裁剪与投影（R4/R5）

### 实现

- `ApplyCharBudget` 以最终将要发送的序列化包（含 stats/omitted 元信息）为准反复测量。
- 删除顺序：related → candidate → 目标；同类按显式 `rank` 与问题顺序，不用字符串排序。
- 仅剩目标仍超限时，先尝试语义投影，再逐个删除目标；最后仍只超元信息时，进入显式 `budget_insufficient_metadata` 降级包，不静默发送正常包宣称完整。
- 最终 `LastPackChars` 在 stats 写回后重新计算；metadata 降级也有确定状态和 warning。
- `ProjectSource` 扩充了本体限定字段、流程/时点、条件/代价/目标、容量/公式/计分、局部槽位等语义键。仍超单条阈值时递归截断字符串并标记 `projection_complete=false`，不伪装完整。
- `ProjectionComplete`、`ContentHash`、`Projection` 和 relation 字段进入 evidence 条目，便于对账。

### 验证

- candidate 与 optional related 共存时先删 related；rank 2/3/10 的 e10/e2/e3 顺序按 rank 而不是 ID 字典序。
- 单 target 超大、多 target 超大、元信息本身超大均在最终序列化长度上验证。
- 同包重复 ApplyBudget 不重复累计 omission。
- 大条目投影保留嵌套 condition/content，并标记 incomplete。
- 小于/大于投影阈值的等价规则关键字段不丢失。

## 6. 分层指标与诊断工具

新增/更新：

- `tools/eval/analyze_evidence_layers.py`：分别报告检索候选、正式装包、Respond 引用、答案通过四层。历史字段只作传统计，不把 legacy Matched 与 program packed 当同义。
- `tools/eval/compare_versions.py`：新增逐层 observable/hit 字段、coverage/outcome 状态、读取/复用/预算统计；旧 `direct_expected_hit` 保留但在分析中明确标注语义不等价。
- `tools/eval/analyze_compare.py`：分表展示检索/装包/引用命中，输出旧口径警告。
- `tools/eval/diagnose_unavailable.py`：按 plan/retrieval、retrieval→pack、respond/证据充分性给出 earliest-failure hint，要求人工复核。
- `tools/eval/build_repair_dev_set.py`：可复现生成诊断开发集。
- `tools/eval/compare_evidence_packs.py`：对旧/新两代事实包做路径、内容 hash、角色、omission、字符数对账。
- `tools/diagnostics/EvidencePackDump`：固定计划只跑 pack builder，不调用回答模型；可复制到旧 worktree 对旧提交跑同一计划。
- `tools/diagnostics/RespondPackProbe`：加载冻结事实包，只运行生产 Respond 提示和 `submit_answer` schema 一次。

## 7. 冻结诊断集

文件：`tools/qa/program_evidence_repair_dev_questions.jsonl`（SHA-256 `0467cc6c…f21c3dce`）。

选择依据：

- 31 个去重问题，覆盖 9 款游戏。
- 19 个曾出现 program unavailable 的正例；
- 3 个曾进入 unavailable/ambiguous 路径的负例或歧义题；
- 7 个历史两版均通过的已知正确对照；
- 2 个 slot 回归（含 Civolution `final_scoring_area_hex.point_bonus`）。
- 来自已观察结果和此前 `new_holdout` 题，仅作为开发/诊断集，不声称是未参与调参的留出集；没有把 expected ID 注入在线 builder。

历史 unavailable 再分类（第二轮 program 运行，双评委共识）：

- 30 个 unavailable、17 个去重问题。
- `other_evidence_packed_target_missing`：14；
- `expected_target_packed_but_unavailable`：6；
- `no_formal_evidence_packed`：5；
- `unknown_no_expected_target`：5。
- earliest hint：plan/retrieval 23、retrieval→pack 1、respond/证据充分性 1、unknown 5。
- 这仍然只是分诊，不把“有 evidence”当成“模型该回答”的证明。

典型阶段：

- `gold-064`（Wingspan 栖息地有哪几种）：3/3 无正式证据，最早失败在计划/检索。
- `intent-09`（Agricola 职业卡开局怎么发）：3/3 目标未入包，最早失败在计划/检索。
- `gold-054`（Seasons 水晶怎么获得）：3/3 expected target 已在包内，但模型仍 unavailable/clarify，属于 Respond/证据充分性需人审。
- `gold-066`（Wingspan 先手标记给谁）：包内有目标，模型有时回答“规则未写明首发归属”；部分是事实缺口，部分是不充分回答。

## 8. 固定计划 → 事实包对照

冻结计划（来自历史 program 运行的理解结果）：

```json
[
  {"question_id":"q1","entity":"收获阶段的喂养","relation":"flow","intent":"收获阶段中喂养步骤的结算顺序和具体做法"},
  {"question_id":"q2","entity":"喂养费用","relation":"explain","intent":"每位家庭成员需要支付多少食物以及支付方式"},
  {"question_id":"q3","entity":"食物不足","relation":"boundary","intent":"无法支付足够食物时的处理办法"}
]
```

旧构建器取 `5d279c5` worktree，修复构建器取当前工作区；游戏 Agricola、规则内容未发生版本差异。

| 指标 | 旧构建器 | 修复构建器 |
|---|---:|---:|
| evidence records | 7 | 22 |
| distinct canonical paths | 3 | 22 |
| expected `harvest_feeding` | 无 | 有 |
| compact pack chars | 16,029 | 44,751 |
| q1/q2/q3 绑定数 | 1/4/2 | 8/8/8 |
| omission | 0（旧口径未记录） | 15 |
| q coverage | 旧包无字段 | 3 题均 partial_evidence |

旧包只落到 `game`、`family_member`、`food`；expected target 缺失。修复包新增 `harvest_feeding`，并保留 `begging_marker`、`place_family_member`、`ontology::pipeline` 等必要上下文；代价是包变大 2.79 倍，且三题都因条数预算产生 explicit omitted，不伪装完整。

产物：

- `gold-013-old-pack.json`
- `gold-013-new-pack.json`
- `gold-013-pack-diff.json`
- 对应 Respond 输出 `respond-old-pack.json` / `respond-new-pack.json`

## 9. 固定事实包 → Respond 单阶段

旧包先转换为当前 pack schema 的等价正文与绑定，不改变 evidence 内容；使用相同 program Respond 提示、`submit_answer` schema、`deepseek-v4-flash`、Thinking=low，各跑一次。

| 输入包 | 结果摘要 | prompt / completion tokens | 单次耗时 |
|---|---|---:|---:|
| 旧包，无 `harvest_feeding` | answered 喂养费用，但 q1 明确 unavailable：“没有读到顺序” | 6,870 / 761 | 4,525ms |
| 修复包，含 `harvest_feeding` | 三题 answered：第二阶段、每人两份、新生儿一份、缺一拿乞讨标记 | 15,385 / 379 | 2,755ms |

这是单例隔离观察，不构成质量结论；它证明“旧包缺目标、修复包补上目标”会改变 Respond 可用的依据，也说明修复后的主要剩余问题之一是大包带来的 prompt token 与输出稳定性。

## 10. 小规模端到端对照

- 输入：31 题诊断集，每问每版 3 次，共 186 次。
- 两版共享：模型 `deepseek-v4-flash`、Thinking=low、规则内容、索引、API 主机、并发和超时。
- 判定：两个版本盲评委（`deepseek-v4-flash` 与 `deepseek-chat`），只有两个评委都 pass 才算 consensus pass。
- 采集时 program 已包含 R1–R5 修复、全局 pack 引用放宽；尚未包含最后一个 trace 去重修复。唯一 1/93 `internal_error` 的原因是同 `question_id` 的 need_more 结果在 trace 聚合时 `ToDictionary` 抛异常；该问题已修复并有单测，随后 gold-066 三次定向 smoke 未再出现 internal_error。因此下面数字标记为“最终修复前 1 个 crash 的 93 次运行”。

### 主要结果

| 指标 | legacy | program 修复版 |
|---|---:|---:|
| 严格回答通过（双评共识） | 68.8% | 37.6% |
| 正例检索候选命中 | 71.4%* | 50.0% |
| 正例正式装包命中 | N/A（legacy 无正式包层） | 71.4% |
| 正例 Respond 引用命中 | N/A（legacy 无 cite 层） | 69.0% |
| 正例答案通过 | 69.0% | 38.1% |
| program outcome | 不适用 | answer 58、unavailable 30、clarify 4、internal_error 1 |
| 模型调用均值 / P50 | 本运行响应未记录 | 2.29 / 2 |
| P50 延迟 | 3,603ms | 5,110ms |
| P95 延迟 | 9,923ms | 9,402ms |
| 平均事实包字符 | N/A | 21,313 |
| 事实包字符 P95 | N/A | 42,476 |

\* legacy 检索命中来自历史兼容字段中的候选路径，不等同于 program 新的显式 `retrievalCandidatePaths` trace 口径；仅作方向性对照。

配对结果：

- 可配对问题 31。
- 问题级多数票通过：legacy 71.0% vs program 35.5%，差 -35.5pp。
- 每次运行通过率差均值 -31.2pp，问题级 Bootstrap 95% CI [-43.0, -19.4]pp。
- McNemar：legacy-only pass 11，program-only pass 0，p=0.0010。
- 配对延迟差：均值 +973ms，中位数 +1,208ms。
- 配对 query 数差均值 -1.00。
- 两位评委 run-level 完整一致率 80.1%；因此结果仍应视为小样本筛查，不作架构优越性结论。

### 分层观察

- 召回不足：正例 expected 规范路径在首轮检索候选中的命中仅 50.0%，比 legacy 历史候选路径约 71% 低约 21pp。计划拆分/实体表述是首要失败阶段。
- 装包恢复：修复后 identity 和 related 展开使 pack hit 达到 71.4%，高于召回命中；说明构建器能从部分候选/关联中救回条目，但不能替代检索召回。
- 引用：citation hit 69.0%，接近 pack hit；全局 catalog 引用放宽后协议修复率从 19.4% 降到 2.2%，protocol_error 归零；但 1 个 trace 去重异常仍造成 internal_error（已修）。
- 答案：即使 pack/citation 命中，answer pass 仍明显低于 legacy；unavailable/clarify 中有目标已装包的案例。不能把失败都归因于事实包身份。

## 11. 性能

program 修复版（93 次）：

- Understand 平均 1,340ms；平均 prompt 828 / completion 165 tokens。
- Retrieval 平均 588ms。
- Respond 平均 3,640ms；每次 Respond 平均 prompt 7,170 / completion 462 / reasoning 178 tokens。
- 平均 read attempts 3.09、successful reads 3.09、binding reuses 3.38、pack items 9.24、omitted 10.73。
- 模型调用分布：2 次 66、3 次 27。
- P50 延迟 5,110ms、P95 9,402ms。
- legacy 本运行只记录了端到端延迟；相同 log 里的 legacy 模型调用数在后续 smoke 时被覆盖，无法做同数据集精确调用数比较。历史 133×3 运行的 legacy 均值 3.16 仅作背景，不与本轮混比。

## 12. 门槛判断与后续

未满足下一轮全量对照门槛：

- 确定性测试已过，默认 legacy 基线已隔离。
- 开发集 program 严格正确率显著低于 legacy。
- 充分证据仍 unavailable 的问题有 6 个运行，但尚未被修复到可复核改善。
- 正例检索候选命中、pack、citation、answer 四层均显示独立损失；retrieval/planning 是数量上最大的第一失败阶段。
- 没有证据支持切换默认或进行 798 次全量重跑。

下一步若继续该方向，应先把优化拆开：

1. 只修 Understand/planning/retrieval 的规范路径召回，不碰 Respond；
2. 在冻结充分包上单独做 Respond 提示/输出协议变体，不同时改检索和预算；
3. 对修复包尝试仅压缩重复正文和元信息，保留必要语义；把“身份修复”和“包压缩收益”分开测量；
4. 只有开发集不再显著低于 legacy、P50 不恶化后，再考虑真正未参与调参的留出集和全量配对。

## 13. 产物与复现

代码/测试：

- `backend/BoardAI.Api/Services/RuleEvidencePackBuilder.cs`
- `backend/BoardAI.Api/Services/RulesReferenceService.cs`
- `backend/BoardAI.Api/Services/RulesConceptCatalog.cs`
- `backend/BoardAI.Api/Services/ProgramControlledChatOrchestrator.cs`
- `backend/BoardAI.Api/Services/ChatOrchestratorService.cs`
- `backend/BoardAI.Api/Models/ChatResponse.cs`
- `backend/BoardAI.Api.Tests/PromptIsolationTests.cs`
- `backend/BoardAI.Api.Tests/RuleEvidencePackBuilderTests.cs`
- `backend/BoardAI.Api.Tests/ProgramControlledOrchestrationTests.cs`

工具：

- `tools/eval/analyze_evidence_layers.py`
- `tools/eval/diagnose_unavailable.py`
- `tools/eval/build_repair_dev_set.py`
- `tools/eval/compare_evidence_packs.py`
- `tools/diagnostics/EvidencePackDump/`
- `tools/diagnostics/RespondPackProbe/`

本轮实验 artifact（gitignored）：

- `.tmp/program-evidence-repair/e2e-v2/`：runs、two judges、consensus、analysis-layered、evidence-layers、unavailable-diagnosis。
- `.tmp/program-evidence-repair/gold-013-*`：冻结计划、旧/新包、pack diff。
- `.tmp/program-evidence-repair/respond-*.json`：固定包 Respond 单次输出。
- `.tmp/program-evidence-repair/gold-066-smoke/`：最后一处 internal_error 修复后的定向前测。

复现命令：

```bash
# 后端测试
dotnet test backend/BoardAI.Api.Tests/BoardAI.Api.Tests.csproj --nologo

# 冻结诊断集
python3 tools/eval/build_repair_dev_set.py \
  --out tools/qa/program_evidence_repair_dev_questions.jsonl

# 旧/新事实包对照
dotnet run --project tools/diagnostics/EvidencePackDump -- \
  /mnt/d/workspace/board agricola .tmp/plan.json .tmp/new-pack.json 60000
python3 tools/eval/compare_evidence_packs.py \
  --old .tmp/old-pack.json --new .tmp/new-pack.json \
  --expected harvest_feeding --out .tmp/pack-diff.json

# 分层分析
python3 tools/eval/analyze_evidence_layers.py \
  --runs .tmp/program-evidence-repair/e2e-v2/runs.layered.jsonl \
  --judge .tmp/program-evidence-repair/e2e-v2/judge-consensus.jsonl \
  --out .tmp/program-evidence-repair/e2e-v2/evidence-layers.json
```

## 14. 已知限制

- 31 题诊断集来自已观察失败与已观察正确题，不是独立留出集。
- 小样本、每题重复 3 次，质量差值和延迟差值都有采样不确定性；本报告不做大样本显著性宣传。
- 旧构建器对照只覆盖一个 Agricola 固定计划；旧包已是旧格式，Respond 单例使用了当前 Respond 提示，不能与历史 old program 的完整端到端分数直接等同。
- v2 的 93 次 program 运行里有 1 次已定位、已修复但尚未重新全量采集的 trace 去重异常；该运行对总体结论影响有限，但最终数字仍应视为“修复前最后采集版本”。
- 没有重跑 Android 真机；本轮新增的 JSON trace 字段是向后兼容的 additive 字段，未改变 Android 解析路径。后端测试 220/220；未在本轮宣称 Android 验收完成。
