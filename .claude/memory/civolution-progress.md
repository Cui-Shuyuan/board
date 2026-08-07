---
name: civolution-progress
description: 第二款游戏《文明演化》规则形式化的进度、评估与待办阻塞项
metadata:
  type: project
  originSessionId: fbc4a732-5c5a-482a-a184-077fdaab9f56
---

# 文明演化（Civolution）规则形式化进度

## 基本信息

- **目录**: `games/civolution/`
- **当前文件**: `concepts.json`（Phase A 对象清单层骨架）、`口播稿.md`、`Civolution_Rules_US_web_v1_0.txt`
- **权威规则书**: `Civolution_Rules_US_web_v1_0.pdf`（英文规则书，已提取为同目录 `.txt`）
- **复杂度**: 远高于璀璨宝石，预计 `concepts.json` 体量是 Splendor 的 3~5 倍
- **当前状态**: Phase A 进行中。进程版图与流程版图已 review 完成；模块升级模型已重构为 Lose + Gain（15 主模块拆为 45 个 effect 实例 + 15 个 tile，console.content 已扩展至 16 项）。2026-07-25：terrain/region 从 ontology 移除，7 种地形改为游戏层 `<ontology::zone>` 子类，territory 保留为游戏层 zone 概念。2026-07-26：`territory_token` 删除（本质即 hunting_token）；`site_slot` 修正为 24 格（非 25）；`site` 概念与 `building_slot` 边界澄清（site_slot 是分轨与大陆间的空位，building_slot 是建造点 site 自带的建造格）。对象总数：168。

## 为什么选这款游戏

- 规则极其复杂、卡牌/地点众多
- 游戏较新，LLM 不联网时对它一无所知
- 对系统、LLM 和形式化工作本身都是真实压力测试

## 已确认的关键决策

1. **允许扩展 ontology**: 为支持文明演化机制，需要新增骰子、轨道、升级、地点/区域、替代费用、被动效果等概念。
2. **完整规则，但分阶段迭代**: 不一次性生成完整 JSON，按 Phase A~E 逐步推进。
3. **多模态素材**: `pdftoppm` 已安装，可将 PDF 转成图片用 `Read` 工具分析；已有卡牌图片（`card/神权制.jpg`）验证过 piece 表结构。
4. **牌表与地点表是必要的**: 研究牌、事件牌、地点牌需整理成结构化表后，再转进 `concepts.json`。
5. **规则来源优先级**: 英文 PDF 规则书 > `.txt` 提取文本 > 口播稿.md。口播稿用于理解讲解重点，规则书用于确认精确数值与流程。
6. **namespace 方案已确认**: ontology 概念引用统一使用 `<ontology::concept_id>` 格式（如 `<ontology::resource>`），游戏自定义概念保持 `<game_concept>` 原样。后端 `get_concept` / `search_concepts` 已同步支持带/不带 namespace 的查询。
7. **ontology 概念直接引用，不在游戏层重复封装**: 当 ontology 中已有通用概念（如 `<setting>`、`<supply>`、`<player_board>`）时，游戏文件直接以 `<ontology::concept_id>` 引用或在顶层声明实例，不新建 `<game_xxx>` 包装概念。游戏特有的内容作为该概念定义中的示例/实例出现，避免同一语义两层定义。

## 评估结论

- **可以做**，但工程量大。
- 当前本体 71 个概念，已可覆盖文明演化的骰子、轨道、模组升级、地点被动效果等核心机制。
- 口播稿存在信息缺口：大量「等级二/等级三效果如图」、研究牌具体能力、24 个地点效果大部分缺失、部分数值模糊。
- 建议先做「能解释核心规则」的最小可用版，再逐步补全高级模组和牌表。

## 可用素材

| 文件 | 类型 | 用途 |
|---|---|---|
| `Civolution_Rules_US_web_v1_0.pdf` | 英文规则书 | 权威来源，精确流程/数值/卡牌能力 |
| `Civolution_Rules_US_web_v1_0.txt` | PDF 文本提取 | 快速检索、全文搜索 |
| `口播稿.md` | 口播脚本 | 讲解顺序、术语对照、重点机制 |
| `card/神权制.jpg` | 卡牌图片 | 验证研究牌三段式结构 |
| `pdftoppm` | 工具 | 将 PDF 页面转成图片供视觉分析 |

