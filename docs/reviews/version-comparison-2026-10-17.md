> 历史路径说明（2026-10-23）：本文保留当时本地 scratch 路径 `.tmp/...`，这些不是当前运行依赖。新实验请使用 `$BOARD_RUNTIME_ROOT/tmp/...`；2026-10-23 之前的原始 agent/eval 产物已压缩到 `$BOARD_RUNTIME_ROOT/archive/agent-workspace-evidence-20261023-004025.tar.zst`，需要时解压后按原相对路径读取。

# 规则问答新旧版本全面对比报告

日期：2026-10-17  
旧版 commit：`36e4dbf`  
新版 commit：`d8862ac feat(rules): preserve query intent and converge answers`  
被测模型：`deepseek-v4-flash`，`LLM:Thinking=low`，仓库当前超时/轮次配置  
规则内容哈希：`e2441e933ae25e42e25eb3d7574f2309141c5cdb4c2e068b4358ccca96951332`

## 1. 结论

**新版在规则问答质量上优于旧版，且证据确认完整度大幅提高；代价是明显的延迟增加。**

本实验的保守主指标为两个盲评 judge 都通过的严格回答成功率：

| 指标 | 旧版 | 新版 | 差值 |
|---|---:|---:|---:|
| 严格回答成功率 | 60.4% | 75.5% | **+15.1pp** |
| 问题级多数票通过率 | 61.5% | 82.0% | **+20.5pp** |
| 正例直接命中目标规则 | 61.3% | 80.3% | **+19.1pp** |
| 正例直接命中或候选前三 | 87.0% | 89.5% | +2.5pp |
| tier1 率 | 37.3% | 90.3% | **+53.0pp** |
| pending=0 率 | 37.9% | 90.3% | **+52.4pp** |
| 错误规则/编造率 | 36.2% | 22.2% | **-14.0pp** |
| 无关扩展率 | 11.7% | 3.1% | **-8.5pp** |
| 不必要追问率 | 0.9% | 0.0% | -0.9pp |
| 平均 query 数 | 2.52 | 2.45 | -0.07 |
| 延迟中位数 | 2732 ms | 3572 ms | **+840 ms** |
| 延迟 P95 | 5689 ms | 8852 ms | **+3163 ms** |

主配对检验：

- 可配对问题 117 个，每问每版 3 次运行。
- 每次运行通过率差均值：**+15.1pp**，问题级 Bootstrap 95% CI **[+8.0, +22.2]pp**。
- McNemar 精确检验：旧版单独多数通过 8 问，新版单独多数通过 32 问，**p=0.0002**。
- 正例规则直接命中率配对差：**+19.1pp**，95% CI **[+9.8, +28.3]pp**，McNemar **p=0.0009**。
- 延迟差均值 **+1357 ms**，95% CI **[+1087, +1630] ms**。
- query 数差 **-0.07**，95% CI 跨零，未观察到工具计划数量下降/上升。

因此，若目标是回答正确性、规则证据完整和少编规则，**新版更好**；若目标是低延迟，新版目前要付出约 1.3 秒的额外平均成本。

## 2. 实验条件

### 版本与运行环境

- 旧版从 `36e4dbf` 独立 worktree 构建，新版为当前 `d8862ac`。
- 两版使用同一份当前 `content/` 快照和同一个 Qdrant 运行库。
- 两版各自使用仓库中的 SystemPrompt/schema，这是本次端到端处理变量。
- 两个 API 分别运行在 5001 / 5002，配对发送同一问题。
- 每次请求独立 messages；不携带上一次上下文。
- 每版启动后先跑 2 条非测试问题热身并丢弃。
- 同一问题旧/新版请求成对启动，提交顺序随机化；每问重复 3 次。

### 测试集

`tools/qa/comprehensive_eval_questions.jsonl`，共 **117 条问题**：

| 来源 | 问题数 | 说明 |
|---|---:|---|
| 既有 execute-plan gold | 85 | 九款游戏，explain/condition 规则定位 |
| 既有 query intent 回归 | 13 | 原案例、口语、步骤、数量、多问题、准确 ID、Agricola/Wingspan 意图 |
| 既有检索回归 | 7 | identify、外观和整句语义约束 |
| 手工负例/歧义 | 12 | 虚构规则、错误指代、需要澄清 |

