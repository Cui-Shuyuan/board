---
name: runtime-architecture
description: BoardAI.Api 后端 Runtime 的具体架构、接口设计与关键实现决策
metadata:
  type: project
---

# Runtime 架构

## 服务位置
`D:\workspace\board\backend\BoardAI.Api/`，.NET 9 Web API，默认监听 `http://localhost:5000`。

## 核心组件

| 文件 | 职责 |
|---|---|
| `Controllers/ChatController.cs` | `POST /api/chat` 入口 |
| `Controllers/RulesController.cs` | 规则查询 HTTP 入口 |
| `Services/ChatOrchestratorService.cs` | ReAct 式工具循环，对接 LLM 与规则服务 |
| `Services/GameRulesService.cs` | 读取 `ontology/concepts.json`、`games/{game}/concepts.json`、`games/{game}/flow.json`、`games/{game}/instances.json` |
| `Services/ILLMService.cs` / `DeepSeekLLMService.cs` | LLM 抽象与 DeepSeek v4 Pro 实现 |
| `Models/ChatMessages.cs` | OpenAI 兼容的消息/工具/响应模型 |

## 接口设计

### 对话接口
`POST /api/chat`

请求：
```json
{
  "game_id": "splendor",
  "messages": [
    { "role": "user", "content": "贵族该怎么获得？" },
    { "role": "assistant", "content": "..." },
    { "role": "user", "content": "游戏怎么结束？" }
  ]
}
```

响应：
```json
{ "reply": "有人声望分达到十五分时..." }
```

**无状态**：历史由前端维护，后端只在请求到达时把 `messages` 拼到 system prompt 后面。这样客人退出再进入，只要前端把历史带回来，上下文就不会丢。

### 规则查询接口
- `GET /api/rules/games`
- `GET /api/rules/games/{game}/types`
- `GET /api/rules/games/{game}/concepts?type=objects|actions|triggers|conditions|top_level_refs|flow|ontology`
- `GET /api/rules/games/{game}/concepts/{id}` → 返回所有匹配的 concept 数组；支持 `ontology::concept_id` 限定只查 ontology，不带 namespace 时同时查当前游戏与 ontology
- `GET /api/rules/games/{game}/actions/{actionId}/conditions`
- `GET /api/rules/games/{game}/search?q=...` → 支持 `ontology::concept_id` 限定搜索范围

`{game}` 对应 `games/` 下的目录名。新增游戏只需新建目录放 JSON，代码不改动。

## 关键决策

### 1. 无状态优于有状态
早期实现过 `session_id` + 服务端内存保存历史，但会带来两个问题：
- 前端退出再进入，session 丢失，历史就没了
- 服务重启，所有会话清空

改为前端传完整 `messages` 后，服务端更简单、更可靠，也更贴合 OpenAI/DeepSeek 的原生 API 形态。

### 2. Prompt 配置驱动
`LLMOptions.cs` 中默认 prompt 为空，`appsettings.json` 中的 `LLM:SystemPrompt` 是唯一来源。system prompt 支持 `{game_name}` 占位符，运行时被替换为实际 `game_id`。

好处：
- 调 prompt 不用重新编译
- 游戏名动态注入，prompt 本身保持通用

### 3. 工具集合最小化
当前暴露 4 个工具给 LLM（具体 schema 由代码在每次请求时随 `tools` 参数传入，不在 system prompt 中重复描述）：
- `search_concepts(game_id, query)` — 语义搜索，返回带元数据的 SearchConceptsResult（含 count/strategy/note，告知 LLM 这是 Top-K 非穷举）
- `get_concept(game_id, concept_id)`
- `get_action_conditions(game_id, action_id)`
- `list_concept_ids(game_id)` — **2026-07-26 新增**：穷举全量概念 ID+名称，按类型分组，极轻量。LLM 需要确认某概念不存在或浏览全量目录时用，替代反复换关键词搜索

