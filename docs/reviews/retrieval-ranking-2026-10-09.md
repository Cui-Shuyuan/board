# 检索排序修改与实测（2026-10-09）

本轮修复了已经召回正确概念、却在融合或计划解析时被丢弃的问题。修改后的 71 次同题聊天中，“白色骰子”六次均查询到了激活骰；Favor 测试不再自动解析成恩惠标记，point bonus 卡不再自动解析成玩家辅助卡。原始向量召回仍有噪声，本轮没有证明所有 FAQ 的答案都正确。

## 修改内容

- 完整查询主导排序。分数为整句向量分 + 整句关键词分 + 0.02 × 子词通道平均分；同一身份路径、同一查询、同一通道重复命中取最大值。整句关键词要求所有分词出现在同一个概念中，避免只有“卡”也被算作“point bonus 卡”的整句匹配。
- identify 保留颜色、形状、位置等完整描述，不再因问题里出现“骰子”就返回通用 dice。精确 ID 可直接解析，外观描述返回候选及定义，让模型核对后按确切 ID 查询。
- explain 联合全文和名称检索。名称索引补充缺失目标，其分数上限为 0.55，保留原始 NameMatchScore，不能凭补漏分自动确认；全文中的弱关键词重复不能挡住名称补漏。计划最多返回 15 个候选。
- boundary 允许对象、本体、局部槽位等实体，避免把已经排第一的分数轨过滤掉。
- 自动语义确认同时要求整句向量 ≥ 0.72、整句关键词 ≥ 0.30、总分 ≥ 0.80，且与第二名相差 ≥ 0.10。混合分数只是启发式排序分，不是正确概率；精确 ID/名称解析保持原路径。
- 日志记录完整 request ID，避免所有新连接都显示相同的末尾编号。

这些权重和阈值用于修复本次已观察的错误，仍需更大标注集校准。本轮保留现有 embedding、索引内容和候选向量最低分，没有修改规则文档。

## 实测范围

启动真实 BoardAI.Api，使用当前 DeepSeek V4 Flash、thinking=low；逐题建立新会话，重放修改前同一组 71 次 /api/chat 请求：Civolution 多人 FAQ 50 题、Splendor gold 原问句 10 题、追加问题与骰子重复提问 11 次。FAQ 只用作问题来源，不把其答案当作规则真理；Civolution 单人 FAQ 未覆盖。

另外直接调用 execute-plan 跑既有 85 条 gold 和新增 7 条回归，并记录 65 个检索查询的原始全文/名称 top30、正式融合结果及分数账本。诊断用 top30 不设最低分；正式检索仍是向量 top15、最低分 0.55。133 条后端 xUnit 全部通过；提交 hook 的规则校验为 0 errors / 72 warnings。

代码修改前聊天基线为 5100d8e，本轮代码提交为 7b6c9c6；期间项目已有的索引收口文档提交为 1a1d0ea。Civolution 活动全文版本 75a5bd36f360c382（434 条）、Splendor 03283886a77eb0de（156 条），两个聊天批次均使用这些版本；九款活动 alias 前后对比见本地 summary.json。本轮没有重建索引。

## 可复现案例

| 查询 / 分支 | 修改前 | 修改后 |
|---|---|---|
| identify 白色骰子 | question_hit 直接返回通用 dice，绕过全文检索 | activation_die 候选第一，随后按 ID 查询 |
| 白色 骰子，全文融合 | dice 第一；激活骰第二 | 激活骰第一，0.6114；通用骰子仅 0.0130 |
| explain Favor 测试 | 恩惠标记第一并自动确认，检定第四 | favor_test 第一，0.5808；候选交给模型核对 |
| explain point bonus 卡 | player_aid 第一并自动确认，终局槽位第六 | 分数奖励相关项占前三；目标 final_scoring_area_hex.point_bonus 第三 |
| boundary 分数轨道 | score_track 原始第一，但被类型过滤 | score_track 返回候选第一 |
| explain 收获喂养 | 原有名称检索可命中 | 保留名称补漏，harvest_feeding 第三；没有因改用全文而丢失 |

白色骰子两种表述各重复三次：修改前六次中四次最终查询到激活骰，两次只接受通用骰子；修改后六次均 identify → explain activation_die，两条查询完成。6/6 是这批样本的观察，不能外推为稳定成功率。

