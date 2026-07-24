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
- **当前状态**: Phase A review 进行中。进程版图与流程版图全部组件已过完；剩余：player_console 及其内部 zone、supply 类（module_supply 等）、deck 类、piece/token 类、大陆与地形、骰子等待 review。

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
- 当前本体 68 个概念无法直接覆盖文明演化的骰子、轨道、模组升级、地点被动效果等机制。
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
  - 已扩展 ontology：新增 `dice`、`terrain`、`region`、`campsite`、`site`、`alternative_cost`、`choice`、`passive_effect`、`die_roll`、`favor_test`、`upgrade`、`setting` 共 12 个概念
  - 已梳理全部 object/resource/piece/token/aid/zone 并写入 `games/civolution/concepts.json` 的 `objects` 层（约 80 个对象）
  - 已产出 `games/civolution/flow.json` 流程骨架（Setup、4 时代 × 8 阶段、终局计分）
  - 剩余：对象层 review、修正 parent/引用、补全 flow 中的占位 action（如 `<activate_module>`、`<reset>`）
- **Phase B**: 核心机制（区域/相邻/迁徙/生产/运输/建造/安装研究牌/收入芯片）
- **Phase C**: 22 个模组（1~3 等级拆分为 actions）
- **Phase D**: 流程层（4 时代 × 8 阶段 + 终局计分）
- **Phase E**: 接入 `BoardAI.Api` Runtime 验证

## 阻塞项

- 当前 `concepts.json` 的进程版图/流程版图部分已 review 完成；剩余 player_console（已部分更新）、supply、deck、piece/token、大陆/地形、骰子等组件待继续 review
- flow.json 中存在占位引用（如 `<activate_module>`、`<reset>`、`<action_phase_end>`），需要在 concepts.json 的 actions/conditions 层补全
- 需要从 PDF 中系统提取 22 个模组等级二/三效果、24 个地点效果、研究牌完整能力、事件牌/收入芯片/目标芯片集合
- 部分数值和图标需结合 PDF 图片确认（尤其是费用格图标、进程轨奖励线位置）
- Phase B~D 依赖对象层定稿，避免后续大量返工

### 2026-07-24 新增待办

- **[timing]** upgrade trigger 的 `<timing>` 暂留空——需等 Phase B 效果独立定义完成后，确定触发 upgrade 的具体 effect 再填充
- **[effect 独立定义]** 效果（effect）需独立定义为 concept 实例，模组只引用。当前 45 个 effect 实例为容器（无 cost/content），具体内容待 Phase B 填充。一个模组有两个骰子槽位（左下/右下），可能引用不同 effect；不同模组可共享同一 effect
- **[tile 多 effect 组合]** tile 可承载多个 effect（对应多个骰子槽位），关系为 AND（并）或 OR（或）。需考虑 composite effect 或在 tile 上表达 effect 组合关系

## 最近进展

- **2026-07-24（晚间）**: 重构模块升级模型——Lose + Gain：
  - **模块各等级改为独立 effect 实例**：15 个主模块各拆为 3 个 effect 实例（effect_xxx_lv1/lv2/lv3），通过 id 前缀保持模块 identity。L1/L2 由 tile 正反面持有，L3 由 player_console 持有。共新增 45 个 effect 实例 + 15 个 tile 概念。`module.level` 字段已删除。
  - **`<upgrade>` 父类改为 `<trigger>`**：核心语义是 level 提升，不再硬编码 lose/gain。同一载体（tile 翻面 L1→L2）仅为 level 变化；载体切换（L2→L3）时旧载体 `<lose>` 旧 effect、新载体 `<gain>` 新 effect。
  - **新增 `<lose>` 和 `<gain>` 作为 Event 子类**：`<lose>`——object 失去 property（domain → null）；`<gain>`——object 获得 property（domain → 新实体）。ontology 概念总数：69 → 71。
  - **player_console.content 已更新**：从 1 项扩展为 16 项（1 innate_activity + 15 lv3 effect），所有 lv3 effect 初始不可用，升级时由 console `<gain>`。
