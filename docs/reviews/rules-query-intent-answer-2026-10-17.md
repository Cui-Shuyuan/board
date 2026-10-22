> 历史路径说明（2026-10-23）：本文保留当时本地 scratch 路径 `.tmp/...`，这些不是当前运行依赖。新实验请使用 `$BOARD_RUNTIME_ROOT/tmp/...`；2026-10-23 之前的原始 agent/eval 产物已压缩到 `D:\board\archive\agent-workspace-evidence-20261023-004025.tar.zst`，需要时解压后按原相对路径读取。

# 查询意图保留与回答收敛实施报告

日期：2026-10-17  
模型/配置：`deepseek-v4-flash`，`LLM:Thinking=low`，使用仓库当前 `appsettings.json`；真实回归通过本机运行中的 Board API 执行。  
范围：仅规则问答计划、检索候选、证据确认与回答约束；未改规则事实、动画、音频、cue 或动画 QA。

## 1. 结论

- `execute_plan` query 已支持 `intent`，并接通 tool schema、SystemPrompt、计划解析、检索、结果字段、日志与证据。
- 原案例中 `deal_nobles` 从基线候选第 5 名升到意图检索第 1 名；选中 `deal_nobles` 后返回正式 `ok / exact_id` 事实。
- 未确认候选不再能无记录地直接变成正式回答依据：编排层最多做一次定向候选确认纠偏；确认关联记录 `resolvedByQueryIndex`、`confirmedCandidateId`、`confirmedByRelation`。
- 回答出口增加窄口径的组件数量/玩家人数约束守卫：客人说“三块贵族”时，不再反问人数或反推“这是两人局”，并尽量在第一句承接“三块”。
- 后端 xUnit 173/173；意图回归 13 行 / 14 子查询全部通过，错误确认 0；既有检索 gold 与 7 条回归未退化。
- 真实 LLM 最终回归 14/14 通过本轮验收口径；原句重复 5/5 通过。14 次中 13 次 `tier1`，1 次保持 `partial`（有 1 个多余 pending query），没有把 pending 硬升级。

## 2. 关键实现

### 2.1 Query schema 与降级

关键文件：

- `backend/BoardAI.Api/Services/ChatOrchestratorService.cs`
- `backend/BoardAI.Api/Services/RulesPlanService.cs`
- `backend/BoardAI.Api/Services/GameRulesService.Models.cs`
- `backend/BoardAI.Api/Models/ChatResponse.cs`
- `backend/BoardAI.Api/appsettings.json`

query 字段：

- `entity`：查谁/什么，保留概念 id、准确中文名或对象描述。
- `intent`：针对该实体要知道的动作、场景和疑点，来自用户问题及有效上下文。
- `relation`：需要哪类规则信息。

降级规则：

- 单一 query 缺失 `intent` 时，结果和日志可回填客人原问句；
- 检索仍保守走既有 entity 路径，不把回填句当成新的场景检索依据；
- 多 query 缺失 `intent` 时不把整段问题套给每一项。

计划结果新增 `queryIndex`、`intent`、`retrievalQueries`；候选新增 `matchReason`。日志记录原问题、逐 query relation/entity/intent、实际检索文本、候选排序/分数/来源、matched id、确认关联、tier、工具轮次、execute_plan 耗时和总耗时。

### 2.2 意图召回与候选排序

- entity 仍负责名称/身份定位。
- 显式完整 intent 走全文语义召回；合并时使用 `0.8 × intent + 0.2 × entity` 的同身份合并分数，避免泛词和多通道累加翻盘。
- 候选按稳定身份（`Path`，否则 `Id`）去重。
- 对象名精确命中但 intent 含额外场景/动作时，仍返回相关动作/流程候选；不会把对象定义当成完整答案。
- 准确 ID 直接保留身份；action/trigger/flow 的精确名称命中也不会被宽泛 intent 再次打散。
- 自动确认阈值没有放宽。

原案例前后对比（同一真实规则索引）：