分布：

- 正例 105 条，负例/歧义 12 条。
- 覆盖全部 9 款现有规则游戏：agricola、ark-nova、brass-birmingham、castles-of-burgundy、civolution、puerto-rico、seasons、splendor、wingspan。
- 每题两版各 3 次，共 **702 次聊天**。
- HTTP 错误 0，judge 失败 0，无重试后丢样本。

### 盲评

最终回答隐藏版本标签，由两个独立 judge 评审：

- `deepseek-v4-flash`：被测系统同模型名的独立评分调用；
- `deepseek-chat`：另一模型名；
- 主结果采用保守共识：两个 judge 都 pass 才算 pass。
- 两个 judge 的 run-level pass 判定一致率 **80.9%**。

个体 judge 结果也同向：

| Judge | 旧版通过率 | 新版通过率 | 差值 | McNemar p |
|---|---:|---:|---:|---:|
| deepseek-v4-flash | 61.3% | 75.8% | +14.5pp | 0.0003 |
| deepseek-chat | 81.2% | 91.7% | +10.5pp | 0.0127 |
| 两者都通过（主指标） | 60.4% | 75.5% | +15.1pp | 0.0002 |

judge 仍是 LLM 评审，不是绝对真理；该不一致率与人工抽检限制已列入残留问题。

## 3. 分游戏结果

主指标为两 judge 共识严格通过率；每款游戏约 8–21 个问题、每问 3 次。

| 游戏 | 问题数 | 旧版 | 新版 | 差值 |
|---|---:|---:|---:|---:|
| agricola | 17 | 74.5% | 80.4% | +5.9pp |
| ark-nova | 8 | 70.8% | 91.7% | +20.8pp |
| brass-birmingham | 11 | 42.4% | 78.8% | **+36.4pp** |
| castles-of-burgundy | 9 | 59.3% | 77.8% | +18.5pp |
| civolution | 19 | 35.1% | 45.6% | +10.5pp |
| puerto-rico | 10 | 53.3% | 73.3% | +20.0pp |
| seasons | 8 | 70.8% | 75.0% | +4.2pp |
| splendor | 21 | 76.2% | 87.3% | +11.1pp |
| wingspan | 14 | 61.9% | 81.0% | +19.1pp |

所有 9 款游戏的总体点估计都朝新版方向；Brass Birmingham、Ark Nova、Puerto Rico、Wingspan 提升最明显。Civolution 的绝对表现仍最低，是下一轮最值得优先排查的游戏。

## 4. 分问题类别

| 类别 | 问题数 | 旧版 | 新版 | 差值 |
|---|---:|---:|---:|---:|
| retrieval_condition | 39 | 59.8% | 85.5% | +25.6pp |
| retrieval_explain | 46 | 62.3% | 74.6% | +12.3pp |
| intent_and_multi | 13 | 71.8% | 74.4% | +2.6pp |
| negative_or_ambiguous | 12 | 52.8% | 55.6% | +2.8pp |
| regression_identify | 2 | 33.3% | 83.3% | +50.0pp |
| regression_boundary | 1 | 33.3% | 100.0% | +66.7pp |
| regression_explain | 4 | 50.0% | 41.7% | -8.3pp |

注意：

- `regression_identify` 和 `regression_boundary` 只有 2 条、1 条问题，比例波动很大，不能当成稳定类别结论。
- `regression_explain` 的点估计为 -8.3pp，但只有 4 条问题；主要来自 Favor 测试解释题在两个 judge 下的严格表述差异。该小类不足以抵消总体和分游戏的一致优势，但应作为人工复核项。
- `negative_or_ambiguous` 只小幅提升，仍是新版应继续优化的区域。

### 负例/歧义的细分表现

| 负例子类 | 旧版 | 新版 | 结论 |
|---|---:|---:|---|
| 否定不存在规则 | 41.7% | 62.5% | 新版更好 |
| 需要澄清指代 | 75.0% | 41.7% | 新版更差，容易先给相关规则再问 |