## 计划阶段（Phase A~E）

- **Phase A**: 对象清单 + ontology 扩展草案（骨架已完成，待 review）
  - 已扩展 ontology：新增 `dice`、`alternative_cost`、`choice`、`passive_effect`、`die_roll`、`upgrade`、`setting` 共 7 个概念。注：`terrain`、`region` 最初加入但于 2026-07-25 移回游戏层——地形类型本质是 zone 子类（`forest extends zone`），无需 ontology 概念；`encampment`、`site`、`favor_test` 也已移回游戏层
  - 已梳理全部 object/resource/piece/token/aid/zone 并写入 `games/civolution/concepts.json` 的 `objects` 层（169 个对象，含 45 个 effect 实例 + 15 个 module tile）
  - 已产出 `games/civolution/flow.json` 流程骨架（Setup、4 时代 × 8 阶段、终局计分）
  - 剩余：对象层 review、修正 extends/specifies/引用、补全 flow 中的占位 action（如 `<activate_module>`、`<reset>`）
- **Phase B**: 核心机制（区域/相邻/迁徙/生产/运输/建造/安装研究牌/收入芯片）
- **Phase C**: 22 个模组（1~3 等级拆分为 actions）
- **Phase D**: 流程层（4 时代 × 8 阶段 + 终局计分）
- **Phase E**: 接入 `BoardAI.Api` Runtime 验证

## 阻塞项

- 当前 `concepts.json` 的进程版图/流程版图部分已 review 完成；剩余 console（已部分更新）、supply、deck、piece/token、大陆/地形、骰子等组件待继续 review
- flow.json 中存在占位引用（如 `<activate_module>`、`<reset>`、`<action_phase_end>`），需要在 concepts.json 的 actions/conditions 层补全
- 需要从 PDF 中系统提取 22 个模组等级二/三效果、24 个地点效果、研究牌完整能力、事件牌/收入芯片/目标芯片集合
- 部分数值和图标需结合 PDF 图片确认（尤其是费用格图标、进程轨奖励线位置）
- Phase B~D 依赖对象层定稿，避免后续大量返工

### 2026-07-24 新增待办

- **[timing]** upgrade trigger 的 `<timing>` 暂留空——需等 Phase B 效果独立定义完成后，确定触发 upgrade 的具体 effect 再填充
- **[effect 独立定义]** 效果（effect）需独立定义为 concept 实例，模组只引用。当前 45 个 effect 实例为容器（无 cost/content），具体内容待 Phase B 填充。一个模组有两个骰子槽位（左下/右下），可能引用不同 effect；不同模组可共享同一 effect
- **[tile 多 effect 组合]** tile 可承载多个 effect（对应多个骰子槽位），关系为 AND（并）或 OR（或）。需考虑 composite effect 或在 tile 上表达 effect 组合关系

## 最近进展

