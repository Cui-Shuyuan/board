# FAQ 类型收敛与程序化检索工具（2026-08-16）

## 愿景（用户 2026-08-16 定调）

LLM 只负责两件事：**意图理解** + **答案语言组织**。LLM 知道的事实要尽可能少。
FAQ 收敛为几个类型，每个类型配一个专属确定性工具负责推理；LLM 只拿结果讲给客人听。
广播架构：LLM 输出一个请求 → 广播给所有方法 → 谁能处理谁处理 → 都处理不了 `get_concept` 兜底并返回概念让 LLM 自己推理。
原则：能确定的就不该交给 LLM（1+1 交给计算器）。

## FAQ 聚类结果（scripts/_faq_cluster.py，237 题 / 5 游戏）

模式聚类（multi-tag）：许可 41、方式 40、时序 20、数量 16、计分 15、强制 11、位置 10、
假设 9、定义 7、条件 6、其他 96。

## 用户裁决：description 不许拟合 FAQ（2026-08-16）

用户：「faq 也不一定都是对的……在 description 里拼命去加描述加限制，这属于过拟合，
好的程序设计不需要任何 faq 也能推理出正确答案。」

- 后续各游戏章节里列出的**描述类修复已全部撤销**（commit af73a36，14 处）：
  brass 3 处（同义表述/灯泡=1 级/例外句）、cob 3 处（免费行动×2/每次拿取粒度）、
  pr 2 处（可降至 0/码头必须装完）、civolution 6 处（Sustenance/突变 Errata/
  特征要求/Glossary/补满≠Reset/探索放标记）
- **保留**：检索层 aliases/正则、结构化 quantity.numeric/capacity.formula/score_table、
  income_chip 修正（规则书 p.23 第 4 步逐字支持，修正自相矛盾的错误数据）
- 今后 QA 答错只修两类：真实数据 bug（规则书支持）或检索层；FAQ 口径一律不进 description
- 教训：同一份 FAQ 既当修复来源又当验证集 = 复测通过只证明拟合了原题；
  规则书才是数据口径的唯一来源，其次用户拍板（见 [[user-preferences]]）

## 已完成的程序化工具（按落地顺序）

### 1. 名词直呼（请求级，commit 7b00e2d）
执行计划解析时精确查表：`exact_id` > `exact_name_zh/en/alias`（大小写不敏感）> `contain_unique` > `auto_semantic`。

### 2. 广播第二站：问题级直呼 + 基名/多值查表（commit d8eeda8）
execute_plan 现在把**客人问题原文**一并传给程序：
- 实体解析失败 → 程序直接扫问题原文做最长名字优先匹配（`question_hit` 拍板）——
  C 类题的全部来源就是 LLM 转述失败（「谷物播种」「开局食物」「农场空格」）
- 精确查表改多值映射 + 基名：括号注解剥除（「家庭成长（需空房间）」→「家庭成长」→
  两个成长行动一起返回）；flow 节点 zh 名入表；identify 也先做问题级直呼
- question_hit 结果 related 轻量化：不展开本体概念，只给游戏概念引用

## 3. 广播第三站：事实卡（用户 2026-08-16 拍板「方案甲：程序自己算分」）

用户定调：「算分倾向方案甲——桌游算分能复杂到哪去？绝对可以靠公式自己算出来。
计分表挂在 flow 下面，终局计分时在 pipeline 中使用。先做农场主看看效果。」

落地方式：问答含分数词/数量词时，`GameRulesService.ExtractFacts` 对命中的概念做程序提取：

- **计分表**挂 `flow.json` 的 `<final_scoring>` 块：`by_count`（带 rows min/max/vp、
  aliases、note）/ `per_unit`（vp×数量）/ `by_material`（房间木0/泥1/石2）/ `by_card`；
  表级 `aliases` 汇总词（动物→牛羊猪、作物→谷物蔬菜）
- **容量/成本公式**挂 concepts.json 的 `quantity`：`"formula": {"op": "*|+|^", "operands": [...]}`
  + `examples: [{"zh", "bindings": {var: n}}]`，程序 EvalFormula 算好答案连同例子一起给 LLM
