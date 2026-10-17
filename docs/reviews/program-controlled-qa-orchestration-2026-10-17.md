# 程序控制问答编排实施与对照报告

日期：2026-10-17  
基线：`a992cfe`  
程序控制实现提交：`691d359 feat(chat): add bounded program-controlled QA orchestration`  
被测配置：`deepseek-v4-flash`、`Thinking=low`、同一规则内容与同一 Qdrant 索引  
规则内容哈希：`e2441e933ae25e42e25eb3d7574f2309141c5cdb4c2e068b4358ccca96951332`

## 1. 最终结论

程序控制状态机、正式事实包、四类出口、有界补充/修复、引用校验和阶段观测均已实现并通过确定性测试；新路径确实把平均模型调用数从 **3.160 降到 2.208**，但**没有通过质量与延迟验收**：

- 两个 blind judge 的保守共识严格回答通过率：旧版 **68.7%**，新版 **56.9%**，差值 **-11.8pp**，问题级 Bootstrap 95% CI **[-19.1, -4.8]pp**。
- 规则直接命中率：旧版 **77.5%**，新版 **71.5%**。
- 正例“直接命中或候选前三”：旧版 **90.6%**，新版 **79.2%**。
- P50 延迟：旧版 **3684ms**，新版 **4653ms**，增加 **969ms**；P95 也增加约 **644ms**。
- 两个独立 judge 分别给出旧版 69.2% → 新版 58.2%（-11.0pp）和 90.5% → 72.4%（-18.1pp），方向一致。

按计划默认验收门槛，**新版不满足“严格回答成功率不低于基线、95% CI 下界高于 -5pp、P50 有实际下降”**。因此：

> **生产默认继续保持 `LLM:OrchestrationMode=legacy`。program 路径保留为显式实验能力，不在本轮切换默认。**

这不表示新架构没有价值：它在未知/歧义题、局部 slot 读取、正式证据引用和调用预算控制上有明确改善；失败主要在一次性问题计划质量、事实包选择，以及回答阶段过度使用 `unavailable`。

## 2. 实际实现

### 2.1 主流程

默认行为未切换，但实现了完整 program 路径：

```text
绑定同一规则快照/取消/时间预算
  → Understand：submit_understanding，只输出 request_kind + 子问题计划
  → Retrieve：程序执行查询并主动读取正式规则，生成 RuleEvidencePack
  → Respond：submit_answer，输出 answer / clarify / unavailable / need_more
  → 结构/引用校验 → 输出
  → need_more：最多一次受限补充检索，再 Respond
  → 修复：全请求共享一次协议修复预算
```

硬预算：

- 总模型调用上限默认 4 次，包含 Understand、Respond、补充和修复。
- `need_more` 最多一次。
- 协议修复最多一次，且与补充共用总调用数和请求超时。
- 补充查询必须说明缺失信息、补充 entity/relation/intent；重复查询会被拒绝。
- 不是无界工具循环；模型不能控制任意轮次。

关键文件：

- `backend/BoardAI.Api/Services/ProgramControlledChatOrchestrator.cs`
- `backend/BoardAI.Api/Services/RuleEvidencePackBuilder.cs`
- `backend/BoardAI.Api/Services/ChatOrchestrationContracts.cs`
- `backend/BoardAI.Api/Services/ChatOrchestratorService.cs`
- `backend/BoardAI.Api/Models/ChatResponse.cs`
- `backend/BoardAI.Api/Models/LLMOptions.cs`
- `backend/BoardAI.Api/appsettings.json`
- Android：`clients/android/app/src/main/java/com/boardai/tutorial/uaal/qa/QaEvidence.kt`

### 2.2 四种用户可见出口

程序实现并测试了：

| kind | 行为 |
|---|---|
| `answer` | 必须有本事实包内的 evidence_id；逐子问题给 `question_results`；外部引用或空引用会被拒绝/修复 |
| `clarify` | 正常结束，不再因候选 pending 被强制要求选对象；可以在无引用时返回必要澄清 |
| `unavailable` | 正常结束；用于未找到、读取失败或预算不足；不把检索未命中写成实体绝对不存在 |
| `need_more` | 内部状态，不直接播报；最多触发一次受限补充，之后必须回答或明确无依据 |

`not_needed` 只允许用于确有理由的多余子问题，并要求 reason；不删除用户真正提出的子问题。

### 2.3 正式事实包

`RuleEvidencePackBuilder` 做的是确定性读取与投影，不让第二个 LLM 总结规则：

