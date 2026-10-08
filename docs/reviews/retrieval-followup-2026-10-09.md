# 检索、规则事实与证据恢复：后续实测

日期：2026-10-09。接续 [首轮排序修改](retrieval-ranking-2026-10-09.md)。本轮目标是提高相关性、比较方案、核查规则事实，并改善查询证据的恢复记录；不扩展棋盘运行时。

## 结论与已经落地的修改

本轮最有效的变化是修正 embedding 的池化方式，而不是继续加权。旧实现将 token 向量平均；现在默认使用 BGE 的 CLS 向量并归一化。BGE 的官方 [模型配置](https://huggingface.co/BAAI/bge-base-zh-v1.5/raw/main/1_Pooling/config.json) 指定 CLS pooling；模型用法见 [官方模型页](https://huggingface.co/BAAI/bge-base-zh-v1.5)。

- API 和 Python 重建工具共用 `Embedding:Pooling`，环境变量为 `Embedding__Pooling`；当前默认 `cls`，保留 `mean` 回滚方式。
- CLS 的召回底线为 0.50，mean 保留 0.55；名称补漏分数上限跟随全文召回底线。召回分只决定候选能否展示，自动确认仍要求强整句文字/向量证据和领先分差。
- 问题原文命中的名称改为上下文候选，不能直接确认未识别的查询实体。这避免“未知能力的问句提到模组，就确认成模组”。
- 索引版本区分 `bge-base-zh-v1.5-fp32` 与 `bge-base-zh-v1.5-fp32/cls-v1`。查询先解析物理 collection 并核查 embedding profile，防止 CLS 查询旧 mean 坐标。配置与索引不兼容时向量通道拒绝返回结果并记录 warning，关键词通道仍可工作。
- 超过 512 token 时，C# 的 CLS 路径保留句尾分隔 token，与 Python tokenizer 对齐。真实 ONNX 对比覆盖短中文、中英混写、空输入和 1600 字长输入，CLS 最大逐维误差小于 `3.5e-7`。legacy mean 长输入原有截断差异保留，以免悄悄改变回滚坐标。
- 本机九款游戏的 18 个全文/名称 alias 已迁移到 CLS，旧 collection 保留。没有推送 Git，也没有部署到其他 API 主机。

事实片段、查询指令前缀、RRF 与本地 cross encoder 均已比较。它们没有稳定胜过 CLS 整段概念，因此未接入正式问答路径；不是为了节省几毫秒放弃相关性，而是当前证据不支持增加这层复杂度。

## 自然问句数据与比较方法

新增 `tools/qa/retrieval_questions.jsonl`：53 条手写问句，覆盖九款游戏。48 条可回答问题分成 20 条 dev、28 条 holdout；另外有三条虚构实体与两条歧义问题。同一主题的近似问句只进入一个分组，防止改写泄漏。每条正例标注主要组件、分级相关组件和真实 JSON 文件/指针；FAQ 答案不作为真值。

实验先冻结标注，再比较模型/投影。全文沿用正式索引提取文本；事实片段提取中文定义、描述、数量、材质等字段，附原始指针并将引用展开为概念名。名称文本严格沿用正式索引的中文名优先规则。Civolution 投影数为全文 434、片段 1068、名称 412。

两个 cross encoder 方案使用同一个候选池：mean 全文前 30、CLS 全文前 30、CLS 片段前 30、名称前 15 的并集，经 RRF 选前 60。重排输入分别是最佳事实片段、组件中文描述加最佳片段；没有把预期 ID 特意塞入候选池。这 60 个候选覆盖了全部 48 条正例的主要目标，重排未改善不能归因于主要目标未召回。

下表是独立排名实验，**不是最终回答准确率**。命中以预先指定的主要概念为准；自然问句可能也能由其他合法事实回答，当前标注并不穷尽全部有效路径。holdout 已用于方案比较，后续调参应使用新的验证集。

| 方案 | 28 条 holdout 首位 | holdout 前三 | MRR |
|---|---:|---:|---:|
| mean 整段概念 | 6 | 17 | 0.4227 |
| CLS 整段概念 | 17 | 23 | 0.7134 |
| CLS 整段 + 查询指令 | 13 | 24 | 0.6413 |
| mean 事实片段 | 12 | 20 | 0.6046 |
| CLS 事实片段 | 17 | 20 | 0.7063 |
| RRF 并集 | 16 | 22 | 0.6920 |
| cross encoder：最佳片段 | 14 | 19 | 0.6465 |
| cross encoder：组件描述 + 片段 | 16 | 23 | 0.7023 |

全部 48 条正例：mean 整段首位 16 / 前三 32；CLS 整段首位 28 / 前三 41。真实 API 的融合排序另测 53 条，正例首位 29/48、前三 41/48；holdout 首位 17/28、前三 23/28。五条负例的 explain 全部返回 unresolved，没有被程序自动确认为已知实体。这不等于已证明无答案检测：它们仍可能返回不相关候选。

## API 门禁与代价

| execute-plan 门禁 | 上一轮 mean | 本轮 CLS |
|---|---:|---:|
| 85 条：直接命中预期实体 | 77 | 77 |
| 85 条：返回候选 | 8 | 8 |
| 上述候选中目标第一 | 7 | 8 |
| 错误直接确认 / no_match | 0 / 0 | 0 / 0 |
| 7 条描述性回归：前三 | 7 | 7 |
| 7 条描述性回归：第一 | 5 | 7 |

迁移试验发现 CLS 分数尺度与旧阈值不同：“白色骰子”的正确全文分为约 0.546，被旧 0.55 底线截掉。先保留失败记录，再统一调整 CLS 召回底线与名称补漏上限；没有为这个词加特判，也没有放松自动确认门槛。

后端 xUnit 161/161 通过；Android JVM 91/91 通过，新增了恢复证据解析用例；检索实验 Python 5/5 通过。规则校验仍为 0 errors / 72 warnings。Android 测试使用隔离的 `.tmp/gradle-evidence-home`，没有清理用户全局 Gradle 缓存；不等于真机验收。

Python animation 165/165 也通过，其中索引契约测试现在同时校验九款游戏的 mean / CLS 版本行，C# 对相同 18 个版本逐一断言。

真实 API 重放上一轮相同的 71 次聊天，另加五条负例：76 请求、0 HTTP 错误；六次白色骰子都查询到了 `activation_die`。76 条的 evidence 分布是 tier1=14、partial=54、tier2=7、tier3=1；观察到一条历史单候选查询被精确查询恢复。中位耗时为相同 71 题的 3.99 秒，不将单次运行的耗时变化归因于检索。终局九类计分的大问题触发了 60 秒预算上限，收到预算结束回复，不能把 HTTP 200 算作答题成功。

其中一次白色骰子回复额外说“每次激活模组要掷出两颗这样的骰子”，这是超出已查规则的错误补充：激活是支付已有点数的骰子，重掷属于 Reset 等明确步骤。故 6/6 只表示实体最终查对，不能表述为六个完整回答全部正确。负例聊天虽没有把虚构能力当作有效能力使用，但对未登记卡牌直接宣称“游戏没有此牌”仍缺少穷尽性证据。

## 规则书事实核查与修正

核查来源是项目本地规则书文本，不使用未核查 FAQ 答案来改规则：

- [Civolution 规则书第 27 页对应文本](../../content/games/civolution/Civolution_Rules_US_web_v1_0.txt#L1542)：正在执行本回合激活的模组行动时若升级它，新等级要到自己的下一回合才能用。此前 `upgrade_main_module` 只写等级变化/翻面，`activate_module` 只写当前等级，漏了这个限定。实际聊天即使 tier1，也说出“升级是单独行动”和“同一回合先升级后激活即可”的不可靠补充。
- 已在 `flow.json` 的 `upgrade_main_module`、L2→L3 子步骤及 `activate_module` 双语描述中补齐限定，区分露出新效果与本次执行是否可用；没有引入实时状态 DSL。
- 改动后只重建 Civolution 的全文/名称索引并原子切换，计数仍为 434 / 412；当前 CLS 版本为 `2fbdea8f273e564e`，同内容 mean 版本为 `9c19ea7abb301bd2`。旧 CLS 版本 `c5cc5c529da162ee` 和原始 mean 索引都保留。
- 用真实 API 追加三种升级问法、突变奖励和白色骰子，共五次请求。三种升级问法都明确回答本次不能换用新等级、等自己的下一回合；突变奖励回答以该张牌列出的动作为准。总计本轮 81 次聊天请求，0 HTTP 错误。修正后重新运行 85 + 7 门禁及 53 条自然问句，指标未退化。
- [规则书第 25 页对应文本](../../content/games/civolution/Civolution_Rules_US_web_v1_0.txt#L1433) 和现有 `instant_bonus` 明确逐卡结算列出的动作；没有把“突变牌即时奖励总会推进轨道”等概括补进 JSON。建造牌的替代要求按 [第 24 页示例](../../content/games/civolution/Civolution_Rules_US_web_v1_0.txt#L1417) 核对，不能泛化为所有卡牌都适用。

新增 `tools/qa/rule_fact_regressions.jsonl` 记录两条手写回答验收要求、允许/拒绝的断言及来源。它不是字符串匹配评分器，也没有宣称完成全部 FAQ 的逐事实验收。

## Evidence 的改善与限制

新增 `pendingCount` 和逐查询 `resolvedByQueryIndex`，同时保留原始 unresolved 状态和历史计数。一个 unresolved 查询只有一个候选时，后续同规则版本、同关系的精确 ID/中文名查询命中该候选，可以记为恢复；identify 可由后续 explain 恢复。Android 不再把这种已恢复项提示成仍缺少证据。

多候选、不同关系、不同规则版本、no_match 或 unsupported 不自动清除。查询到多个候选中的某一个，也可能只是回答了问题的一部分，因此仍保守标为 partial。白色骰子在 CLS 下有多个候选时仍会出现这一限制。下一步如要准确覆盖多候选恢复，需要显式记录“这次精确查询解决了哪个先前查询”，不能只看后来是否查到某个 ID。

`tier1` / `isComplete` 表示工具查询的数据覆盖，不是 LLM 回答正确的证明。模型选错概念、推断过度、遗漏限定词、口播数字丢失，都需要回答层验收。

## 尚存检索偏差

冻结的主要概念标签没有为提高成绩而改写。真实 API 中以下七条正例未达到主要目标前三：

- Splendor 黄金万能支付：前排仍是拿取宝石、供应堆；这是明显的相关性偏差。
- Splendor 从牌堆顶保留：前排为通用 top_draw、deck，未优先返回游戏行动。
- Civolution 分轨绕圈、倒退过零：可能先返回绕圈事件或一百分标记。这些可能含有效事实，组件标签的遗漏需要与实际事实覆盖分开评估。
- Civolution 终局最后一格奖励：通用研究牌/终局计分排在专用槽位前。
- Agricola 缺粮乞讨、两种烤炉：乞讨标记或具体烤炉可能本来就能回答；下一版标注应增加确切事实需求与允许的回答路径，当前结果保留原始严格口径。

优先工作是补“需要哪些事实”的标注、校准无答案/歧义处理，以及核查实际回答。当前没有证明所有游戏、所有 FAQ、所有卡牌都准确。

## 复现与本地记录

```powershell
python tools/indexing/retrieval_lab.py --help
python tools/indexing/eval_natural_retrieval.py --api http://localhost:5000 --out .tmp/live-natural
python tools/indexing/eval_retrieval.py --gold tools/qa/retrieval_gold.jsonl --api http://localhost:5000
python tools/indexing/eval_retrieval.py --gold tools/qa/retrieval_regressions.jsonl --api http://localhost:5000
python tools/indexing/rebuild_index.py --all --pooling cls --no-switch
```

实验工具只读取本地模型，不隐式下载或修改 alias。cross encoder 使用 [BAAI 官方模型](https://huggingface.co/BAAI/bge-reranker-base)，本地权重 revision 为 `2cfc18c9415c912f9d8155881c133215df768a70`。ONNX SHA256：`06c4bd9d00f16bdccacd4a838fb74e31f0ba22dafa2472e1ac1ce1266e7ce13d`；tokenizer SHA256：`7dfbf1966ebf99d471c3796e9b457329d2b2182b817e144f1e904b957745c839`；reranker SHA256：`ced967c45fd1902eb92716c9ceeca7c95a936770ea9db611f5a841b926e33fbd`。

完整排名和投影：`.tmp/retrieval-lab-verified-20261009/`；API、索引切换、ONNX 双端一致性、实际聊天记录：`.tmp/retrieval-cls-rollout-20261009/`。这些是本机诊断产物，不进入 Git；模型和密钥也不进入 Git。

跨主机迁移：先用目标配置构建/核查 CLS 索引，再启用同配置的 API；不要让旧 mean 服务与 CLS alias 混用。回滚须同时设置 `Embedding__Pooling=mean`，并恢复或重建对应 mean alias；仅改变一边会被 profile 检查拒绝。旧 mean alias 映射保存在本机 `aliases-before.json`；规则内容发生修正后，应重建当前内容的 mean 索引，以免恢复过时内容。