- **分支数量**挂 `quantity.numeric`：`[{"when": {"zh": "起始玩家"}, "value": 2, ...}]`，
  按问题关键词选分支；只有选中「真子集」才求和（互斥替代项如 起始2/其他3 永不求和）
- 程序侧：`PlanItemResult.Facts`（可空不序列化）；分数词正则 `计分|得分|几分|多少分|...`、
  数量词正则 `几个|几只|上限|容量|...`；事实类型 score_rows/score_per_unit/
  score_by_material/score_by_card/score_table/capacity_formula/quantity_numeric；
  问题含单个整数时直接代入计算（如 1 个农场格 → -1 分）
- **计分表不进 LLM 上下文**：selected 概念 0 个或 >6 个时才发整表（score_table 兜底）

实测 8 道事实题全覆盖（Q15 牧场容量 2×格数×2^马厩=16、Q26 播种拿取 1+2=3、
Q34 农场空格 -1、Q35-36 计分行、Q37 家庭成员+3/房间材质分、Q38 开局食物 2/3 无求和）。

### 工具自愈（配套修复）

- 模型偶发把工具参数包成 `{"arguments": "<json 字符串>"}` OpenAI 外壳 → orchestrator 解包
- execute_plan 的 error 提示带完整 JSON 形状样例，避免模型出错后 29 轮死循环
- permanent_action_space 加英文别名 "After X, also Y"——Q2「之后算 X 吗」的问题直呼命中

## QA 基线（农场主 55 题，fp32 模型，2026-08-16 终版 commit d1a2d05）

**A53/C0/E2**（首版工具时 A43/C10/E2；E2 = 已知 BAD 题 48 误读问题 / 53 起始玩家轮换——
有数据仍答错，是 LLM 推理侧问题而非检索侧）。ok 结果程序拍板率 **98%**：
question_hit 48% / exact_name_zh 22% / exact_alias 9% / exact_id 5% /
contain_unique 5% / auto_semantic 2% / exact_base 2% / exact_name_en 1% / llm_picked 2%。

**复测发现的波动与修复**（三轮 QA，每轮各有 1 道 C 类换题——LLM 措辞方差，修一道稳一道）：
- Q2：permanent_action_space 加英文别名 "After X, also Y" → 问题直呼命中
- Q32：模型把工具参数包成 `{"arguments": "<json 字符串>"}` 外壳导致 29 轮死循环 → orchestrator 解包自愈
- Q3（「只为阻挡不执行行动」）：模型判定问题含糊、未调工具直接反问客人 →
  系统提示词强化：含糊问题也必须先查规则，拿到程序结果后仍缺现场状态才能反问
- Q24（「木屋时能直接建泥/石房间吗」）：house 加「木屋/泥屋/石屋」别名 → 问题直呼命中

**坑**：
- 分析脚本必须在 QA 日志完全落盘后再跑（曾因读半截文件把 Q47 误判为 C）
- API 就绪探测要用 `/api/rules/games`（不是 /api/games，会一直 404 超时）
- 事实卡 JSON 日志里键名是小写 `"kind"`（序列化 naming policy），grep 时注意
- **并行多游戏 QA 日志交错**：请求头（`[Chat] game: ...`）与轮次行（`[Chat] Game X, Round ...`）
  在各自 LLM 延迟后交错写入，且无时间戳的续行占绝大多数（推理/JSON 多行内容）。
  分析脚本切分必须**按行归属**：带标签的行自带游戏 id，续行继承上一行的归属；
  每游戏独立记当前请求的问题文本。按「下一个请求头」切连续区间会吞入相邻游戏的轮次行。

## 事实卡推广（2026-08-16）

### Brass: Birmingham（commit bab6a3b）QA 复测 50/50 全对，A48/C2/E0

- 数量事实：loan 收入后退 3 级 / 贷款拿 £30、scout 万能地点卡+万能产业卡各 1 张 →
  quantity.numeric（Q22、Q38 事实卡命中）