- 每个子问题执行程序查询。
- 对 unresolved 候选按稳定路径主动读取前若干正式条目。
- evidence 条目包含 `evidence_id`、`canonical_path`、`source_ref`、名称、角色（target/candidate/related）和读取状态。
- 按 relation 保留 `condition/cost/target/boundary/siblings/loop/flow_context/facts`。
- related 证据与 target 分开，避免把别的候选条件误套到目标上。
- 候选摘要没有 evidence_id，不能作为引用依据。
- 局部 slot 使用 `owner.slot` 规范路径；slot JSON 即使没有顶层 id 也可成为正式 evidence。
- 有候选数、每问证据数、总证据数、单条 JSON 字符数和整包字符数预算；超限优先省略低优先 related/candidate 并记录 omitted。
- 事实包总大小和 omitted 数进入 `orchestration` 轨迹。

非 Splendor 样例（Civolution `final_scoring_area_hex.point_bonus`）：

```json
{
  "evidence_id": "e1",
  "canonical_path": "final_scoring_area_hex.point_bonus",
  "source_ref": "civolution::final_scoring_area_hex.point_bonus",
  "role": "target",
  "condition": "阶段指示物位于本格",
  "target": "所有已安装且包含分数奖励的研究牌",
  "content": "加总这些研究牌标示的分数奖励并推进 score_track"
}
```

### 2.4 观测与客户端

`ChatResponse` 保留旧 evidence/tier，同时新增：

- `orchestration.outcome`
- 模型调用总数、补充次数、修复次数
- 每次模型调用的阶段、reason、完整耗时、prompt/completion/reasoning tokens
- understand/retrieve/respond/validation 耗时
- evidence 读取数、候选读取失败数、omitted 数、事实包字符数
- 逐子问题 retrieval 状态、evidence_ids 和 outcome status

Android evidence 解析已增加 `orchestrationOutcome`、`evidenceReadCount`、`citedEvidenceCount`、`modelCallCount` 等可选字段；`clarify` 不再显示“缺证据”提示。旧客户端忽略未知字段仍可工作。

## 3. 测试与静态验证

执行结果：

- 后端 xUnit：**198/198 通过**。
- Android JVM：**13 个测试文件 / 104 个测试全绿**。
- `tools/eval` 单元测试：**2/2 通过**。
- `python tools/content/validate_rules.py --errors-only`：**0 errors / 72 warnings**。
- 默认配置 `legacy` 冒烟：真实 `/api/chat` 成功返回，响应不含 program `orchestration`，证据 tier1。
- program 路径单元测试覆盖：两轮正常回答、conversation 单轮、clarify、need_more 一次、第二次 need_more 停止、非法 evidence 修复、候选摘要不能当证据、局部 slot、事实包预算、未知对象、取消、总调用预算和 Controller 返回 orchestration。

### 测试中的三个重点回归

1. **局部 slot**：测试 `board_a.point_bonus` / `board_b.point_bonus` 同名槽位，正式读取后 evidence 路径保持 owner 隔离；缺顶层 id 的 slot 不再被误判无数据。
2. **澄清不被拦截**：候选存在时 respond 返回 `clarify` 被接受，不会触发旧的候选确认循环。
3. **多余 query**：多子问题中一个 `not_needed` 有明确 reason 时，回答不被未使用候选阻塞；真正未回答的用户子问题仍标记 unavailable/clarify。

## 4. 对照实验设计

- 基线：`a992cfe`，旧自由工具循环。
- 新版：`691d359`，`LLM:OrchestrationMode=program`。
- 固定回归 117 题 + 新增未参与实现调参的 16 题留出集 = **133 题**。
- 每题每版重复 **3 次**，共 **798 次聊天**。
- 配对方式：旧/新请求成对启动，随机化提交顺序；`pair-workers=2`，即同时两对在飞，两版承受相同并发负载。
- 同一 `content` 哈希、同一 Qdrant 索引、同一模型与思考档位。
- 0 HTTP 错误，无回答质量重试，失败样本全部保留。
- 盲评：`deepseek-v4-flash` 与 `deepseek-chat` 两个 judge，隐藏版本和 evidence；主指标为两个 judge 都 pass。
- judge run-level 一致率：**80.45%**。

## 5. 实验结果

### 5.1 主质量指标

| 指标 | 旧版 | 新版 | 差值 |
|---|---:|---:|---:|
| 严格回答通过率（两 judge 共识） | 68.7% | 56.9% | **-11.8pp** |
| 问题级多数票通过率 | 73.7% | 63.2% | -10.5pp |
| 正例直接命中目标规则 | 77.5% | 71.5% | -6.0pp |
| 正例直接命中或候选前三 | 90.6% | 79.2% | -11.4pp |
| tier1 率 | 87.2% | 73.2% | -14.0pp |
| pending=0 率 | 87.2% | 92.2% | +5.0pp |
| 无依据/矛盾率 | 27.6% | 23.6% | -4.0pp |
| 不必要追问率 | 0.0% | 11.0% | +11.0pp |
| 无关扩展率 | 10.0% | 12.3% | +2.3pp |
| 人数反推率 | 0.0% | 0.8% | +0.8pp |

