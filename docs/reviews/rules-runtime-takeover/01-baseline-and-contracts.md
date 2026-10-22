> 历史路径说明（2026-10-23）：本文保留当时本地 scratch 路径 `.tmp/...`，这些不是当前运行依赖。新实验请使用 `$BOARD_RUNTIME_ROOT/tmp/...`；2026-10-23 之前的原始 agent/eval 产物已压缩到 `$BOARD_RUNTIME_ROOT/archive/agent-workspace-evidence-20261023-004025.tar.zst`，需要时解压后按原相对路径读取。

# 规则问答程序接管 01：基线、结果契约与归因修正

日期：2026-10-17
分支：`feat/rules-runtime-takeover`
代码基线：`ffac498`；启动工作区 HEAD 为 `578d072`（其后只有文档修订）

## 1. 冻结基线

本阶段没有修改提示、模型或采样参数，只冻结当前默认 legacy 路径并加入可观测契约。

| 项目 | 冻结值 |
|---|---|
| 代码提交 | `ffac498890ccf9fdd10bb180b0dc37e9b88ed585` |
| 代码树 | `30f4302de7c7b52ca79beb0e34705ad47dc44537` |
| 默认编排 | `legacy` |
| 模型 | `deepseek-v4-flash` |
| Thinking | `low`（请求同时发送 `reasoning_effort=low`） |
| legacy system prompt SHA-256 | `859260e5ca6ed8f5154b63dde2fbdad61ad1e1e3bd97261108f7339f7f1343d5` |
| appsettings.json SHA-256 | `a950779e279329541fc993fb16c6f81d35539a2adbc4fa5ab11ce10b0b3001d4` |
| execute_plan tool schema 指纹 | `4682359fbac7`（对实际 `BuildTools("template")` 序列化取 SHA-256 前 12 位） |
| `content/` tree | `8e5269aef8c789f31696bdda4c3bd01e907c55ad` |
| 规则内容 285 文件清单 SHA-256 | `02c4173ea7373f4078453edda712c61480ec3c4509bb593c4553767810317f50` |
| 历史裁剪 | `MaxConversationMessages=40` |
| 工具预算 | `MaxToolRounds=6`、`MaxToolCallsPerRound=4`、`MaxTotalToolCalls=12` |
| 请求超时 | `MaxRequestSeconds=60` |
| 重复失败停止 | `MaxRepeatedFailures=3` |
| 生成参数 | 代码未显式覆盖 temperature/top_p/max_tokens；请求只固定 model、messages、tools、thinking/reasoning_effort |

基线文件指纹：

| 文件 | SHA-256 |
|---|---|
| `ChatOrchestratorService.cs` | `5288e25c1e96a37e95a1cdb63aa0886e12df6e3c81f381baa117c5b427e54d53` |
| `RulesPlanService.cs` | `7860b24bfbc3354550dfa3729de9b057fcee5ffddd4e488963ecb34589d4cac3` |
| `GameRulesService.Models.cs` | `34afcd106fde1b7860b1a4fe4e0736d598cd495965f0d230526492708734a466` |
| `RulesFlowService.cs` | `c46f16c45ad0d5d337d44403441607b7013d4dc54aae02bd36d2f114f8573608` |
| `RulesConceptCatalog.cs` | `7448c9f1922a35a4803b4654f469afd4f037c76159a5a1f7c66b6a5b08ba2b80` |

### 数据边界

- 31 题是选择过失败样本的开发诊断集，不是随机留出集；legacy 68.8% / program 37.6% 只用于开发诊断。
- 历史 133 题已经参与调参，也不再是留出集。
- 单题 expected target 命中不是答案正确的充分条件；评测保留人工可接受证据组合与答案评审。
- 本阶段没有运行新的真实模型对照，所有效果结论为未知。

## 2. 统一结果契约

`PlanItemResult` 现在携带并序列化以下字段：

| 字段 | 语义 |
|---|---|
| `QueryId` / `RulesVersion` | 请求内 query 身份与规则快照版本 |
| `Resolution` | `exact` / `candidates` / `not_found` / `unsupported` / `invalid`，只描述目标解析 |
| `ResolvedTargets` | 规范路径，身份键为 `(game, rules_version, canonical_path)` |
| `Payloads` | `definition` / `catalog_page` / `flow_slice` / `relation_facts` / `computed_fact` 有类型负载 |
| `Coverage` | `complete` / `partial` / `unknown`，只描述本次请求范围的覆盖 |
| `Omissions` | 明确原因、路径、字段与 continuation；不把截断伪装成完整 |

兼容扁平字段仍在同一 DTO 中用于旧评测脚本和 program 适配，但新消费者优先读取 payload：