- **2026-07-24（凌晨）**: 理清模组本质与安装模型：
  - **模组是 effect，带 level state**：[已废弃，见上方晚间更新] 模组的 parent 从 `<ontology::piece>` 改为 `<ontology::effect>`。主模组有 level 1/2/3，不同 level 对应不同 cost/content，identity 不变。等级一二由 tile 承载，等级三由 board content 承载（印在控制台上）。升级（`<upgrade>`）本质是 state change——先提升 level，翻面/移除板块是后果而非原因。Ontology 中 `<upgrade>` 定义已同步更新。
  - **安装 = transfer**：卡牌/芯片安装到控制台就是从 source zone transfer 到控制台上逻辑坐标的 zone。Zone 是纯概念不绑定物理尺寸，所以纸片可以互相叠压。控制台每个行列坐标就是一个 zone（有独立 capacity）。
  - **实体承载的 zone 不会销毁**：初始芯片牌在 setup 后 zone 还在，只是没有规则再引用它——不需要引入 availability 概念。
  - **三级模组不是三个 effect**：[已废弃，见上方晚间更新——现已改为三个独立 effect 实例，通过 id 前缀关联]
- **2026-07-23**: 完成本体重大重构——Board 概念拆分与 Zone 宿主模型修正：
  - **新增 `<board>` 概念**（ontology 第 69 个概念）：从 `<aid>` 中拆出，承载游戏状态、可 host zone、可携带自身 content。Board 不可 transfer（区别于 piece），不承载状态的是 aid（缩窄为纯参考物）。`<public_board>` 和 `<player_board>` 的 parent 已从 `<aid>` 改为 `<board>`。
  - **Zone 可由实体承载**：card 和 board 都可以提供 zone。`<card>` 新增可选 `zones` 字段。Civolution 的 `starting_chip_card` 已标注设置阶段提供的临时目标芯片 zone。
  - **Board 的 `zones` 字段替代 `maps_to`**：`zones` 表达物理宿主关系（附带 position 和 description），而非 aid 时代的弱视觉映射。Civolution 的 `player_console`、`progress_board`、`sequence_board`、`public_board` 均已从 `maps_to` 迁移至 `zones`，每个 zone 附带面板上的物理位置描述。
  - **`player_console` 新增 `content`**：面板自带的基础活动图标——玩家无需安装任何研究牌即可使用的 innate 能力。
- **2026-07-23**: 完成进程版图与流程版图全部组件的逐项 review，主要改动：
  - **parent 归类修正**：`final_scoring_area` zone→track（本质是标记逐格推进的轨）；`dice_display`/`hunting_token_display`/`hundred_point_token_display` zone→supply；`goal_chip_display`/`income_chip_display`/`attribute_chip_display` zone→market
  - **market 新增 capacity**：ontology `market` 加 `capacity` 字段（`integer | null`），游戏层 `goal_chip_display`=6、`income_chip_display`=玩家人数+2、`attribute_chip_display`=3；`dice_display` 按玩家人数+1 每种骰子
  - **全局 namespace 引用**：`<ownership>` → `<ontology::ownership>`（21处）、`<information_visibility>` → `<ontology::information_visibility>`（21处）
  - **定义清理**：全文去掉「继承自/extends」冗余表述，`parent` 字段已足够
  - **8 个阶段概念**：按英文规则书名称定义 `phase_1_new_cards` ~ `phase_8_income`，不设 order（顺序由 flow.json 的 `do_after` 表达）
  - **`event_card_space` 两格结构**：右格背面朝上牌堆、左格正面朝上当前时代牌；定义中 "区域" → `<ontology::zone>`
  - **Splendor flow.json**：phase 排序从 `order` 改为 `do_after` 依赖链，与复杂流程一致
- 2026-07-22: 完成对象清单层细节修正：`private_board` → `player_board` 重命名；supply 的 public/player 区系统一用 `<ownership>` 表达，不再拆分子类；Civolution 中的「进程版图/流程版图」改为 `<progress_board>` / `<sequence_board>` 概念引用
- 2026-07-22: 新增 `<favor_of_ager_track>` 概念并替换所有「阿格拉恩惠轨」文本；新增 `<ontology::setting>` 概念承载世界观/背景，删除冗余的 `<civolution_setting>`
- 2026-07-22: 明确设计约定：ontology 已有概念直接引用，不在游戏层再包一层
- 2026-07-21: 扩展 `ontology/ontology.json`，新增 11 个 Civolution 所需概念；完成 `concepts.json` objects 层骨架和 `flow.json` 流程骨架
- 2026-07-21: 完成 namespace 替换并同步后端查询支持

## 相关记忆

- [[project-overview]] — 项目阶段与当前重点
- [[ontology-design]] — 本体扩展约定
- [[splendor-progress]] — 第一款游戏的实现参考
- [[runtime-architecture]] — 后端接口与验证方式

**Why:** 记录第二款游戏的形式化进度，避免下次重新开始评估。
**How to apply:** 当前节点为 Phase A 骨架完成、namespace 方案已确认；等待用户明天 review 对象清单与 8 阶段流程；review 通过后进入 Phase B 核心机制。