- **2026-08-07（15 个主模组全部实例化 + 对话实测 + 逐模组 review）**:
    - **15 个主模组全部完成**（instances.json）。已确认骰子点数 8 个：research 1+2、migration 1+3、activity 2+3、exploration 1+4、sustenance 2+4、planning 3+4、transport 1+5、procreation 2+5；剩余 7 个 TBD：production/building/achievement/insight/mutation/invention/trade
    - **procreate 拆分为 pipeline（用户 review）**：繁育 = `place_new_tribe`（action：选区域+transfer）+ 4 个平铺 trigger（驱逐原部落/虚弱/篝火营地得分/开发领地），均 do_after 放置 action；虚弱 do_after 驱逐（规则书 "before"，驱逐不发生则不虚弱）；补「未开发区域立即开发」缺口（原定义缺失）。L3 恩惠检定与繁育顺序可互换（规则书原文 "either before or after"，无 do_after）
    - **新增 10 个基础 action**（flow.json，41 triggers）：procreate、hunt、strengthen_tribe、produce_material、transport_material（复合）、gain_activation_die、gain_fate_die、gain_goal_chip、place_planning_markers、move_feature_marker
    - **运输拆分（用户 review 修正）**：transport_material 拆为 transport_land_material（陆地按区域类型）+ transport_boat_material（船载，constraints 定义 storage_space 入参：缺省=相邻已开发区域类型、'any'=任意格，L3 使用）+ 复合 CHOOSE_ONE。参照 migrate = move_tribe + resolve_migration_triggers 模式——需要「实例化时区分入参」的底层 action 用 constraints 定义参数
    - **hunt 修正（用户 review）**：掷骰用 `<ontology::die_roll>`（fate_die[] count all）；食物数量改查表描述（quantity 0 占位删除）
    - **激活骰支付 destination 统一 `<player_holding>`**（36 处：15 主模块 + 6 feature 模块）：激活骰属于玩家，支付后放回玩家保留区（重置时拿回），非公共 supply
    - **标记物理形态统一**：供应堆里是通用 `<octagonal_pillar>`，进入食物格/创意格/钱币格/骰子格才「成为」food/idea_marker/money/planning_marker（6 处修正 + planning_marker/idea_marker 定义 component 印证）
    - **planning_marker 描述补充**：用作支付时放回 `<ontology::supply>`（不进入玩家保留区），只能替代 `<activation_die>` 不能替代 `<fate_die>`；idea_marker 显式「可修改 activation_die 或 fate_die 点数」
    - **单选项 condition 修正**：单条谓词直接写 zh/en，不再用 options/type 结构（7 处）；多条才用 options
    - **cost 可读性修复**：15 个模组 cost 增加 description 明确「两项骰子都要支付，非二选一」——实测中发现 LLM 将 EXECUTE_ALL 双骰误读为「或」（Q5 回答成「一颗点数为一或四」）
    - **对话实测结论**：10 题全部准确（含终局计分、喂养、目标芯片等复杂题）；耗时 4.4~21.5s 平均约 8s；状态依赖问题正确反问；工具链 2-6 轮偶有重复搜索
    - **索引重建**：civolution 434 概念、splendor 160 概念，索引脚本兼容
    - **工作方式反馈**：用户偏好 Edit 工具逐处修改（可审查 old→new），脚本只用于真正的机械批量且需先展示脚本内容

- **2026-08-06（ontology 体系化 + 控制台右半边 + 迁徙 pipeline 重构 + evaluate/check/state）**:
    - **ontology 新增**：`instant_cost`/`continuous_cost`/`instant_effect`/`continuous_effect`、`evaluate`（extends trigger，产出 result）、`check`（specifies evaluate，pass/fail）、`flip`（specifies state_change，face）、`temporary_zone`（瞬时中间态）、`state_change`（subject+to+optional attribute/from）
    - **state 实例化**：tribe posture upright/lying；card/tile face face_up/face_down
    - **控制台右半边完成**：activation_dice_area/fate_dice_area/feature_space/tier_1_completion_reward/tier_2_completion_reward/reset_column
    - **effect 结构规范化**：effect→instant/continuous_effect→content→instant_content，null 省略，cost 分 instant/continuous_cost
    - **迁徙 pipeline 拆分**：move_tribe（纯 transfer）+ resolve_migration_triggers（4 trigger，入参用 constraints 形式化）+ migrate = move→triggers。模块中灵活组合序列
    - **favor_test 重写**：specifies check，content 掷 fate_die，result = any ∈ [1, track.position]
    - **instances.json 大清理**：删除 45 个 effect 占位符；module_tiles 精简
    - **module 新模型**：upgradable_module extends module，模块层 cost + level_effects[].content；research（骰 1+2）、migration（骰 1+3）已完成