| 生产分支 | 生成 payload / resolution | 消费证据 |
|---|---|---|
| `list` | `catalog_page`，complete；空目录也是 complete 空集合 | 不再依赖非空 `Matched` 才认为有数据 |
| exact `explain` / `condition` / `ordering` / `boundary` | `definition` + 相应 `relation_facts`；exact 时 `Candidates=null` | exact target 不再被候选确认流程误判 |
| `identify` / unresolved | resolution=`candidates`，coverage=unknown，omission=`ambiguous_identity` | 候选仍不能作为正式事实 |
| 实体未命中 | resolution=`not_found`，omission=`no_rule_coverage` | 与预算未读完、字段缺失分开 |
| 未知 relation | resolution=`unsupported`，omission=`unsupported_relation` | 不再 ok + 丢弃正文 |
| condition 无字段 | relation payload completeness=partial，omission=`field_missing` | 模型看到缺字段而不是空成功 |
| 未知 payload 类型 | 消费端转 `unsupported`、`HasData=false` | 不再静默流转 |

## 3. 行为测试

新增/相关测试：

- `QueryResultContractTests.List_ReturnsCatalogPagePayload_AndEvidenceCollectorTreatsItAsData`
- `QueryResultContractTests.ExactHit_ReturnsDefinitionPayload_AndNoCandidates`
- `QueryResultContractTests.SameNamedSlots_ResolveOnlyThroughTheirOwnerPaths`
- `QueryResultContractTests.GameAndOntologySameId_AreNotMixedWithoutNamespace`
- `QueryResultContractTests.ConditionWithoutFields_IsPartialWithExplicitOmission`
- `QueryResultContractTests.UnknownPayload_IsUnsupportedInsteadOfOkWithDroppedBody`
- `QueryResultContractTests.UnresolvedAndNotFound_AreDistinct`
- `QueryResultContractTests.EmptyCatalog_IsCompleteEmptySet_NotNotFoundOrUnknown`
- `RequestTimelineTests.LegacyRequest_RecordsModelAndToolSpans_ThatReconcileWithTotalElapsed`
- `RequestTimelineTests.ParallelSpans_AreReportedAsOverlap_NotSummedAsElapsed`
- `tools/eval/test_diagnose_unavailable.py`
- `tools/eval/test_compare_versions.py`

测试结果：后端 `230/230`；`python3 -m unittest discover -s tools/eval -p 'test_*.py'` `8/8`。首次全量运行出现过一次既有 snapshot GC 测试偶发失败，单独重跑全套通过；未修改该测试。

## 4. 计时时间线

legacy 现在返回 `ChatOrchestrationTrace`，包含：

- `model` span：每次带工具决策/无工具终答的开始偏移、时长、token（provider 提供时）。
- `tool` span：每次 `execute_plan` 的总时长和结果字符数。
- `TimelineAccounting`：span 区间并集 `CoveredMs`、重叠量 `OverlapMs`、未覆盖 `UnaccountedMs` 和 `IsReconciled`。
- 并行 span 记录同一 `ConcurrencyGroup`，重叠量单独报告，不把 span 时长相加当总耗时。
- 未暴露 reasoning token 时字段为 null，不用 reasoning 文本推断内部耗时。

当前 tool span 只能对账到“工具总时长”，搜索/向量化/正式读取/装配的细粒度拆分留到 04 的工具交付器落地后继续补齐。

## 5. 旧数据分析（不重跑聊天）

使用既有 `.tmp/program-evidence-repair/e2e-v2/runs.jsonl` 与 `judge-consensus.jsonl` 重新分析：

| 指标 | 修正前诊断 | 修正后诊断 |
|---|---|---|
| 总 unavailable runs | 30 | 30 |
| 归因 plan/retrieval | 23 | 18 |
| 归因 retrieval→pack | 1 | 1（navigation/formal read） |
| 归因 respond/sufficiency | 1 | 6 |
| unknown | 5 | 5 |
| expected target 已交付仍 unavailable | 6（原报告已有，但被部分候选未命中归因覆盖） | 6 |

另有 `pack_hit_rate=0.9231`（65 个可观测 run），说明“候选未命中”不能覆盖已正式装包命中；`answer_pass_rate=0.381`（84 个正问题 run）仍需按“证据已交付但答案未通过”继续人工复核，不能自动归因于检索。

## 6. 程序实际接管与模型少做的步骤

- 程序接管：查询结果归一化为 resolution/coverage/payload；目录成功不再被证据消费者当空；规范路径与版本进入结果；诊断按正式交付层优先归因；时间线由程序记录。
- 模型少做：本阶段没有修改实际发给模型的提示或工具 schema，因此**没有宣称模型少调用一次或减少机械查询**。可观测性修复只是为后续阶段提供可信基线。
- 不确定性：真实模型回答质量是否变化未知；tool span 内部搜索/正式读取尚未细分；扁平兼容字段仍可能让旧脚本继续使用旧归因，但改过的 eval 脚本已用规范路径精确匹配。

## 7. 未通过/未知项

- 本阶段不宣称回答准确率提升或延迟下降。
- 没有跑 31 题新的同轮对照。
- 预算裁剪的端到端形态要到 04 交付器实现后再做真实 payload 测试；本阶段先固定了 omission 契约和消费语义。
- 旧历史记录缺少规范身份时一律 unknown，不补造精确命中。
