# 协议小对照与失败案例人工复核

日期：2026-10-17
分支：`feat/rules-runtime-takeover`
候选代码：`47231f8`
开发集：`tools/qa/program_evidence_repair_dev_questions.jsonl`（31 题）
本轮新增在线 judge 调用：**0**

## 1. 背景口径修正

采纳以下判断：

- 上一轮 31 题配对 CI `[-3.2pp, +20.4pp]` 说明证据不足以支持默认发布，不表示禁止继续验证。
- 31 题是经过筛选的开发集，不围绕 CI 下界差的 0.23pp 反复调参。
- 本轮先定位可解释的协议成本并做小对照，而不是追求把开发集 CI 下界“刷过线”。

## 2. 三个协议对照

保持规则内容、Qdrant 索引、工具 schema、证据交付和回答策略不变，仅改变：

| 变体 | Thinking | ToolChoice | 终态提示 |
|---|---|---|---|
| A | low | auto | 统一为所有回复必须走 submit_answer |
| B | disabled | auto | 同 A |
| C2 | disabled | required | 同 A |

每组为 31 题 × 3 次 = 93 次候选请求。A/B/C2 的 HTTP 错误均为 0，重试均为 0。C 的最终汇总使用 `C2`：首次 C 在 `47231f8` 修复“非法 submit_answer 的后续修复调用计数”之前启动，出现 1 次 `protocol_validation_error` 而修复调用数仍记为 0；修复提交后重启同一配置复跑为 C2。C2 在 `thinking=disabled` 下使用 `tool_choice=required`，先通过独立接口冒烟，请求 accepted 且返回了工具调用。

### 指标

| 指标 | A: low + auto | B: disabled + auto | C2: disabled + required |
|---|---:|---:|---:|
| plain final 次数 / run | 13 / 93 (14.0%) | 3 / 93 (3.2%) | **0 / 93** |
| protocol validation error run | 0 | 0 | **0** |
| 协议修复 run 比例 | 13.98% | 3.23% | **0%** |
| 修复耗时（全体均值） | 169.3ms | 36.3ms | **0ms** |
| 修复耗时（发生修复时均值） | 1,211ms | 1,125ms | N/A |
| 模型调用均值/中位 | 2.527 / 2 | 2.387 / 2 | **2.269 / 2** |
| 工具执行均值 | 1.387 | 1.355 | 1.269 |
| 输入 token 均值 | 16,038 | 14,403 | 13,738 |
| 输出 token 均值 | 454 | 296 | 268 |
| reasoning token 均值 | 154.3 | **0** | **0** |
| 延迟 P50 | 3,643ms | 3,149ms | **2,892ms** |
| 延迟 P95 | 7,848ms | 4,565ms | **4,477ms** |
| 时间线对账率 | 100% | 100% | 100% |

对比上一轮同开发集的候选路径（Thinking=low、旧终态提示），plain final/协议修复率约为 **33.3% → 14.0%（统一提示）→ 3.2%（关闭 Thinking）→ 0%（required）**。

- 把 Thinking 切到 disabled，在统一提示下 P50 从 3,643ms 降到 3,149ms，P95 从 7,848ms 降到 4,565ms；同时 reasoning tokens 全为 0。
- 在 disabled 基础上加 `tool_choice=required`，P50 再降到 2,892ms，P95 4,477ms；plain final 和协议修复归零。
- 这些是单轮候选-only 数据，没有复用旧 baseline 的实时配对；延迟仍受服务时间和缓存状态影响，不能直接当作最终门槛结论。
- A/B/C2 中实际 continuation 分别为 3、2、1 次，人工核对原始日志均从第一页进入下一页并取得新 auto-read 证据，没有重复第一页。`runs.jsonl` 未结构化保存 continuation handle，因此该计数未进入自动汇总。
- C 首跑（修复前）仍有 0 次 plain final，但出现 1 次非法 submit_answer 校验失败；C2（修复后）为 0。这个差异说明协议修复计数本身也需要可验证，不能用“看起来没有 plain final”代替完整协议错误观测。

### 解释边界

- 协议修复率不能预期降到零；A 的结果说明统一提示已经明显减少 plain final，剩余问题可由 Thinking 和 required tool 进一步压缩。
- 关闭 Thinking 的收益同时包含生成速度、输出长度和协议遵守；不能把其中某一部分单独归因给“推理时间”。
- tool_choice 只在关闭 Thinking 下做了接口冒烟；没有假设思考开启时同样支持 required。默认配置仍不设置 tool_choice。
- 三个变体未新增在线 judge 调用，因此本轮不产生新的答案质量通过率。协议通过不等于答案正确。

## 3. 失败案例人工复核

复核对象是上一轮真实配对候选的失败/争议案例以及本轮 C2 回答。没有调用新 judge。判断依据是：原始 judge 逐条向量、当时实际交付的 evidence catalog、规则源数据/完整规则文本。