- **2026-08-04（控制台 review 继续 + trigger 迁入 flow + pipeline + 材料体系 + 建造行动）**:
    - **ontology**：`multiple_choice_enum` 新增 `CHOOSE_ANY`（任意数量执行）；`trigger_pipeline` 定义标准执行流程
    - **flow.json `triggers` 数组**：新建，容纳 action/activation/event/transfer/upgrade/play 类概念。`gain_income_chip` 从 procedures 迁入
    - **events[] 弃用 → pipeline**：全文件 `events` 数组改为 `pipeline: { options, type }` 格式，与 ontology `trigger_pipeline` 对齐
    - **type → specifies 统一**：全文件 `"type"` 改 `"specifies"`，仅 `multiple_choice_enum` 保留 `"type"`
    - **console parts**：`lv3_effects` → `lv3_effect_zone`（zone 参照 goal_area）；匿名 stage effect+aid → `stage_indicator`（specifies effect + aid part）；删除 `dead_tribe_area`；food_space/money_space 位置修正为面板左半边 + 交易规则 aid
    - **settlement_zone**：4 格 player_holding + continuous_effect（Prosperity 钻石计分）+ 材料清单 aid
    - **farm_supply + boat_supply**：3 格/2 格 player_holding，不补充
    - **领地类型 6 个**：forest/grassland/hill/swamp/mountain/desert（specifies territory）
    - **stored_material 重写**：abstract，新增 territory_type/sell_price/lucky_harvest_die 属性
    - **18 种材料**：3 行（基础/稀有/珍贵）× 6 列（领地），各有 sell_price 1/2/3 和 lucky_harvest_die 范围
    - **storage_area 重写**：3×6 网格 + 行间钻石 aid（上下非空则激活）
    - **以下概念从 concepts.json 迁入 flow.json triggers**：trade、favor_test、activate_income_chip、perform_activity、install_research_card（合并 5 变体，card_type enum 区分）、install_goal_chip、install_income_chip、install_attribute_chip、lose_food、remove_tribe、push_any_progress_track、weather_effect、upgrade_main_module
    - **flow.json 新增 actions**：`lucky_find`（幸运收获）、`build_settlement`（建造聚落，4 格费用全写清）
    - **concepts.json 保留**：`activity`（effect-identity，定义"是什么"）
    - **待办**：建造聚落/农场/船/雕像 aid、feature_space（焦点格）、idea_space（创意格）

- **2026-08-01~02（cost/content 模型重构 + 控制台 review + 芯片安装体系）**:
    - **cost 二分**：`instant_cost`（一次性支付）+ `continuous_cost`（状态检查），替代旧 `condition` + `event`
    - **content 三字段**：`<instant_content>`（无条件一次执行）、`<continuous_content>`（无条件电平维持）、`<effect>`（条件触发，含 `instant_effect` 一次机会 + `continuous_effect` 持续武装）
    - **effect specifies trigger**：不再携带顶层 condition，由调用方决定时机。`instant_effect`（condition 失败永远消失）和 `continuous_effect`（持续监听，end_condition = null 永不自动终结）
    - **play 重构**：三步 `pay_cost` → `transfer_piece` → `resolve`，替代旧 effect 模式的安装操作。`install_xxx_chip` 和 `install_research_card` 底层均改为 `<ontology::play>`
    - **resolve 语义**：只接触 content 三种形态——instant 执行一次、continuous 进入生效池、effect 分 instant/continuous 武装
    - **三种芯片 parts 定义完成**：`goal_chip`（chip_name / cost / chip_number）、`income_chip`（effect）、`attribute_chip`（chip_name / cost / effect）
    - **install_goal_chip / install_income_chip / install_attribute_chip**：均改为 play，各自有列选择规则
    - **goal_area**：3 格 player_holding，每格 `continuous_effect`（condition="格空后"→content=`upgrade_main_module`）
    - **upgrade_main_module**：结构化升级——选模组→L1→L2 翻面 / L2→L3 放回游戏盒
    - **lose_food / remove_tribe → transfer**：不再是 effect/trigger，就是纯粹的转移操作
    - **控制台 content 数组移除**：`activity_01` 和 15 个 L3 效果改为 parts
    - **stage_tile 重构**：双面——激活面含 effect（终局计分）+ aid（费用格提示）
    - **控制台 stage 1-3 计分**：一组匿名 effect+aid（始终激活）
    - **所有 parts 迁至 `type` 字段格式**：console、research_card、event_card 等
    - **Splendor**：discount 删 target/params
    - **后端兼容**：`GameRulesService` 同步更新