| 查询 | 候选顺序（前五） | 目标位置 |
|---|---|---:|
| 基线：`entity=贵族`，无 intent | `attract_noble` 1.0796, `noble` 1.0485, `noble_market` 1.0362, `noble_satisfied` 1.0243, `deal_nobles` 0.9599 | 5 |
| 新：`entity=贵族板块`，`intent=设置时从游戏盒中取出三块贵族板块，应当如何选择` | `deal_nobles` 0.7026, `setup` 0.6119, `prepare_level_3_deck` 0.6010, `noble` 0.5936, `game_box` 0.5688 | 1 |

分数来自不同查询文本，只比较排序，不把混合分当概率。用准确 id 再查得到：

```text
status=ok, source=exact_id, matched=[deal_nobles]
description=从游戏盒中随机盲抽 player_count + 1 枚贵族板块，放入贵族市场。
flowContext=[游戏, 设置]
```

### 2.3 候选确认与证据关联

- unresolved 候选只用于选择目标，不是已确认事实。
- 后续 exact id / 准确中文名必须唯一命中，且对象必须出现在原候选中，relation 必须相同或 `identify → explain`。
- 同一候选出现在多个 pending query 时，不能仅凭 id 清空全部；先尝试 intent 完全一致，再用字符二元组相似度关联唯一的 intent（阈值 0.70 且领先第二名至少 0.15）。
- 确认后写 `resolvedByQueryIndex`、`confirmedCandidateId`、`confirmedByRelation`；原 status 保留为历史。
- 模型在 unresolved 候选存在时直接结束回答，编排层最多做一次定向纠偏；纠偏后若仍 pending，允许用户问题真正需要的已确认部分作答，但不把 pending 标记为已验证。

### 2.4 回答收敛

SystemPrompt 与最终回答约束已更新：

- 先回答用户问的方法/判断，不从概念定义重讲；
- 未确认候选描述不能当正式规则；
- 客人已给出的数量、对象和当前操作直接承接，不重复追问；
- 组件数量不是玩家人数证据，不得反推“这是几人局”；
- 不主动列出未问到的其他人数配置、另一种获得方式或后续计分。

另加两项程序化出口约束：

1. **未确认候选纠偏**：有 unresolved 候选且仍有预算时，最多一次要求模型确认候选。
2. **数量回答守卫**：仅当原问题含明确数字组件数量（如 `3个贵族`）时，检查回答是否反推玩家人数或遗漏该数量；命中则最多重答一次。它不是完整事实裁判，也不把 `tier1` 升级成回答正确性证明。

## 3. 验证结果

### 3.1 后端与检索

执行命令：

```bash
/mnt/d/dotnet/dotnet.exe test backend/BoardAI.Api.Tests/BoardAI.Api.Tests.csproj --nologo
python3 tools/indexing/eval_retrieval.py --gold tools/qa/retrieval_gold.jsonl --api http://172.17.208.1:5000
python3 tools/indexing/eval_retrieval.py --gold tools/qa/retrieval_regressions.jsonl --api http://172.17.208.1:5000
python3 tools/indexing/eval_query_intent.py --api http://172.17.208.1:5000 --out .tmp/query-intent-after
python3 tools/indexing/eval_natural_retrieval.py --api http://172.17.208.1:5000 --out .tmp/live-natural-after
```

结果：

- 后端 xUnit：173/173 通过。
- 既有 gold 85 条：直接命中 77、候选前三 8、错误确认 0、no_match 0；与仓库既有基线一致。
- 既有 7 条回归：目标全部前三，错误确认 0。
- 新增 `tools/qa/query_intent_regressions.jsonl`：13 行、14 个子查询，全部通过；2 个直接 ok，12 个在允许候选位次内，错误确认 0。
- 自然问句 48 条正例：hit1 29/48（0.6042）、hit3 41/48（0.8542）、MRR 0.7311；5 条负例保持 unresolved。该入口未显式使用 intent，读数与既有 API 基线一致。
- 未修改规则 JSON，因此未触发规则重建；检索索引沿用当前运行库。