- 检索强化：network 描述并入「板上无板块/链接时可在任意地点建 link」例外；
  no_tiles_on_board 别名「没有任何存在/无 link 无建筑」→ Q3 question_hit 命中
- Q19（能 Develop 1 级陶器吗）数据修复后由 D 转 A
- C2 = Q4（煤矿/铁厂立方体之后能否再卖）/ Q16（覆盖建造条件），凭记忆答对，可后续再做
- brass 无需 score_table：终局计分靠 canal_scoring/rail_scoring/final_scoring 概念直接检索

### Castles of Burgundy（commit 44922bc）QA 52/52 全对（3 道口径题修复后定向复测通过）

- score_table 挂 flow `<final_scoring>`：per_unit 货物/银币每枚 1 分、工人 divisor 2（每 2 个 1 分）；
  by_card 知识瓷砖 rule=放置才计分+终局计分瓷砖分值摘要；note=存储区剩余不计分
- 检索缺口修复（Q45「黄色瓷砖 #15 及以后」曾答错反问）：monastery_tile_1..15 注册「#N」别名
  （question_hit 最长名优先，「#13」盖「#1」不冲突）、tile_15 描述补「16-23 建筑计分/24-26」
  家族说明、key_space 注册「存储区」别名（Q33「存储区剩余瓷砖计分」→ 命中 key_space →
  计分表事实带 note 存储区不计分）
- 口径题修复（重跑发现 3 道 LLM 措辞偏差，数据修复后定向复测全过）：
  Q50「阶段奖励顺序」答成放置效果顺序 → phase_end 加「阶段奖励」别名（节点已有
  先矿银币后黄色效果描述，缺的是实体解析）；Q15「免费行动」→ black_depot 与
  buy_from_black_depot 描述注明「免费=免骰子，不是免银币」（先写「并非免费」反而
  强化错误口径，须从 FAQ 立场定义术语）；Q5「一次弃两块」→ discard_stored_tile 注明
  「弃置按每次拿取判定：一次至多 1 块，两颗骰子两次拿取合计至多 2 块」
- 教训：多值别名（如给 26 块瓷砖共享「黄色瓷砖」）会被 question_hit 的 MaxQuestionHits=4
  截断为前 4 个错误概念——宁可给每块注册唯一的「#N」键，命中即拍板单个正确概念；
  过泛别名（monastery_tile 别名「瓷砖」）会劫持六边形瓷砖类问题的实体解析，宁可锚定
  语义正确的概念（存储区→key_space）
- 术语口径类答案矛盾（免费/一次等）不是检索缺口，是数据措辞没给 LLM 立场——
  修复方式是在概念描述里按 FAQ 的术语定义写，而不是按成本视角写

### Puerto Rico 1897（commit 702d394）QA 55 题 A50/C2/E3，E3 修数据后复测全对

- score_table 挂 flow `<final_scoring>`：by_card 建筑（印刷 VP 无论占据全计）、
  大建筑加成（fire_station/residence/fortress/customs_house/city_hall 规则）；
  别名「终局计分」→ 两卡全发；note 平局钱币+货物
- 数量事实：harbor 装货+1VP、estate_display 容量公式 players+1（含 3/4/5 人局例）、
  planter_role 刷新抽牌 numeric（三人4/四人5/五人6）、vp_supply 筹码按人数、
  give_starting_coins 起始钱币 numeric（三人2/四人3/五人4）
- E3 修复（数据正确但 LLM 措辞错，均修描述/别名后复测通过）：
  Q38 最低成本→ building.cost 注明「只有折扣上限、无最低费用限制，可降至 0（免费建例）」；
  Q47 码头必须装完全部→ wharf 描述写「用不用码头可选；一旦用必须装完该类型全部桶」；
  Q54 留存结算→ storage_cleanup 节点加别名 剩余货物/保留货物/保存货物/留一桶