- **2026-07-30（trigger/effect 模型重构 + activity_01 形式化）**:
    - **ontology 重构**：trigger 从 "timing + condition → events" 改为 "condition + cost + content" 递归模型。effect specifies trigger，仅额外增加 options。删除 `<passive_effect>`。新增 `<push_track>` event。
    - **activity 概念**：新增 `<activity>`（specifies `<ontology::effect>`），预填 condition="<perform_activity>"。
    - **activity_01 实例**：进 `<activity>s` 数组，结构化 cost（condition + transfer：money_space → supply）和 content（push_any_progress_track）。
    - **push_any_progress_track**：specifies `<ontology::push_track>`，track 绑为 5 条进程轨的 CHOOSE_ONE。
    - **Splendor 迁移**：8 处 trigger 全迁新模型（删 timing，event[] → content，加 cost=null）。
- **2026-07-27（概念补全 + parts 格式升级）**:
  - **新增 2 个概念**：`<card_name>`（specifies `<ontology::object>`，卡牌名称标签）和 `<weather_trend>`（specifies `<ontology::object>`，事件牌天气趋势指示器）。二者均为通用概念，后续实例化各卡牌时通过 parts 引用。
  - **event_card parts 维护**：事件牌三部分——`<card_name>`（左上角）、`<weather_trend>`（右上角）、`<ontology::instant_content>`（下半部分，全部即时内容；2026-07-30 由 instant_effect 改名）。
  - **parts 格式升级**：全局 `"as": "<concept>"` → `"<concept>": {...}`，概念 ID 直接做 key。Civolution 7 概念 + Splendor 2 概念共 ~37 个 part 全部迁移。
  - **终局计分区重构**：`<final_scoring_area>` 从 track 改为 zone，拆为两个子概念——`<final_scoring_area_icons>`（图标 zone，放置计分板块）和 `<final_scoring_area_hex>`（六角格 track，slots=null，终局计分时阶段标记逐格推进）。
  - **外观描述补全**：`<scoring_tile>`（小型矩形 + 一角弧形角，双面）、`<site>`（正八边形 + 一角弧形角）、`<hundred_point_token>`（正方形而非圆形）。
  - **命名对齐规则书**：`一百分指示物` → `100分指示物`，TTS 友好工作留给 LLM。
  - **event_card_space 英文描述修正**：左格为 face-up stack（非单张），去掉 setup 流程细节。
  - **phase_indicator 定义补全**：加入「六角形」同义词和终局计分流程引用，提升对「六角形黄色东西」类问题的搜索命中。
- **2026-07-26（天气轨效果模型）**: 天气轨从纯文本描述升级为结构化 trigger + effect 模型：
  - 新增 5 个概念：`<activate_income_chip>`（specifies `<ontology::activation>`）、`<perform_activity>`（specifies `<ontology::activation>`）、`<lose_food>`（specifies `<ontology::effect>`，cost=null）、`<remove_tribe>`（specifies `<ontology::effect>`，cost=null）、`<weather_effect>`（specifies `<ontology::trigger>`，timing=事件阶段天气标记移动完成，无 condition）
  - weather_gauge 新增 `<ontology::trigger>` 引用 `<weather_effect>`，5 个 slot 从 `description` 文本升级为 `"<ontology::effect>": <ref>` 结构化引用
  - 单引用格式：`"<ontology::effect>": "<activate_income_chip>"`；多选格式：`"<ontology::effect>": {"options": [...], "type": "<ontology::multiple_choice_enum.CHOOSE_ONE>"}`
  - phase_sequence 补 8 个 slot（每阶段名称+概要），final_scoring_area 补 `slots` 字段
  - ontology: `scale` → `slots`（必填），新增 `<multiple_choice_enum>`，effect/cost/content 各加 `options` 可选字段
- **2026-07-26（关系重构）**: `parent` 已拆分为 `extends` / `specifies` / `instance_of` 三种关系。Civolution concepts.json 中 7 个 extends（module、research_card、stored_material、feature_marker、continent、continent_tile、site）+ 101 个 specifies。instances.json 中 60 个 instance_of。后端代码零改动。详见 [[ontology-design]]。

