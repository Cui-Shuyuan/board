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
- **当前状态**: Phase A 进行中 —— `concepts.json` 的 `objects` 层骨架已产出，包含资源、piece、token、terrain、zone、supply/market、aid 等约 80 个对象定义；待用户 review 后再进入 Phase B

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

## 评估结论

- **可以做**，但工程量大。
- 当前本体 51 个概念无法直接覆盖文明演化的骰子、轨道、模组升级、地点被动效果等机制。
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

- **Phase A**: 对象清单 + ontology 扩展草案（进行中，objects 层骨架已完成）
  - 已扩展 ontology：新增 `dice`、`terrain`、`region`、`campsite`、`site`、`alternative_cost`、`choice`、`passive_effect`、`die_roll`、`favor_test`、`upgrade` 共 11 个概念
  - 已梳理全部 object/resource/piece/token/aid/zone 并写入 `games/civolution/concepts.json` 的 `objects` 层
  - 剩余：对象层 review、修正 parent 引用、补充少量遗漏（如 progress_track 概念是否进 ontology）
- **Phase B**: 核心机制（区域/相邻/迁徙/生产/运输/建造/安装研究牌/收入芯片）
- **Phase C**: 22 个模组（1~3 等级拆分为 actions）
- **Phase D**: 流程层（4 时代 × 8 阶段 + 终局计分）
- **Phase E**: 接入 `BoardAI.Api` Runtime 验证

## 阻塞项

- 当前 `concepts.json` 是 Phase A 骨架，需要用户 review 后确认对象命名、parent 引用、遗漏项
- 需要从 PDF 中系统提取 22 个模组等级二/三效果、24 个地点效果、研究牌完整能力、事件牌/收入芯片/目标芯片集合
- 部分数值和图标需结合 PDF 图片确认（尤其是费用格图标、进程轨奖励线位置）
- Phase B~D 依赖对象层定稿，避免后续大量返工

## 最近进展

- 2026-07-21: 扩展 `ontology/ontology.json`，新增 11 个 Civolution 所需概念
- 2026-07-21: 完成 `games/civolution/concepts.json` 的 `objects` 层骨架，覆盖资源、piece、token、terrain、dice、zone、supply/market、aid 等约 80 个对象定义

## 相关记忆

- [[project-overview]] — 项目阶段与当前重点
- [[ontology-design]] — 本体扩展约定
- [[splendor-progress]] — 第一款游戏的实现参考
- [[runtime-architecture]] — 后端接口与验证方式

**Why:** 记录第二款游戏的形式化进度，避免下次重新开始评估。
**How to apply:** 当前节点为 Phase A 骨架完成，等待用户 review；review 通过后进入 Phase B 核心机制或继续补全对象层遗漏。