新增回归覆盖：本次原句、口语变体、正反面选择步骤、同对象获得/上限、纯对象定义、三人局数量、多子问题隔离、准确 ID 确认、Agricola 两种职业卡意图、Wingspan 设置/打出/回合末计分。

### 3.2 真实 LLM 回归

干净重启 API 后固定连续执行 14 次聊天，未失败后只保留成功记录：

| 组 | 次数 | 通过 | 备注 |
|---|---:|---:|---|
| 原句重复 | 5 | 5 | 5 次均直接承接三枚/随机盲抽，不反问人数，不反推人数局 |
| 口语/步骤/数量/多问题/纯对象 | 5 | 5 | 数量题答四块；多问题两段回答；纯对象保留定义 |
| Agricola 两款意图 | 2 | 2 | 发放职业卡、打出职业卡分别确认到目标 |
| Wingspan 设置/打出 | 2 | 2 | 设置数量确认 `player_setup`；打出回答基于已确认 `play_bird` |
| 合计 | 14 | 14 | HTTP 错误 0 |

原句代表性最终回复：

> 不用挑，贵族不是选的，是从盒子里随机盲抽三枚，抽出来放到桌上摆成一排，当本轮可来访的贵族。

另一条原句运行：

> 不用自己挑，直接从盒里随机盲抽，数量是玩家人数加一，抽到哪几枚就摆在桌上哪几枚；你说三枚，那就随机抽三枚摆出来。

基线实际回复（修改前）：

> 不用挑，贵族是随机抽的。抽的张数是玩家人数加一，所以两个人玩就随机抽三张，三个人抽四张，四个人把五张全摆出来。

最终 14 次中 13 次 `tier1`、1 次 `partial`。`partial` 是 Wingspan 打出问题的一次多余 pending query；回答使用了已确认的 `play_bird` 正式事实，证据没有把它硬升级。以上只说明这批样本的表现，不代表稳定成功率或答案真实性保证。

## 4. 性能与代价

14 次真实聊天、33 次 execute_plan 工具调用：

- 端到端耗时：中位数 4.49 秒，最快 2.26 秒，最慢 9.52 秒；原句 5 次中位数 6.84 秒。
- execute_plan 工具耗时：中位数 13 ms，P90 453 ms，最慢 2564 ms（首次请求冷启动，包含向量检索/模型预热）；热路径多数为 0–255 ms。
- 工具轮次：中位数 3；工具调用数中位数 2。
- 本轮 14 次中触发协议纠偏 4 次、数量回答重答 3 次。必要确认增加了少量 LLM 往返；本样本不足以宣传尾延迟。

## 5. 已知残留

- 模型仍可能为一个问题生成多个相近 query（本次 14 次中 queryCount 为 2–6）。确认关联已能处理部分重复，但更多计划收敛仍应在 prompt/计划生成侧继续优化。
- 1 次 Wingspan 打出问句保留了一个多余 pending query，最终证据为 `partial`。当前服务没有模型可调用的“显式终止多余 query”字段；服务端以“纠偏后仍 pending 但不用于作答”的方式保守处理，没有静默确认。
- 数量回答守卫是窄口径程序规则，不是通用回答事实裁判；仅覆盖明确的数字组件数量与“X人局/玩家人数”反推模式，仍需要更多问法和语言样本校准。
- `tier1` 仍只代表查询证据覆盖，不代表最终回答每句话为真；本轮人工检查了样本语义，但没有做全量答案级事实审计。
- 真实 LLM 回归是小样本，未覆盖 9 款游戏全量；非 Splendor 只实际跑了 Agricola / Wingspan 各两问，其余游戏由确定性检索回归覆盖。

## 6. 工作区说明

- 未修改规则 JSON、动画、音频、cue、动画 QA。
- 保留了仓库原有未提交内容：`docs/plans/`（计划文件）未纳入本次实现提交；`.tmp/` 和 QA/模型留档为忽略生成物。
- 未清理、覆盖或 stage 无关工作区修改。