- **2026-07-25（图片提取突破）**: OpenCV + PDF 布局分析成功提取组件图片：
  - **正确页面定位**：组件目录页是 PDF 第 4-5 页（非之前误用的 setup 页 6-7）
  - **方法演进**：纯 CV 阈值/边缘检测 → 失败（页面排版复杂）→ **投影分析法**：水平投影找组件行 + 垂直投影找行内单个组件 → 成功
  - **脚本**：`scripts/extract_components_cv.py`，使用 PyMuPDF（fitz）渲染 600dpi 页面 + OpenCV 投影分析 + 文字标签锚定命名
  - **输出**：126 个组件裁切 → `games/civolution/media/`（5.1 MB），其中 55 个大图（>30KB）为高质量组件照片
  - **标注图**：`page-04_600dpi_annotated.jpg`、`page-05_600dpi_annotated.jpg` 供人工审核检测框质量
  - **关键发现**：PDF 页面是单张全页渲染图（非独立嵌入图片），组件是整张图内的子区域
  - **人工审核需要**：自动检测无法完美区分文字标签和组件照片（部分细长标签条、小图标被误检），建议人工筛选后保留 40-60 张关键组件图

- **2026-07-25（深夜）**: site 重构 + piece.parts 统一 + 图片提取探索：
  - **site_tile 并入 site**：删除过度抽象的 `site_tile`。全局替换 `<site_tile>` → `<site>`。
  - **新增 `<building_slot>`**（extends zone）：建造点 site 上的建造格。
  - **新增 `<site_slot>`**（extends zone）：continent 上 25 个凹槽（非 continent_tile），拼合后形成。
  - **`<piece>.parts` 统一机制**：ontology 中 `<piece>` 新增 `parts`（`any[]`）。zone 不再单独挂在 piece 上——territory、encampment、material_slot 都是 part。Civolution 8 个概念 + Splendor 2 个概念已全部迁移。纯引用用字符串 `"<territory>"`，带属性的用 `{ "as": "<score_track>", "position": ... }`。
  - **site.parts 新增 `<ontology::effect>`**：建造点和 8 个普通地点的效果都通过 parts 体现。
  - **constraints.optional 精简**：36 个 LOCAL 字段改为字符串格式，净减 255 行。
  - **图片提取探索**：`pdftoppm` 导出 PDF 第 6/7 页（组件展示）成功，但 DeepSeek v4 Pro 不支持多模态输入导致 Read 工具返回 `[Unsupported Image]`。结论：需要换用多模态模型（如 Kimi）才能让 LLM 直接识别组件并裁剪坐标。
  - **site.parts 新增 `<ontology::effect>`**：建造点和 8 个普通地点的效果都通过 parts 体现。
- **2026-07-25**: 概念与实例分离 + ontology 清理 + terrain/region 移除：
  - **新增 `instances.json`**：从 `concepts.json` 拆出 45 个 effect 实例 + 15 个 module tile 实例。文件分 `effects`、`modules`、`cards`、`continent_tiles`、`sites`、`chips` 六个数组。
  - **ontology 清理**：`<encampment>`、`<site>`、`<favor_test>`、`<terrain>`、`<region>` 从 ontology 移回游戏层（ontology 71→66）。concepts.json 新增 `site`、`favor_test`，`encampment` extends 改为 `<ontology::object>`，`site_tile` extends 改为 `<site>`。7 种地形改为 `<ontology::zone>` 子类，`territory` extends 改为 `<ontology::zone>`。
  - **后端更新**：`GameRulesService` 全面支持 `instances.json`，Python `rebuild_index.py` 新增 `extract_instances()`。
  - **大陆板块**：`continent_tile` 加 `size` 字段，`continent` zone 加 `grid`（5×3=15 格）和铺满约束。
- **2026-07-25（晚间）**: 对象层补充 + 命名修正 + 格式化：
  - **新增 `<material_slot>`**（材料板块格，extends zone）：位于 continent_tile 上，每陆地区域一个，放置 material_tile 决定产出材料类型
  - **新增 `<encampment>` + `<fire_encampment>`**：英文规则书用 encampment（非 campsite），fire_encampment inherits encampment
  - **命名修正**：campsite → encampment，fireside_encampment → fire_encampment，对齐英文规则书
  - **`continent_tile` 声明 `"<ontology::zone>[]"`**：引用 `<territory>`、`<material_slot>`、`<encampment>`、`<fire_encampment>`
  - **`starting_chip_card` zone 声明**：`"zones"` → `"<ontology::zone>[]"`
  - **移除 4 个纯 setup supply**：`module_supply`、`site_supply`、`material_tile_supply`、`scoring_tile_supply`——setup 用 `<ontology::game_box>` 即可
  - **ontology piece/board zones 字段统一**：`"zones"` → `"<ontology::zone>[]"`（key 即类型）
  - **制表符→4空格**：concepts.json + instances.json 统一格式化
  - **`<piece>` 定义修正**：`zones` 提升至 `<piece>`；play 能力由 ownership 决定。