所有工具的第一个参数都是 `game_id`，保证多游戏场景下不会查错数据。

### 4. 顶层引用动态检测
`GameRulesService` 不再硬编码 `"<hand>"`、`"<player_holding>"` 等 Splendor 专属顶层键，而是从 `games/{game}/concepts.json` 的顶层对象属性中自动检测。不同游戏可以有不同顶层引用。

### 5. 搜索质量优化：向量检索
`search_concepts` 已升级为**向量优先 + 关键词降级**双路由：
- 向量检索：BGE-small-zh ONNX 模型（512 维）→ Qdrant 余弦相似度 → top-10
- 关键词降级：原有的分词 + 子串匹配（向量不可用或无结果时自动切换）

Qdrant 按游戏分 collection（`board_{gameId}`），索引涵盖 ontology、concepts.json（objects/actions/triggers/conditions/top_level_refs）、flow.json（递归展开全流程树）。

向量索引通过 Python 脚本 `scripts/rebuild_index.py` 独立管理，不依赖 .NET。启动服务时不再重建索引（已持久化在 Qdrant 磁盘），改规则后需手动跑脚本。

详见 [[vector-search]]。

### 6. Namespace 支持
为避免 ontology 概念与游戏自定义概念重名（如 Civolution 中游戏自有的 `activity` 与 ontology 的 `action`），规则查询支持 `ontology::concept_id` 前缀：
- `get_concept(game_id, "ontology::resource")` 只返回 ontology 中的 `resource`
- `get_concept(game_id, "resource")` 同时搜索当前游戏与 ontology，返回所有匹配结果数组
- `search_concepts` 同样支持 `ontology::...` 限定范围

工具描述已更新，提示 LLM 可以按需带 namespace 查询。

### 7. 日志策略
控制台输出：
- 每轮 LLM 的 `reasoning_content`
- 每轮调用的工具名与参数
- 每个工具的返回结果（JSON 已美化、中文不转义）
- 最终回答

便于在命令行调试时观察 LLM 的查询链。

### 8. 回答风格约束
通过 system prompt 约束：
- 简洁，一到两句话
- 汉字数字
- 无 markdown
- 无填充语气词
- 不用本体术语（"发展区" → "自己面前"）
- TTS 友好（不用加号、引号、括号）
- 查到足够信息就停止，不要过度查询
- 工具返回中的 `<concept_id>` 引用可继续查

System prompt 中不再重复列出可用工具（工具 schema 已通过 `tools` 参数单独传递），以减轻 prompt 负担。

### 9. 搜索元数据与工具选择策略（2026-07-26）
解决 LLM 不信任部分搜索结果、反复换关键词查全量的循环问题：
- `search_concepts` 返回 `SearchConceptsResult` 包装，包含 `count`、`strategy`（"hybrid_vector_keyword"）、`note`（"NOT exhaustive"）
- 新增 `list_concept_ids` 工具让 LLM 一次性看全量 ID+名称，替代重复搜索
- System prompt 中明确工具选择决策：search_concepts 找入口 → get_concept 跟引用 → list_concept_ids 仅兜底穷举

### 10. 搜索合并策略升级：MAX → SUM + 归一化（2026-07-27）

修复多词查询时单通道高分概念挤掉全通道匹配概念的问题：
- **旧逻辑（MAX）**：同一概念取所有通道最高分 → OR 语义，匹配一个词就能排前面
- **新逻辑（SUM + 归一化）**：同一概念累加所有通道分数，再除以子词数量 → AND 语义，匹配词越多得分越高
- 效果：查询「黄色 六角形 小」时，`phase_indicator`（三词全中）归一化分远高于 `attribute_chip`（只中两词）

### 11. 概念引用注解：`<id>(中文名)` ★（2026-08-10）