- 分数词正则补 `VP`、数量词正则补 `几张`（原正则漏配 FAQ 措辞，本轮 quantity 事实 0 触发）
- C2 = Q6/Q16 凭记忆答对；Q23/Q36/Q45/Q52 为经典版术语（庄园/大学/探矿者/收容所）
  与 1897 版（estate/学校/冒险家/医院）的映射题，模型如实说明并给出正确实质答案，可用

### 坑（PR 轮新增）

- when.zh 别写带数字+空格的「3 人」——BranchMatchesQuestion 按「、，, /空格」切段，
  「人」会命中全部人数分支并错误求和；用「三人/四人/五人」
- FAQ 的**翻译差异**题（经典版名 vs 1897 版名）模型会先声明数据里没有该名再按对应物答——
  只要实质正确即算可用（C 类），不必为旧译名注册别名
- 事实卡验证要在日志里 grep 小写 `"kind": "quantity_numeric"` 等；quantity 事实
  0 触发往往不是 bug 而是 FAQ 措辞不在数量词正则里——先查正则再查数据

### Civolution（commit 927a5ec）QA 50 题 A40/E10，E10 修数据后复测全对

- score_table 挂 flow `<final_scoring>`：by_card scoring_tile（时代/终局按板块类别计分）/
  statue（雕像数 × favor_of_ager_track 位置分数，每时代收入阶段 + 终局前一次）/
  storage_area（繁荣钻石：食物每3枚1钻、钱币每2枚1钻、定居点凹槽、仓库区列上下均存 1+2）；
  别名「终局计分」；note 11 格推进 + 平局先比模组升级数再比座位 + 100 分标记
- 数量事实：draft_starting_marker_cards 3 张选 1 / draft_starting_research_cards 每种 2 张 /
  reset_space capacity 公式 players×2+1（2/3/4 人局 5/7/9 格）
- E10 修复（全是 Errata/Glossary 口径与数据建模不一致，不是 LLM 记忆错误）：
  Q14 正面牌堆空了→ face_up_research_stack 加「正面牌堆」别名（原来答成收入芯片展示区）；
  Q41 收入芯片→ concepts 描述「安装不触发」是旧口径，flow 的 gain_income_chip 本就
  「安装→立即触发」→ 改 concepts 与 flow 一致（FAQ 口径）；Q15/Q44 突变牌→ research_card
  描述补 Errata 特例（费用格仅特征要求、即时奖励不给进度轨步数、Favor 除外）；
  Q37 探索→ explore_site 补「翻出资源图标立即放对应特征标记」；Q22→ tribe 补「喂饱只保命，
  变强需 Sustenance 模组」；Q36 钻石→ 模型只答仓库区漏食物/钱币列与凹槽，score_table 整表
  已含全部，复测后答全（属 LLM 选择性转述，非数据缺口）；Q3→ rulebook 补「Glossary 独立文件」；
  Q7→ equip_reset_columns 注明「补标记≠免费 Reset 行动，骰子时代间不自动清空」；
  Q39→ attribute_chip 补特征要求（持有 3–4 个对应特征标记归还 1 个）
- 事实卡触发验证：quantity_numeric ×3、capacity_formula ×1、score_table ×4、score_by_card ×21
- FAQ 解析坑：civolution FAQ 的 36-49 组编号独占一行（`36.  `），parser 的条目边界正则
  `^\d+\.\s` 对 strip 后的「36.」不匹配 → 前一条目 j 循环吞掉后续所有条目、zh/ans 被末条
  覆盖（35 题悄悄变 1 题错内容）。边界正则要写 `^\d+\.(\s|$)`，并支持「编号行 + 下一行原文」格式

## 候选下一类

- 数量问答余量——其他游戏其他数量题可继续结构化 numeric
- 别名表继续按 FAQ 措辞扩充
- cob C 类残留（Q24/25/33/41/42 等）复测确认
- civolution 数据缺口：cave/holy_rock 等地点概念、Sustenance 模组效果、各张研究牌/事件牌 per-card 内容

相关：[[vector-search]] [[runtime-architecture]] [[user-preferences]]