主配对检验：

- 133 个可配对问题。
- 每次运行通过率差均值：**-11.8pp**，问题级 95% CI **[-19.1, -4.8]pp**。
- 旧版单独多数通过 30 问，新版单独多数通过 16 问；McNemar p=**0.0541**。
- 两个 judge 分别的差值：-11.0pp（95% CI [-18.1, -4.3]）和 -18.1pp（95% CI [-24.1, -12.0]）；方向一致，不是单一 judge 的偏差。

### 5.2 留出集

| 切片 | 旧版通过率 | 新版通过率 | 差值 |
|---|---:|---:|---:|
| 固定回收集 `holdout` | 70.4% | 58.1% | -12.2pp |
| 新增留出集 `new_holdout` | 56.2% | 47.9% | -8.3pp |

新版在新增留出集上同样下降，说明这次结果不是只由旧题集过拟合造成。

### 5.3 哪些类别改善、哪些退化

改善：

- `negative_or_ambiguous`：61.1% → 80.6%，+19.4pp。
- 新留出集 `ambiguous`：0% → 83.3%。
- 新留出集 negative：16.7% → 66.7%。
- 新留出集 slot：66.7% → 100%。

退化较多：

- `retrieval_explain`：69.6% → 42.0%，-27.5pp。
- `intent_and_multi`：56.4% → 46.2%，-10.3pp。
- 新留出集 setup：100% → 33.3%。
- 新留出集 multi：66.7% → 22.2%。

逐游戏：

| 游戏 | 旧版 | 新版 | 差值 |
|---|---:|---:|---:|
| agricola | 70.0% | 73.3% | +3.3pp |
| ark-nova | 85.2% | 51.8% | -33.3pp |
| brass-birmingham | 61.1% | 66.7% | +5.6pp |
| castles-of-burgundy | 72.7% | 69.7% | -3.0pp |
| civolution | 52.4% | 31.8% | -20.6pp |
| puerto-rico | 72.7% | 48.5% | -24.2pp |
| seasons | 81.5% | 59.3% | -22.2pp |
| splendor | 70.8% | 69.4% | -1.4pp |
| wingspan | 68.8% | 41.7% | -27.1pp |

### 5.4 调用次数与延迟

服务日志按 request id 统计：

| 指标 | 旧版 | 新版 | 变化 |
|---|---:|---:|---:|
| 模型调用总数 | 1261 | 881 | -30.1% |
| 每问平均模型调用 | 3.160 | 2.208 | -30.1% |
| 模型调用中位数 | 3 | 2 | -1 |
| P90 模型调用 | 5 | 3 | -2 |
| 最大模型调用 | 7 | 4 | -3 |
| 旧 execute_plan 工具调用均值 | 1.932 | 0（改为程序内部检索） | — |
| 端到端 P50 | 3684ms | 4653ms | **+969ms** |
| 端到端 P95 | 9637ms | 10280ms | **+644ms** |

新版模型调用分布：2 次 **317/399（79.4%）**，3 次 81/399，4 次 1/399。补充检索 74 次（18.5%），协议修复 9 次（2.3%）。说明“普通问题两次调用”的预算目标基本达到，但质量没有守住。

新版阶段平均耗时：understand **1409ms**，retrieval **632ms**，respond **3370ms**。事实包平均 **9198 字符**，P95 19440 字符。模型调用减少没有转化为端到端 P50 下降，主要因为新版回答阶段输入更大、并发负载下 respond 更长。

## 6. 轨迹对照

| 案例 | 旧版 | 新版 | 结果 |
|---|---|---|---|
| 正常规则：同色拿两颗 | 3 次模型、2 次工具 | 3 次模型、1 次补充 | 两版均通过；新版正常两轮目标在该问未命中，因为触发了一次补充 |
| 局部 slot：Civolution point bonus 槽位 | 4 次模型、2 次工具，tier2 | 2 次模型，tier1，正式 slot 证据被引用 | 新版证据读取改善 |
| 合法澄清：“那个标记怎么拿” | 4 次模型、2 次工具 | 2 次模型，clarify | 两版都给出澄清；新版无候选确认循环 |
| 多余 query：原贵族问句 | 4 次模型、2 次工具 | 3 次模型，其中 1 次修复 | 新版仍编造了人数对应，且事实包没有被简洁使用 |
| 未知对象：激光炮牌 | 4 次模型、2 次工具 | 2 次模型，unavailable | 新版通过 3/3，旧版仅 1/3 |
| 应答失败：草原行动方块 | 3 次模型、2 次工具，正确回答 | 3 次模型、1 次补充后仍 unavailable | 证据已在包内，但模型错误地放弃回答 |