**问题**：LLM 拿到含 `<concept_id>` 引用的工具返回后不跟引用查中文名，自行翻译英文 id 产生错误译名（`idea_marker`→「灵感/想法标记」、`focus_marker`→「专注标记」、`hill_territory`→「寒冷」）。温度不可调，改 prompt 像抽奖——改为**数据层解决**：程序把引用注解为 `<id>(中文名)`，LLM 无需翻译。

**实现**：
- `GameRulesService.AnnotateReferences(text, game)`：正则 `<([A-Za-z_][A-Za-z0-9_]*(?:::[A-Za-z_][A-Za-z0-9_]*)?)>` 替换为 `<raw>(name)`；查不到映射（枚举值、未知 id）保持原样
- `GetNameMap(game)`：id → name.zh 映射，覆盖全部概念（ontology + 游戏层 + top_level_refs + instances + **flow 递归节点**），缓存于 `_nameMaps`
- `ChatOrchestratorService.ExecuteToolAsync`：4 个工具返回统一过 `AnnotateReferences`
- **关键坑 ★**：`JsonSerializer.Serialize` 默认把 `<` `>` 转义为 `<` `>`——正则永远匹配不到！工具返回必须用 `ToolResultOptions`（`JavaScriptEncoder.UnsafeRelaxedJsonEscaping`）序列化
- 效果：317 个注解/题组，4 题回归全对（睡眠模组/神秘橡树/激活模组/恩惠检定）

### 12. flow 关键词搜索补 triggers ★（2026-08-10）

`ExtractFlowConcepts` / `ExtractFlowItems` 原来只遍历 flow.json 的 `procedures` 顶层——**triggers 组概念（favor_test 等）完全不在关键词搜索范围**（向量搜索又因 BGE 对中英混合长文本质量差不可靠——「恩惠检定」Top 10 全是 0.78-0.83 噪音）。已改为递归遍历 procedures + triggers + 嵌套 options/events/容器字段。**注意：C# 与 Python 索引路径必须保持一致**（Python rebuild_index.py 一直处理 triggers；C# 补上了）。

### 13. 搜索分数透明化：TermScores ★（2026-08-13）

`search_concepts` 结果中每个概念新增逐词双通道分数，帮 LLM 判断「哪个词匹配弱、要不要换词再搜」：

- **`TermScores`**：dict<切分词, {Vector, Keyword}>——query 按标点/空格切分后，每个子句独立跑向量+关键词两通道，合并时按子句分通道记账（`AccumulatedSearch`）。某个词在所有结果上都很低 = 该说法在规则中没有对应表述
- **`FullQueryScore`**：整句查询的双通道分（多词时存在；单词查询为 null——整句即该词）。整句也参与总分，补上它 LLM 才能与 `Score` 对账
- **`SplitTerms`**：切词回显，让 LLM 看见 query 被切成了什么（中文只按标点/空格切）
- 排名算法零改动（总分 = Σ各子句两通道分 ÷ 子词数）；分数 MathF.Round 保留 2 位小数省 token
- system prompt 查询策略新增一条：TermScores 低 → 换说法；总分接近 → 看各命中哪些词

### 14. get_concept 一层引用扩展 ★（2026-08-13）

`get_concept` 返回从裸数组改为 `{Matched, Related, Note}` 包装——程序自动把 matched 概念**直接引用**的概念定义一并返回（一层，related 内部引用不再递归扩展，只注解中文名）：