新版在 can't-answer 场景更少编造不存在规则，但在“这张牌/那个标记”指代不明时，仍可能先讲一大段相关规则。这是本次质量提升中最清晰的残留短板。

## 5. 代表性行为差异

### 原问题：贵族 setup

旧版示例（同一实验中的实际回答）：

> 不用挑三个。贵族是随机抽的，抽的数量是玩家人数加一枚；比如两个人就抽三枚，三个人抽四枚，四个人抽五枚，然后正面朝上摆出来。

新版示例：

> 这三块不用你自己挑，规则是从盒里随机盲抽，摆出的数量比玩家人数多一枚，抽到哪几块就正面朝上摆哪几块。

新版更直接承接三块，不主动展开三人/四人配置。旧版该问的规则直接命中率明显更低。

### 证据完整度

新版 tier1 率 90.3%，旧版 37.3%。主要原因是新版会保留完整 intent，并在对象名命中后继续找回设置/获得/结算流程；旧版更容易停在宽泛对象或 partial 状态。

### 仍存在的 judge 敏感性

Agricola“开局职业卡怎么发”一题，两版回答都覆盖按人数筛选、洗混、每人七张；新版因为把 `[1+] / [3+] / [4]` 写得更具体，被一个 judge 判为“证据未明确支持”，但规则内容本身基本正确。这说明：
- LLM judge 对“额外但正确的表述”并不稳定；
- 保守双 judge 共识会低估部分正确回答；
- 结论建立在两 judge 同向和确定性 evidence 指标同时改善的基础上，不应把单个 judge 的小类波动当成产品结论。

## 6. 性能与代价

| 指标 | 旧版 | 新版 |
|---|---:|---:|
| 端到端延迟中位数 | 2732 ms | 3572 ms |
| 端到端延迟 P95 | 5689 ms | 8852 ms |
| 平均 query 数 | 2.52 | 2.45 |
| run 数 | 351 | 351 |
| HTTP 错误 | 0 | 0 |

新版为确认候选和回答约束增加了额外 LLM 往返，典型代价是数百毫秒到数秒。质量提升不是免费的。

## 7. 最终判断

**质量优先：新版胜出，应作为默认版本。**

理由：

1. 严格回答成功率 +15.1pp，95% CI 明确排除 0；
2. 规则直接命中 +19.1pp，证据 tier1/pending 状态大幅改善；
3. 编造/矛盾率下降 14pp；
4. 9 款游戏的总体点估计全部不差于旧版；
5. 代价是延迟中位数增加约 0.84 秒、P95 增加约 3.2 秒。

**延迟优先场景：需要先优化新版的确认轮次和重复 query，再替换旧版。** 当前不应宣称新版在所有交互指标上全面胜出。

## 8. 复现与证据路径

关键文件：

- 题集：`tools/qa/comprehensive_eval_questions.jsonl`
- 运行器：`tools/eval/compare_versions.py`
- 规则片段生成器：`tools/eval/build_question_ground_truth.py`
- 概念详情生成器：`tools/eval/fetch_rule_details.py`
- 盲评器：`tools/eval/judge_answers.py`
- judge 共识：`tools/eval/combine_judges.py`
- 分析器：`tools/eval/analyze_compare.py`
- 操作说明：`tools/eval/README.md`

本次运行生成物（gitignored，保留在本机 `.tmp/`）：

- `.tmp/comprehensive-eval/main/runs.jsonl`：702 次原始聊天和 evidence；
- `.tmp/comprehensive-eval/main/judge.jsonl`：deepseek-v4-flash 评分；
- `.tmp/comprehensive-eval/main/judge-deepseek-chat.jsonl`：deepseek-chat 评分；
- `.tmp/comprehensive-eval/main/judge-consensus.jsonl`：保守共识；
- `.tmp/comprehensive-eval/main/rule_details.json`：匹配/引用概念详情；
- `.tmp/comprehensive-eval/main/analysis-consensus/summary.md`：自动汇总；
- `.tmp/comprehensive-eval/main/analysis-consensus/analysis.json`：完整数值。

未修改生产规则、动画、音频或 cue；本报告只新增评测数据、工具和结果结论。