- **2026-07-24（晚间）**: 重构模块升级模型——Lose + Gain：
  - **模块各等级改为独立 effect 实例**：15 个主模块各拆为 3 个 effect 实例（effect_xxx_lv1/lv2/lv3），通过 id 前缀保持模块 identity。L1/L2 由 tile 正反面持有，L3 由 console 持有。共新增 45 个 effect 实例 + 15 个 tile 概念。`module.level` 字段已删除。
  - **`<upgrade>` 父类改为 `<trigger>`**：核心语义是 level 提升，不再硬编码 lose/gain。同一载体（tile 翻面 L1→L2）仅为 level 变化；载体切换（L2→L3）时旧载体 `<lose>` 旧 effect、新载体 `<gain>` 新 effect。
  - **新增 `<lose>` 和 `<gain>` 作为 Event 子类**：`<lose>`——object 失去 property（domain → null）；`<gain>`——object 获得 property（domain → 新实体）。ontology 概念总数：69 → 71。
  - **console.content 已更新**：从 1 项扩展为 16 项（1 innate_activity + 15 lv3 effect），所有 lv3 effect 初始不可用，升级时由 console `<gain>`。
- **2026-07-24（凌晨）**: 理清模组本质与安装模型：
  - **模组是 effect，带 level state**：[已废弃，见上方晚间更新] 模组的 extends 从 `<ontology::piece>` 改为 `<ontology::effect>`。主模组有 level 1/2/3，不同 level 对应不同 cost/content，identity 不变。等级一二由 tile 承载，等级三由 board content 承载（印在控制台上）。升级（`<upgrade>`）本质是 state change——先提升 level，翻面/移除板块是后果而非原因。Ontology 中 `<upgrade>` 定义已同步更新。
  - **安装 = transfer**：卡牌/芯片安装到控制台就是从 source zone transfer 到控制台上逻辑坐标的 zone。Zone 是纯概念不绑定物理尺寸，所以纸片可以互相叠压。控制台每个行列坐标就是一个 zone（有独立 capacity）。
  - **实体承载的 zone 不会销毁**：初始芯片牌在 setup 后 zone 还在，只是没有规则再引用它——不需要引入 availability 概念。
  - **三级模组不是三个 effect**：[已废弃，见上方晚间更新——现已改为三个独立 effect 实例，通过 id 前缀关联]
- **2026-07-23**: 完成本体重大重构——Board 概念拆分与 Zone 宿主模型修正：
  - **新增 `<board>` 概念**（ontology 第 69 个概念）：从 `<aid>` 中拆出，承载游戏状态、可 host zone、可携带自身 content。Board 不可 transfer（区别于 piece），不承载状态的是 aid（缩窄为纯参考物）。`<public_board>` 和 `<player_board>` 的 extends 已从 `<aid>` 改为 `<board>`。
  - **Zone 可由实体承载**：card 和 board 都可以提供 zone。`<card>` 新增可选 `zones` 字段。Civolution 的 `starting_chip_card` 已标注设置阶段提供的临时目标芯片 zone。
  - **Board 的 `zones` 字段替代 `maps_to`**：`zones` 表达物理宿主关系（附带 position 和 description），而非 aid 时代的弱视觉映射。Civolution 的 `console`、`progress_board`、`sequence_board`、`public_board` 均已从 `maps_to` 迁移至 `zones`，每个 zone 附带面板上的物理位置描述。
  - **`console` 新增 `content`**：面板自带的基础活动图标——玩家无需安装任何研究牌即可使用的 innate 能力。