- **实现**：`GetConceptsWithExpansion`（GameRulesService）——matched 元素用 `RelaxedJsonOptions`（UnsafeRelaxedJsonEscaping）序列化后跑 `ConceptRefRegex`（与注解共用的静态正则）提取引用；`GetConcepts` 能解析的才扩展；排除查询概念自身与 matched 集合（按 id）
- **去重**：按解析结果的 concept id 去重（`<score_track>` 与 `<ontology::score_track>` 可能解析到同一概念）+ 按引用串去重（seen）
- **硬上限 MaxRelatedConcepts = 10**（首现顺序）：QA 实测无 cap 时 get_concept("game") 扩展出 128 个 related（435KB）；cap 后 related ≤ ~35KB。截断时 Note 追加提示。**教训**：一层扩展必须带数量 cap，顶层聚合概念（game/round）的引用集可以非常大
- **坑 ★**：引用提取的序列化必须用 UnsafeRelaxedJsonEscaping——默认 JsonSerializer 把 `<` `>` 转义为 `<>`，正则永远匹配不到（与工具返回的 ToolResultOptions 同一坑，第二次踩）
- **payload 观察**（52 题 QA）：get_concept avg 31KB / max 158KB——max 的 131KB 是 `game` 概念自身定义（顶层 round 内联全流程），非扩展所致；search_concepts avg 8KB（TermScores 增加 ~2KB）
- 工具描述与 system prompt 已同步（「get_concept 看详情（含一层引用）」）；`Related` 的概念被注解（引用带中文名），LLM 要更深层时用具体 ID 继续查

### 16. BGE 窄锥发现与搜索路由定稿 ★★★（2026-08-13 深夜，用户「向量检索是不是我的玩法」之问）

**核心发现——窄锥噪声带**：BGE-small-zh（句模型）的 L2 归一化向量挤在窄锥里，**无关文本对的余弦相似度基线是 0.74-0.82 而非 0**（实测：白色 vs 天气温暖 0.757、香蕉 vs 翻面板块 0.788、飞机 vs 放置船只 0.822；语料均值向量模长 0.8963——90% 能量是公共分量）。后果：短词（≤2 字）查询对全文集合的相似度排序**近似随机**（「白色」top-16 无一个相关概念，天气格 warm 排第 6），Qdrant 阈值 0.55 形同虚设。

**中心化实验**：减语料均值后地板确实被打掉（0.80→0.30 以下），但短词排名仍是噪声——短查询残差本身无内容信号（模型是句对句训练的，两三字不在分布里）。

**结论与路由定稿**：向量检索是对的玩法，但「短词 vs 全文」是键位错误（用户计算器类比：1+1 该按加法键）。正确对局：
- **短词（≤2 字）→ 名字集合**（短文本对短文本，查询模板分量相消，排序有意义：「骰子」dice 精确名 0.91 升第一、「模组」module 升第一）
- **长词/整句 → 全文集合**（语义合成有信号：「白色 骰子」activation_die 第一）
- **关键词通道始终兜底**（词面精确匹配 1.0/0.95/0.90 档位）
- 实现：`VectorChannelAsync` 按 `q.Length <= 2` 选择 effectiveMode；topK 5→15；批次内按 concept_id 去重取最高分（**public_board 同 id 存在于 ontology 与游戏层，重复计分产生 1.62 超范围幻影分**——这是真正的 bug，已修）
- 遗留边界：「怎么狩猎」类泛用词整句（「怎么」压过内容信号）仍噪声——LLM 换短词「狩猎」即恢复（hunt #1）

**复测基线（2026-08-13 深夜）**：白色 骰子→activation_die 2.17 #1；骰子/模组/狩猎/激活骰/命运骰/恩惠检定→精确名概念全部 #1；白色→真含该词的概念平列 0.9。QA 全量回归未跑（下一轮）。

**QA 产物不入库**：scripts/_qa_results.json 从 git 移除（.gitignore 已有，91d0e3c 执行 rm --cached）。

**引用化全面推广（2026-08-14，ad93074）**：用户定稿「把 civolution 所有概念的 description 都过一遍，应替换为引用的自然语言都替换掉」——逐概念人工审查三文件约 180 处，政策：阶段→<phase_X>、动作动词→动作概念（狩猎→<hunt>）、材料产地→<forest_territory>（terrain_forest 去留待议）、睡眠模组→<module_sleep>、模组板块→<module>的<ontology::tile>、白/粉描述语去除、自身名/定义保留字面、parts 路径格式、槽位整体尖括号（<weather_gauge.warm>）。**副作用**：W05 孤立警告 28→11（引用化让材料等概念获得引用）。抽查：骰子/模组/狩猎/部落/白色骰子全部 #1。