## 评测结果及代价

| 既有 85 条 execute-plan gold | 修改前 | 修改后 |
|---|---:|---:|
| 直接解析到预期目标 | 82 | 77 |
| 返回候选，目标在前三 | 3 | 8 |
| 上述候选中目标第一 | 3 | 7 |
| 错误自动解析 / no_match | 0 / 0 | 0 / 0 |
| 直接命中或前三可找回 | 85 | 85 |

新增 7 条自然语言回归：全部返回候选，目标均在前三，其中五条在第一；错误自动确认和 no_match 均为零。point bonus 卡、收获喂养的预期目标仍为第三。目标在前三不等于模型必然选对，也不等于答案准确率。既有 gold 预设了 relation/entity，不能替代自然问句评测。

更谨慎的自动确认让五条原本直接命中的用例需要模型核对候选。这是明确的代价；本轮只验证目标没有丢失，没有把直接解析下降包装成命中率提高。

| 同一组 71 次完整聊天 | 修改前 | 修改后 |
|---|---:|---:|
| HTTP 错误 | 0 | 0 |
| 耗时中位数 / 均值（秒） | 4.89 / 6.35 | 3.58 / 5.05 |
| 累计规则查询数 | 276 | 268 |

两批没有随机交错执行，模型输出、缓存和服务时延会变动，不能把耗时差完全归因于修改，也没有做速度保证。

Civolution 50 题的 evidence：修改前 tier1=20、partial=29、tier3=1；修改后 tier1=8、partial=40、tier2=1、tier3=1。Splendor 10 题：修改前 tier1=3、partial=4、tier2=3；修改后 tier1=1、partial=5、tier2=4。partial 经常只是先返回候选、后来查到确定 ID，不能算答错；tier1 也不能算答对。两批均没有记录工具循环因预算/重复失败停止。

## 仍值得继续做的工作

1. 扩充自然问句标注集：记录完整问句、模型计划、候选和应查的事实路径。把“正确组件”“正确规则片段”“最终答案”分开评测，增加无答案、歧义、同类卡牌和中英混写问题。现有七条回归不足以校准通用权重。
2. 在同一固定候选集上比较重排方案。原始全文仍有错误，例如 Splendor“金色的筹码能当任意颜色宝石用吗”前五没有 gold；“不买牌也能拿到贵族吗”把 deal_nobles 排在 attract_noble 前。“骰子修改”也将 favor_test 排在 adjust_die_value 前。这些不是单靠避免重复累计就能解决的；下一轮应比较事实片段索引、名称/全文联合召回和 reranker，以标注集上的排名决定取舍。
3. 核查同类卡牌检索和事实推断。突变牌的候选包含模组、事件牌和通用牌，聊天仍会浏览目录；升级相关回答也可能由已查事实推导出没有被直接支持的约束。本轮不宣称这些 FAQ 已通过规则正确性验收。
4. 区分“途中未解析”与“最终仍未覆盖”。白色骰子已按 ID 查到事实，最终 evidence 仍因此前 identify=unresolved 标为 partial；应保留历史诊断，同时让客户端提示反映尚未解决的需求。此项未修改。

## 复查入口

服务启动后运行：

```bash
python tools/indexing/eval_retrieval.py --gold tools/qa/retrieval_gold.jsonl --api http://localhost:5000
python tools/indexing/eval_retrieval.py --gold tools/qa/retrieval_regressions.jsonl --api http://localhost:5000
dotnet test backend/BoardAI.Api.Tests/BoardAI.Api.Tests.csproj --nologo
```

完整本地记录位于 .tmp/retrieval-fix-verified-20261009/：chat-results.jsonl、answers-and-plans.md、search-traces.jsonl、targeted-plans.json、gold-eval.json、new-regressions.json、summary.json、api.log。修改前记录位于 .tmp/retrieval-diagnostic-20261009/。这些生成物被 gitignore；持久结论保存在本报告和待办中。回放脚本 run_chat_replay.py 会真实请求模型并产生费用；需要已编译 API、本地 Qdrant、现有模型与 .env，端口占用时拒绝启动，运行后按 api-owner.json 核验并清理自己启动的 API。