失败机制中最明显的一类是：

- 新版 399 次中有 **44 次 `unavailable` 且事实包已有证据**；
- 另有 12 次 `unavailable` 且没有证据；
- 回答文本中位长度从旧版 93 字符升到新版 144 字符，模型更容易加入无关/未支持细节；
- 单次 understand 计划无法完全替代旧循环的候选消歧，导致直接命中率下降 6pp、候选前三可恢复率下降 11.4pp。

## 7. 验收门槛核对

| 门槛 | 结果 |
|---|---|
| 确定性测试都通过，slot/澄清/多余 query 回归闭环 | 通过 |
| 普通问题实测两次调用，复杂/修复有界 | 基本通过：79.4% 运行恰好两次，所有运行 ≤4 次 |
| 严格成功率不低于基线，95% CI 下界高于 -5pp | **失败**：-11.8pp，CI [-19.1, -4.8] |
| 无依据断言、必要澄清不实质退步 | 部分改善/部分退步：无依据率改善 4pp，但 unnecessary clarification 11pp、无关扩展 +2.3pp |
| 平均模型调用与端到端 P50 实际下降 | 调用下降；P50 **上升 969ms**，失败 |
| 无游戏特判、无过拟合答案模板 | 通过 |
| 客户端兼容/Android 解析 | 通过：104/104 JVM 测试 |

## 8. 为什么保留 program 路径

虽然本轮不切换默认，program 路径仍保留，因为：

- 它提供了比旧自由循环更清晰、可审计的执行状态和预算上限。
- 它在未知/歧义题、局部 slot 和正式证据引用方面表现更好。
- 它把“读取了证据”和“模型选择了正确证据”分开，并暴露了具体失败阶段。
- 它已经具备后续优化的最小结构，不需要重写整个规则系统。

## 9. 已知风险和后续工作

1. **事实包中的证据存在 ≠ 回答模型会使用它**。program 的直接命中率、tier1 和 recoverable top3 都低于旧循环，说明一次性 Understand 计划没有替代候选迭代消歧。
2. **模型过度选择 unavailable**。44 个有证据运行仍被模型判为 insufficient；需要将“有正式 evidence 但不确定”与“没有 evidence”在回答提示和程序校验中更明确地区分，并考虑一次有界重答。
3. **回答过长和无关扩展**。新路径需要更严格的“只回答当前问题，不重述全部规则”约束，但不得用正则或数量重写等过拟合逻辑。
4. **延迟分解**：新版模型调用少了 30%，但事实包更大、respond 输入更长；P50 没有下降。后续应先压缩事实包到问题所需字段，再评价延迟。
5. **judge 仍有波动**：两个 judge 的绝对通过率差很大（69% vs 90% 旧版），共识降低了单 judge 偏差，但仍不是人工真值；争议样本需要人工复核。
6. **未完成项**：没有为新架构进行第三轮质量调优和正式切换；没有做低/关闭 thinking 的独立实验；没有 Android 真机端到端验收。

## 10. 复现

```bash
# 后端
dotnet test backend/BoardAI.Api.Tests/BoardAI.Api.Tests.csproj --nologo

# Android（Windows）
cd clients/android
gradlew.bat testDebugUnitTest --no-daemon --console=plain

# 程序控制对照（详见 tools/eval/README.md）
python3 tools/eval/build_question_ground_truth.py --questions tools/qa/comprehensive_eval_questions.jsonl --api http://127.0.0.1:5002 --out .tmp/program-eval/regression_ground_truth.json
python3 tools/eval/merge_question_sets.py --main-questions tools/qa/comprehensive_eval_questions.jsonl --main-ground-truth .tmp/program-eval/regression_ground_truth.json --holdout-questions tools/qa/program_orchestration_holdout.jsonl --out-questions .tmp/program-eval/questions_all.jsonl --out-ground-truth .tmp/program-eval/question_ground_truth.json
python3 tools/eval/compare_versions.py --questions .tmp/program-eval/questions_all.jsonl --old-api http://127.0.0.1:5001 --new-api http://127.0.0.1:5002 --out .tmp/program-eval/main --repeats 3 --pair-workers 2
```

原始运行和评分留档在本机 `.tmp/program-eval/main/`：

- `runs.jsonl`
- `judge.jsonl`
- `judge-deepseek-chat.jsonl`
- `judge-consensus.jsonl`
- `rule_details.json`
- `analysis-consensus/summary.md`
- `model-calls.json`
- `baseline-api.log` / `new-api.log`

这些是 QA 生成物，按约定不进入 Git。生产默认仍为 `legacy`；program 只通过显式配置启用。