**19. identify 关系 + appearance 机制 + 格式规范化（2026-08-14 深夜）**：

- **identify 关系（9008635）**：用户实测「六边形的黄色标记是干嘛的」被误答进程轨——单工具架构退役 search_concepts 后「外观描述→概念」通道断裂（list 只有名字，颜色/形状在描述里）。新增 relation=identify（entity=描述原文，内部复用退役的搜索设施返回带定义候选）+ prompt 两条（外观描述用 identify、描述与概念矛盾不硬答）。复测：第一轮即正确识别阶段标记
- **appearance 字段机制（830b83f，用户定稿）**：appearance 不限于 component——**一对一实体概念直接持有**（卡牌=纸片+属性集合，身心合一），只有一对多/多对一才拆（八角柱）；抽象概念（分数/流程/效果）不允许。落地：ontology token 补 appearance optional；resource extends 修正为 <token>（此前数据仍是 <object>，与「Token 是 Resource/Marker 物理基类」定稿不符）；校验器 **E16**（抽象概念填 appearance 数据→ERROR，按继承链判物理性）+ **W07**（实体概念缺 appearance→WARN，自动生成填充工作单，当前 81 项）
- **格式规范化（46e1a84，用户要求）**：normalize_json.py 一键规范化（UTF-8 无 BOM、LF、2 空格缩进、末尾换行、去行尾空白），8 文件处理、内容与 HEAD 逐字段比对一致；validate_rules **E01c**（BOM/制表符→ERROR）；.gitattributes（json/md/py eol=lf）。**教训**：BOM 曾致校验器 try/except 静默跳过 ontology 收集（2661 个假错误），E01c 堵住
- **待办清单已入任务列表**：appearance 填充两批（描述提取 + 需规则书核实清单）、W06 六处重名清理、全量 QA 回归（identify 场景入常驻题）

**20. appearance 批量填充完成（2026-08-14，14c0c9f）**：W07 81→1。80 个实体概念的 appearance 全部填充——civolution concepts 56 项（八角柱/圆片表示组写「由 <octagonal_pillar>/<stackable_disc> 表示 + 所在格」、18 种材料、芯片、卡牌、板块、版图、辅助物）+ instances 20 项（15 模组板块——骰子点数从 module cost 数据驱动提取、4 地点实例带分数、theocracy 三段布局）+ splendor 4 项（三级发展卡牌面布局、声望点数「非独立零件，印于卡牌左上角」）。money 顺带补 `component: <octagonal_pillar>`（与 food 等对齐）。**格式：civolution 纯中文串、splendor {zh,en}，与各文件既有风格一致**。**starting_monolith 已由用户提供外观并补填（2026-08-14，2c569f3）**：「由一大一小两块纸板互相垂直拼接而成，能立起来。大的那块是碑体，下宽上窄、形似纪念碑，上面印有阿格拉本人的形象；小的那块是碑座。共 1 个。」——**W07 清零**（0 错误、11 警告 = 6 W06 + 5 W05）。hunting_token（缺形状）、continent_tile（缺拼块形状）已填但形状待用户补充。**教训**：行级批量插入 JSON 字段时——description 是节点最后字段则闭合行 `}` 无逗号，插入后新字段逗号规则要按「插入后谁是最后字段」重算，且 extra 字段的逗号取决于原文件后续是否还有字段（money 的 unit 就是坑）

**17. 思考模式实验 + 查询计划架构（2026-08-14）**：