| 案例 | 实际回答/判分现象 | 分类 |
|---|---|---|
| Seasons 水晶怎么获得（gold-054） | 上一轮三次候选回答都是一票 fail、一票 pass。C2 仍有两轮加入“有些卡牌/季节效果直接给水晶”，其中一轮还补充终局每水晶一分；这些扩展不在该次交付的概念正文中。完整规则文本 `doc/seasons/SEASONS_RULES_US.md`（如 Balance of Ishtar、Die of Malice 等条目）确有卡牌获得水晶的例子。 | “规则可能正确，但本次没有读取到依据” + “有依据但属于用户没问的扩展”。失败 judge 对证据边界的指摘成立，但不能据此认定游戏规则错误。 |
| Ark Nova 终局计分卡（gold-044） | 失败 judge 说“大学/面前卡牌/保护分计数器”无证据；实际交付的 `score_final_scoring_cards` 正文明确含“计分时考虑自己的大学、面前卡牌及其图标”和“保护分推进计数器并计入总分”。但 judge 的标准可接受片段只列了 `final_scoring_card`，且部分回答还额外展开用户没问的终局图标和胜负公式。 | 核心细节主要是裁判材料/判分遗漏；额外展开是回答层面的无关扩展。不是已证明的规则错误。 |
| Wingspan 打鸟（gold-060） | 失败 judge 说“蛋从自己鸟牌上拿”无依据；实际交付的 `play_bird` 结构化费用树写有蛋费的 `source: 该玩家鸟牌上的任意 <egg>`，只是旧 judge 材料只抽 `description`，未注入嵌套 cost 字段。C2 的三次回答没有再把该来源写成新的无依据断言。 | 主要是裁判材料遗漏；规则有正式读取依据，不是未读取的编造。 |
| Agricola 家庭成长（gold-014） | 上一轮一次回答把“本回合先建房再成长也不行”表述成“不能先成长后补建房间”，judge 认为偏离；另外两次 pass。C2 三次均以 clarify 结束，没有形成稳定规则错误。 | 语义/表述歧义或 judge 判分差异，不是稳定的规则错误。 |

人工复核结论：

- 目前没有发现需要针对具体游戏写答案补丁的、已确认的系统性规则错误。
- Seasons 的“卡牌直接给水晶”仍应作为证据支撑缺口处理：规则事实可能成立，但本次交付没有覆盖对应卡牌；若未来要回答“所有获得途径”，需要内容/交付治理，不能靠 runtime 内置游戏答案。
- Ark Nova、Wingspan 的争议至少部分是评审材料遗漏。本轮已修改离线评审工具：`fetch_rule_details.py` 现在保留 target/cost/content/score 等结构化正式字段，`judge_answers.py` 会把它们注入 judge 材料；这只修复后续评审输入，**没有重跑本轮 judge**，也不改变本轮质量结论。
- 仍有一个未闭口：`build_question_ground_truth.py` 的 `acceptable_rule_evidence` 仍按 `expected_rule_ids` 取窄描述，例如 Ark Nova 只列了 `final_scoring_card`，没有把 `score_final_scoring_cards` 作为可接受目标。下一次盲评前应先人工扩充/冻结可接受规则集合，不能拿本次 judge 结果直接代表答案质量。

## 4. 性能口径

- 上一轮 self-time 显示模型请求约 87.6%，程序内部约 11.6%。C2 本次模型 self 均值为 2,607ms，约占端点均值的 85.6%；模型请求仍然是最大成本项。
- 候选的 240 次模型调用均记录 reasoning token，中位数为 30.5，其中 63 次为零。该记录只能说明 API 可见的 reasoning token 分布，不能推算内部思考时长；输入处理、答案生成、网络和服务端等待都包含在模型请求耗时内。
- 按原始记录粗略估算：协议修复成本摊到全部候选请求约 370ms/请求；每条请求扣除已记录修复耗时后中位约 3,430ms，原定目标 3,235ms。这是估算，不是新实验，但说明不能预期“修复率归零”后性能目标自然完成。
- 程序正式读取约 395ms 仍值得后续剖析（本轮 C2 read self 均值 315.6ms），但当前协议修复和模型调用仍是更优先的可解释成本。

## 5. 结论与下一步

- 当前方向继续：程序接管、意图优先、continuation handle、严格预算和 self-time 均保留。
- 从协议和延迟角度，**C2（disabled + required）是下一候选**；但本轮没有新的在线 judge 质量结论，因此不能直接切默认。
- 默认配置仍保持 Thinking=low、ToolChoice 为空、`OrchestrationMode=legacy`；本报告不宣称发布切换。
- 31 题继续作为开发集；新 36 题继续封存，不进入本轮调试。
- 不增加在线裁判调用，不为单款游戏写 answer patch，不换架构。

进入更大规模同轮验证前的条件：

1. 先用修复后的评审材料对一个开发小样本做人工检查，确认结构化 cost/target 确实进入 judge 输入，并扩充/冻结 Ark Nova 等题的可接受规则集合；仍不增加本轮在线 judge 调用。
2. 运行同一候选的协议/质量小对照时，把“模型配置/思考模式”与“回答证据策略”分开，不同时改证据、模型配置和回答策略。
3. Seasons 的证据扩展问题若要在回答层修复，应作为独立的回答约束实验；先验证它对开发集无回归，再进入更大规模实验。
4. 只有小改动继续显示稳定方向且人工复核无新增系统性错误，才用 31 题做更大规模同轮验证；36 题保持封存，直到候选冻结。

## 6. 本次运行说明

- 候选 API：5002；A/B/C/C2 按变体切换 Thinking/ToolChoice。
- 首次 C 与修复后 C2 均保留在 `.tmp/protocol-comparison/`。汇总采用 C2，并在 `10-protocol-variants.json` 中同时保留 `C-pre-fix` 作为审计记录。
- 本轮汇总由 `tools/eval/analyze_protocol_variants.py` 从 `runs.jsonl` 重新生成，避免手工计算指标。
- 评审工具行为测试：`python3 -m unittest discover -s tools/eval -p 'test_*.py'`。
