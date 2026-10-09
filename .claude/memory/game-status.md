---
name: game-status
description: 九款游戏规则数据、流程、QA 与待办状态（以当前 JSON 文件和校验结果为准）
metadata:
  type: project
---

# 游戏状态

统计口径：`content/games/{game}/concepts.json` 顶层分组；`validate_rules.py --errors-only` 当前整体 0 errors / 72 warnings。Warnings 主要是 W07 缺 appearance 与 W05 孤立概念，不阻塞检索与 QA。

| 游戏 | concepts | flow | 状态 |
|---|---|---|---|
| Splendor | 22 objects / 5 actions / 3 triggers / 11 conditions | 有 | Runtime + 讲规动画试点；catalog + manifest v1 |
| Civolution | 146 objects + top_level_refs | 有 | 最复杂，持续补牌表/地点/数值 |
| Puerto Rico 1897 | 62 objects / 8 actions / 25 conditions + instances | 有 | 首版完成，待实物核实 |
| Brass: Birmingham | 47 objects / 7 actions / 3 triggers / 29 conditions | 有 | 首版完成，QA 48/50（待复核，沿用 2026-08 记录），4 处冲突待裁决 |
| Castles of Burgundy | 73 objects / 5 actions / 12 triggers / 15 conditions | 有 | 首版完成，QA 52/52（待复核，沿用 2026-08 记录），待用户 review |
| Ark Nova | 62 objects | 有 | 首版完成，QA 31/32（待复核，沿用 2026-08 记录），Q23 单卡文本挂起 |
| Agricola | 82 objects / 18 actions / 7 triggers / 20 conditions | 有 | 数据与 QA 脚本存在，无专门进度记忆 |
| Wingspan | 33 objects | 有 | 基础概念与流程已加；有 QA 结果和遗留 issues |
| Seasons | 32 objects | 无 | 仅 concepts 骨架，未接入完整流程 |

## Splendor

- `concepts.json` 与 `flow.json` 已完成，是 Runtime 首个验证游戏。
- 讲规动画 full 版：76 cue（cue id 已语义化），TTS、runtime、compiled、Unity 播放器与对账链已跑通；终局计分与真卡身份已收口。
- `time_anchors` 已全量迁移并 commit（`38971d4`）；源数据保留 anchor，compiled 输出数值 `at`。
- 口播 QA 已与 cue 同置流程落地：问题可/优先写在 `full.anim.json` 的 `qa` 字段，历史问题仍在 `_qa/questions.json`；`qa_anim_ask.py` 自动提取发送。
- 当前工作不是“动画时间锚点收口”；动画遗留见 `tutorial-animation.md`。
- 发展卡身份保真（方案 B）已主体完成：28 个 face-up 卡位各有独立真卡模板/扫描，`check_card_identity_v2.py` 当前 562 个中间状态 0 error。
- **下一步：Splendor full 重新过动画**；扫描件已补，重点重看 `action.cards.*` / `action.nobles.*` / `action.reserve.*` 等市场与发展区 cue 的真卡画面，以及 `setup.gems.005.1` 的宝石数量复位、终局示例的双发展区与贵族表现。
- catalog + manifest 已生成：`content/catalog/splendor.json` 入 Git；`content/manifests/splendor.json` 为生成物，不入 Git。
- 路径：`content/games/splendor/`。

## Civolution

- 第二款游戏，机制最复杂，是 Runtime 与形式化工作的压力测试对象。
- 已有 Setup、4 时代 × 8 阶段、终局计分轨骨架。
- 组件图片提取已完成一批；研究牌、事件牌、地点效果、22 个模组等级二/三效果仍需系统补全。
- 最新记录：FAQ 103 题复测 tier1 率约 92%（待复核，沿用 2026-08 记录）。
- 路径：`content/games/civolution/`，包含 PDF/TXT 规则书、`instances.json`、`media/`。

## Puerto Rico 1897

- 基础版 3–5 人首版完成；`concepts.json + flow.json + instances.json`。
- Flow：Setup → 年度循环 → 终局计分；判胜使用 FILTER。
- QA：55 道 FAQ 三轮基线；Runtime 三层回答体系在此游戏上验证（通过数待复核，沿用 2026-08 记录；结果文件 55 行）。
- 待实局核实：生产建筑份数、商业建筑半圆槽数、交易所售价等实物数值。
- 路径：`content/games/puerto-rico/`。

## Brass: Birmingham

- 首版概念层 + 流程层完成。
- Flow：Setup → 运河时代 → 铁路时代 → 终局计分。
- QA：50 题 48/50；遗留 FAQ 与规则书/MD 冲突 4 处，待用户裁决（通过数待复核，沿用 2026-08 记录；结果文件 50 行）。
- 路径：`content/games/brass-birmingham/`。

## Castles of Burgundy

- 概念层 + 流程层完成，校验全绿并已建索引。
- Flow：Setup → 5 阶段循环 → 终局（25 轮，回合顺序按石桥）。
- QA：52 题 52/52；6 处检索强化修复（通过数待复核，沿用 2026-08 记录；结果文件 52 行）。
- 待用户 review 与实物核实。
- 路径：`content/games/castles-of-burgundy/`。

## Ark Nova

- 概念层 + 流程层完成，校验 0 错误。
- Flow：Setup → 5 动作循环 → Break → 终局（触发 → 补完回合 → 计分 → FILTER 判胜）。
- QA：32 题 31 通过；Q23 单卡文本问题挂起，需卡牌实例化（通过数待复核，沿用 2026-08 记录；结果文件 32 行）。
- appearance 字段大量缺失，是当前 W07 warning 的主要来源，待用户自补。
- 路径：`content/games/ark-nova/`。

## Agricola

- 概念层 + 流程层文件完整，已有 55 条 QA 结果（通过数待复核，沿用 2026-08 记录；结果文件 55 行）。
- 有 `tools/qa/_qa_agricola_*` 跑批与分析脚本，但没有单独进度记忆；需要时直接看数据和 QA 脚本。
- 路径：`content/games/agricola/`。

## Wingspan

- 基础规则 concepts + flow 已加入。
- 有 49 条 QA 结果与 `tools/qa/_qa_wingspan_issues.md` 遗留问题清单（通过数待复核，沿用 2026-08 记录；结果文件 49 行）。
- 路径：`content/games/wingspan/`。

## Seasons

- 目前只有 `concepts.json`（32 objects），没有 `flow.json`。
- W07 appearance 缺失和 W05 孤立概念 warning 较多，属于半成品状态。
- 有 7 条 retrieval gold；未进入讲规与完整问答验证。
- 路径：`content/games/seasons/`。

## 全局待办

- 其余 8 款游戏补 `content/catalog/{game}.json` 与 `content/manifests/{game}.json`，让 Android 首页/内容更新覆盖全部游戏；Splendor 已完成 v1（manifest 生成物不入 Git）。
- 新增游戏或补 catalog / manifest 时按 `tools.md` 的索引/校验/QA/内容流程走，并记录“从开始到首次可问答”的耗时，作为平台边际成本的基准。