- **思考模式三轮 QA**（用户工程直觉：「桌游讲规是有限空间，LLM 应只做接入层」）：思考开启 7.3s/3 慢题；思考关闭 5.1s/0 慢题但出现套话；**thinking=low 4.8s/0 慢题/风格最干净**——定为默认。`LLM:Thinking` 配置（default/disabled/low），DeepSeek V4 `thinking.type=disabled` / `reasoning_effort=low` 透传
- **架构定稿：LLM 只给查询计划，程序执行**。用户澄清核心直觉：「问题的原子是关系不是问题本身」——本体定义了封闭关系词汇表（~10 种），所有流程问题都是关系的短组合；穷尽性靠「挖掘→分类→函数化→兜底收集」循环收敛，跨游戏零新函数是完备性证明
- **路由问题的解法：不让 LLM 选工具**——LLM 产出查询计划（数据），程序三道闸验证（实体解析/结果非空/覆盖检查），不过闸自动回退旧工具
- **FAQ 数据集**：games/civolution/faq.md（Grok 抓 BGG 真实提问 ~65 条，faq.md 被 gitignore 旧规则挡住未入库）+ faq-annotated.md（50 道多人题标注）。**频率：explain 52% / condition 12% / quantity 10% / ordering 8% / boundary 8% / timing 4% / substitution 4% / meta 2%**
- **FAQ 反校验形式化的五条结论**（用户澄清）：① gain_income_chip 四步已齐（FAQ 混淆获得/安装）② 驱赶规则我们正确（B 保持状态、A 虚弱）③ 升级时机语义已在 effect 模型中（FAQ 存放机制=工程妥协，理想系统不需要）④ 卡牌类型性质属实例层非真实问题 ⑤ Reset 次数 FAQ 公式错误——**派生事实不入库**，由 ≤3 骰条件+红格最终轮推导
- **P0 execute_plan（36144c5）**：relation=explain；实体解析三档（精确 id → 精确中文名反查 → 名称包含候选消歧）；概念闭包（matched+related+flow 祖先链 zh 名）；prompt 计划优先。**QA：使用率 74%（39/53）、工具轮数「多为 2 轮」→「多为 1 轮」、4.9s 持平、质量零回退**。下一步按频率做 condition/quantity/ordering/boundary

**18. 单工具架构定稿（2026-08-14 深夜，用户洗澡期间自主完成，4a6592b）**：

- **用户定稿架构**：「LLM 只管给计划，它都不知道问题是怎么查出来的，答案摆在它面前——它一个工具都没有了，只有计划」
- **关系函数全量**：explain / condition（条件+费用+目标三要素）/ **ordering**（flow 位置索引：同级 options 顺序+下标+最近 loop，答「X 之后是什么/何时结束」）/ **boundary**（溢出/下溢/圈事件/容量字段+缺省语义）/ flow（game 概念）/ list（全量目录）——search_concepts/get_concept/get_game_flow/list_concept_ids/get_action_conditions 全部退役为 ExecutePlan 内部能力
- **单工具生效验证**：扩展 QA 81+ 题平均 4.7s、零慢题、**零非计划调用**（此前 LLM 会幻觉 search_concepts，prompt 声明「你没有其他工具」后消失）、口语题 4.3s
- **校验脚本升级（用户定稿：JSON 是程序运行前提，校验=编译器）**：E14 独立概念 id 重复检测（按 id 定位破坏确定性）+ W06 中文名重复检测（精确名解析歧义）。E14 抓到 4 处真问题（take_from_display ×3 等）全部按语境改名。现 0 错误 11 警告（6 W06 重名+5 W05 孤立，均为可接受信号）
- **扩展 QA 脚本**：scripts/_qa_extended.py（原 52 + 31 道口语化/STT 模拟：口癖填充、能不能、先后、边界上限、闲聊）
- **教训**：prompt 重写用 Python 正则替换曾静默失败（regex 未匹配但 json 校验通过旧内容）——重写后必须用内容签名当场验证（'你只有一个工具' in prompt）

### 15. 索引去噪三件套 + get_game_flow ★（2026-08-13 晚）

短查询（「激活骰」）排名异常的调查引出三项索引层修复 + 一个新工具：