- **2026-07-23**: 完成进程版图与流程版图全部组件的逐项 review，主要改动：
  - **extends 归类修正**：`final_scoring_area` zone→track（本质是标记逐格推进的轨）；`dice_display`/`hunting_token_display`/`hundred_point_token_display` zone→supply；`goal_chip_display`/`income_chip_display`/`attribute_chip_display` zone→market
  - **market 新增 capacity**：ontology `market` 加 `capacity` 字段（`integer | null`），游戏层 `goal_chip_display`=6、`income_chip_display`=玩家人数+2、`attribute_chip_display`=3；`dice_display` 按玩家人数+1 每种骰子
  - **全局 namespace 引用**：`<ownership>` → `<ontology::ownership>`（21处）、`<information_visibility>` → `<ontology::information_visibility>`（21处）
  - **定义清理**：全文去掉「继承自」冗余表述，`extends` / `specifies` / `instance_of` 字段已足够
  - **8 个阶段概念**：按英文规则书名称定义 `phase_1_new_cards` ~ `phase_8_income`，不设 order（顺序由 flow.json 的 `do_after` 表达）
  - **`event_card_space` 两格结构**：右格背面朝上牌堆、左格正面朝上当前时代牌；定义中 "区域" → `<ontology::zone>`
  - **Splendor flow.json**：phase 排序从 `order` 改为 `do_after` 依赖链，与复杂流程一致
- 2026-07-22: 完成对象清单层细节修正：`private_board` → `player_board` 重命名；supply 的 public/player 区系统一用 `<ownership>` 表达，不再拆分子类；Civolution 中的「进程版图/流程版图」改为 `<progress_board>` / `<sequence_board>` 概念引用
- 2026-07-22: 新增 `<favor_of_ager_track>` 概念并替换所有「阿格拉恩惠轨」文本；新增 `<ontology::setting>` 概念承载世界观/背景，删除冗余的 `<civolution_setting>`
- 2026-07-22: 明确设计约定：ontology 已有概念直接引用，不在游戏层再包一层
- 2026-07-21: 扩展 `ontology/concepts.json`，新增 11 个 Civolution 所需概念；完成 `concepts.json` objects 层骨架和 `flow.json` 流程骨架
- 2026-07-21: 完成 namespace 替换并同步后端查询支持
- **2026-07-26（概念修正 + 前端图片渲染）**:
  - **删除 `<territory_token>`**：概念本质即 `<hunting_token>`（狩猎指示物），双面标记（正面狩猎/背面阻挡）。删除后全局无残留引用。
  - **`<site_slot>` 修正为 24 格**：site_slot 是分轨与大陆板块之间的空位，共 24 格。第 25 个位置由建造点 site 自带的 `<building_slot>` 提供，非 site_slot。
  - **前端图片渲染修复**：`wwwroot/index.html` 的 `addMessage` 对 assistant 消息改用 `innerHTML` + `mdToHtml()` 转换，`![alt](url)` 语法自动转为 `<img>` 标签，LLM 回复中的组件图片得以正常显示。
  - **搜索信任问题修复**：`search_concepts` 返回带元数据的 `SearchConceptsResult`（count/strategy/note），告知 LLM 是 Top-K 非穷举。新增 `list_concept_ids` 工具：穷举全量概念 ID+名称，按类型分组，极轻量。System prompt 明确工具选择策略：search_concepts 找入口 → get_concept 跟引用 → list_concept_ids 仅兜底穷举。解决 LLM 不信任部分结果、反复换关键词查全量的问题。
  - **`player_console` → `console` 重命名**：概念 ID、文件路径、flow.json 与 concepts.json 中所有 `<player_console>` 引用、civolution-progress.md 全文替换。

- **2026-07-26（组件图片按边框裁切）**: 用 OpenCV CCOMP 轮廓层级法从 PDF 规则书检测黑色矩形边框裁切组件，产出一批裁切图到 `media/by_border/`。

## 相关记忆

- [[project-overview]] — 项目阶段与当前重点
- [[ontology-design]] — 本体扩展约定
- [[splendor-progress]] — 第一款游戏的实现参考
- [[runtime-architecture]] — 后端接口与验证方式

**Why:** 记录第二款游戏的形式化进度，避免下次重新开始评估。
**How to apply:** 15 个主模组已全部实例化，其中 8 个点数已确认、7 个 TBD 待用户补充（production/building/achievement/insight/mutation/invention/trade）。逐模组 review 进行中（sustenance/planning/transport/procreation 已过）。待办：upgrade trigger timing、effect 独立定义、tile 多 effect AND/OR 组合。