- **局部步骤退出索引**：判据从「有 id」收紧为「有 id 且有 specifies/extends/instance_of」——flow 278 个带 id 节点中 132 个 pipeline 局部步骤（_skip ×10、roll_die ×2、take_activation_die ×2 等）不进向量库/关键词/list 目录；get_concept 按 id 仍可查。Python extract_flow + C# WalkFlowNode/WalkFlowForSummaries 三处同步（WalkFlowForNames 与 TryFindFlowProcedure 不动）
- **`<>` 引用不参与相似度计算**：嵌入前剥离引用（Python `strip_refs` + C# `ConceptRefRegex.Replace`）——引用由 get_concept 注解/扩展导航，其字面不应污染概念自身语义向量。**这是用户一开始就有的直觉**：描述中的概念名词本应写引用而非汉字，LLM 初稿写了汉字没纠正，长文档靠引用字面蹭查询词造成短查询噪声。替换要**词义敏感**：激活骰子区→`<activation_dice_area>`（zone 不是 die）、主<module> 是错的（没有主可升级模组概念，就是 `<upgradable_module>`）、概念自身名/定义保留字面
- **game 通用容器退出索引**（各游戏共有的顶层流程宿主）；ontology flow 的 trigger_pipeline 四步同因退出（get_concept 本来就查不到它们，退出索引反而是好事——搜得到查不到会害 LLM 白跑）
- **get_game_flow 新工具**：流程类问题直达 game 概念整体流程（返回完整 game JSON，无扩展），不走搜索。LLM 实测正确选用（reasoning 明说 "use get_game_flow directly"），两轮完成 3.9s。system prompt 查询策略/工具决策列表同步
- **实测**（civolution 索引 515→443）：激活骰/命运骰字面引用化后两概念均回 #1；白色骰子 #3；「骰子」仍被 prepare_dice_pool 压（骰子字面未引用化，下一批候选：骰子/白骰/粉骰/材料/食物等概念名词）。**规律**：每个字面被引用化，对应噪声就消失——引用化是逐词推进的长期工程

### QA 回归（2026-08-13 晚，TermScores + 扩展上线后）

52 题平均 **7.6s**（基线 7.2s@51 题，基本持平）；慢题 3 道（[41]17.0s/[44]15.5s/[51]15.8s）均回答正确——[41][44] 是 LLM 沿机制链深挖的偶发方差（前一轮 8-9s），[51] 为已知跨游戏题（正确反问澄清）。工具调用画像：最多 3 工具轮 / 7 次调用（前一轮）或 4 轮 / 8 次调用——**基线时代的 9-10 轮螺旋彻底消失**，原慢题（活动模组/研究牌层级/时代计分）全部 < 12s。

## LLM 调用次数
`MaxToolRounds` 设为 `int.MaxValue`，不再限制 LLM 为一题调几次工具，方便观察复杂问题上的真实查询深度。实际生产时可根据成本和延迟再收紧。

## 前端流程建议
1. 启动时或选游戏前调 `GET /api/rules/games` 拿到游戏列表
2. 客人选定一款游戏，前端记住 `game_id`
3. 对话期间所有 `/api/chat` 请求都带同一个 `game_id`
4. 前端本地维护 `messages`，每次把完整历史发过去，收到回答后追加到本地历史

## 与 [[interaction-model]] 的关系

[[interaction-model]] 定义了 LLM 与程序的**分工边界**（什么交给 LLM、什么交给程序）。本文档定义了**具体实现方式**（HTTP 接口、消息格式、工具设计、无状态策略）。二者共同构成当前 Runtime 层。

**Why:** 把 Runtime 实现细节从概念文档中拆出来，避免 [[project-overview]] 和 [[splendor-progress]] 过于膨胀，也便于后续接新游戏或换前端时快速查阅。
**How to apply:** 新增游戏时按本文档接口接入；调整 prompt、工具或搜索逻辑时同步更新本文档与 [[splendor-progress]]。
