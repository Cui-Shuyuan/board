---
name: current-state
description: 新会话入口——当前进度、工作区状态、待办与不做事项
metadata:
  type: project
---

> 最后更新：2026-10-17 · 当前分支 `refactor/section-first-animation`；完整旧待办已归档 `.claude/archive/memory/2026-10-16/`。

# 当前状态

## 一句话

Runtime / 搜索 / 规则数据已跑通；后端 `GameRulesService` 已完成服务化拆分，xUnit 198/198；Splendor full / quick 已迁移到 section-first + reusable materials 源模型，共享局面/动作/表现从 `tutorial/library/` 引用；full 67 cue、quick 24 cue 静态检查全绿，素材库单测与两轨迁移对账已完成。当前正在做 quick / full 真人视觉重审、Android 真机端到端验收，最后进入 Flow Guide。

## 最近进展

- **Program 问答证据修复与基线隔离（2026-10-17）**：legacy/program prompt 分字段隔离，legacy 原文从 `a992cfe` 恢复并通过 SHA-256 断言；R1–R5 的证据身份、关联边、读取/绑定/装包计数、公平轮询、最终序列化预算、语义投影与分层 trace 已落地。后端 xUnit 220/220。31 题开发诊断集 ×3 双评共识中 legacy 68.8% vs 修复 program 37.6%，program 调用均值 2.29、P50 5,110ms，仍未过质量门槛，默认保持 legacy。报告 `docs/reviews/program-qa-evidence-repair-and-diagnosis-2026-10-17.md`。未跑 798 全量，未切换 Thinking。

- **去除问答过拟合并完成耗时归因（2026-10-17）**：删除组件数量/人数正则重答及相关强制提示，删除按字符数判断场景和将精确对象 intent 分数清零的处理，保留意图检索和候选确认。后端 181/181 测试通过。702 次历史聊天逐条对齐日志：新增平均 1357ms 中模型请求约 1158ms、工具约 199ms；模型调用次数 2.33→3.35，不能归因于纯思考变慢。报告 `docs/reviews/qa-generalization-latency-2026-10-17.md`。本次代码尚未重新跑 702 次真实模型质量评测，历史分数不能直接视为修订版结果。
- **新旧版本全面对比已完成（2026-10-17）**：使用 117 条覆盖九款游戏的问题集，每问两版各重复 3 次，共 702 次真实聊天；两 judge 保守共识严格回答成功率旧版 60.4% → 新版 75.5%（+15.1pp，95% CI +8.0~+22.2pp，McNemar p=0.0002），规则直接命中率 61.3% → 80.3%。新版质量胜出，但端到端中位延迟 2732ms → 3572ms。报告见 `docs/reviews/version-comparison-2026-10-17.md`，题集与评测工具在 `tools/qa/comprehensive_eval_questions.jsonl`、`tools/eval/`。
- **程序控制问答编排已实现但质量未过门槛（2026-10-17）**：新增 Understand → 程序读取正式证据包 → 结构化 Respond 状态机、四类出口、有界补充/修复、引用校验、事实包预算和阶段指标；旧自由循环保留为 legacy 对照。133 题 × 3 次配对实验（两 judge 共识）中，严格回答通过率旧版 68.7% vs 新版 56.9%，新版下降 11.8pp，95% CI [-19.1, -4.8]pp；模型调用均值 3.16 → 2.21，但 P50 延迟 3684ms → 4653ms。因此生产默认仍为 `OrchestrationMode=legacy`，program 仅作为显式实验路径保留。报告：`docs/reviews/program-controlled-qa-orchestration-2026-10-17.md`。后端 xUnit 198/198，Android 104/104。
- **查询意图保留与回答收敛（2026-10-17，已被 a992cfe 收敛修订）**：intent、候选确认与证据关联保留；a992cfe 删除了基于数量/人数的过拟合重答守卫。历史报告见 `docs/reviews/rules-query-intent-answer-2026-10-17.md`。
- **素材化重构与 R1–R8 复核修复已完成（2026-10-17）**：新增 `tutorial-material/v1`、`material_library.py`、`material_cli.py`、`check_materials.py`；编译链按调用边界检查 action/example 契约、递归保留嵌套 usage、强制类别约束与 namespace 隔离、QA HEAD 基线包含 library。Splendor 抽出 10 actions / 3 examples / 1 presentation，full 31、quick 20 处 usage（含嵌套）；迁移后两轨 compiled 与迁移前除 `source_sha256` 外逐字段一致；非卡牌夹具 detailed/compact/same 走同一编译链。
- **Board API QA 已补跑（2026-10-17）**：quick 10/10 通过；full 43 问中 3 问的回答需结合上下文（`take_gems/limit.001.2` 白宝石=钻石；另 2 条为 LLM 缺前提未追问，回答可接受）。合法性由 Board API 调 `/api/chat` 判断并留档；QA 留档按约定不入 Git，暂不修改问答 prompt。
- **section-first 动画源已完成**：track 只排序 section，源真值为 `tutorial/{tracks,sections,shared}/`；旧单文件 `script.{track}.json` / `anim/v2/{track}.anim.json` / `anim/v2/_stage/` 已删除，`build/` 是忽略的装配中间产物。
- cue id 统一为 qualified id `<section_id>/<local_id>`；section 内可写 local，跨 section 必须 qualified；`entry: "section:<id>"` 回到 section 起点。
- TTS 按 section 生成一段连续音频，section 内多条 cue 共享 `audio` 并用 `audio_start` 切画面。
- **Splendor quick 已是可播放 track**：当前 24 cue / 11 section / 106 anchors；catalog 默认 `tutorial_track=quick`，`tutorial_tracks=[quick,full]`。
- 最近 quick 收口内容包括：卡背三级展示、牌面/桌面镜头拆分、拿宝石提示与归还计数、黄金替代购买、贵族单/多张与分流选择、终局计分说明。相关提交集中在 `188a60d` 前后。
- 动画原语近期新增：`label_mode: add|replace` + `label_key`、`marker_key`、箭头 `arrow_direction` 与尾部文字、label 文本框自动适配。
- 本轮 quick 精修（`quick_end`、`quick_nobles`、`quick_reserve`、`quick_gold`、Unity 播放/标注等）以及 full/quick 开场盒面 no-zone stage 已提交到 `425fd14`；Android 统一问答入口、PTT 自动发送/上划取消与 API 进程内 ASR 也已提交。QA 留档仍按约定不入 Git。

## Splendor 验收基线（本轮已跑）

- full：67 cue / 12 section / 338 time anchors；`check_anim_v2` 与 `validate_anim_rules_v2` 均 0 warnings；`audit_anim_v2` 53 cues / 0 errors；`check_card_identity_v2` 553 states / 0 error。
- quick：24 cue / 11 section / 106 time anchors；`check_anim_v2` 与 `validate_anim_rules_v2` 均 0 warnings；`audit_anim_v2` 17 cues / 0 errors；`check_card_identity_v2` 333 states / 0 error。
- `compile_animation_v2 --check` 对两轨均为 “up to date”；`section_source.py --check` 解析通过。
- Python animation/索引回归 227/227；后端 xUnit 198/198；`validate_rules.py --errors-only` 为 0 errors / 72 warnings。
- Android JVM 当前 13 个测试文件 / 104 个测试全绿；真机端到端仍未验收。

## 当前已具备的能力

- **规则问答 / Runtime**：`GameRulesService` 为薄 facade，业务拆为 ContentStore、ConceptCatalog、NameIndex、Search、Index、Flow、Reference、Fact、Plan 协作类；规则按 Revision/内容哈希快照并通过 AsyncLocal 绑定整轮请求，改规则 JSON 自动 freshness，语义检索仍需重建 Qdrant 索引。
- **检索**：默认 CLS 整段；版本化 collection + 稳定 alias；85 条 gold 中 77 直接命中、8 条候选前三、0 wrong/no_match；7 条回归全部前三；真实聊天未宣称答案整体正确。报告见 `docs/reviews/retrieval-ranking-2026-10-09.md`、`retrieval-followup-2026-10-09.md`。
- **内容分发**：只有 Splendor 有 catalog / manifest / release；`rules_ready` / `tutorial_ready` / `tutorial_tracks` 已贯通后端和 Android；manifest/release 为生成物、不入 Git，生产主机需本机构建或单独部署。
- **Android / Unity**：UaaL 原生壳 + Compose 控制层、首页/搜索/历史/资源管理、增量下载/断点续传、教程播放器、PTT/ASR/回答 TTS/重播/继续播放；QA generation 隔离和 Evidence 展示已落地，完整真机验收待复测。
- **动画**：section-first 源、时间锚点、手写 Board API QA、编译/校验/采样对账链已跑通；发展卡真卡身份（方案 B）主体完成。

## 当前优先待办

1. **quick 视觉验收与内容发布**：quick 精修已提交并通过静态检查；继续完成 quick 截图确认，之后按流程 build manifest/release 并更新测试设备。
2. **Splendor full 逐 cue 重审**：真卡身份阻塞已解除，但还没有从头到尾重新过 67 cue；重点检查市场/贵族/预留/购买真卡画面、宝石数量复位、终局示例双发展区与贵族表现。
3. **Android 真机端到端验收**：PTT → ASR → chat → 回答 TTS → 回到动画/继续播放，以及打断后回跳重播。
4. **内容版本**：quick 当前改动尚未重新 build manifest；发布前运行 `python3 tools/content/build_content_manifest.py --game splendor` 并部署 release。
5. **Flow Guide**：动画逐 cue 收口后开始 Civolution 顶层 8 阶段循环 + 终局计分助手。
6. **其余游戏**：8 款规则游戏待补 catalog / manifest；补 manifest 不等于教程可播放，必须显式声明能力字段。

## 其他已知未闭环

- 检索回答层未宣称整体正确：81 次真实聊天 0 HTTP 错误，但仍有白色骰子额外编造、未知职业卡过度宣称不存在、终局九类计分请求触发 60 秒上限等问题；需要事实覆盖、计划拆分和答案级逐事实验收。
- 自然问句标签仍需补“需要哪些事实/允许的答案路径”，并用新验证集校准确无答案与歧义处理；多候选恢复当前保守不恢复。
- 其他主机部署时需同步迁移 CLS 索引与 API 配置；代码已 push 不等于远端已部署或内容已发布。
- 历史验收证据与完整列表见 `.claude/archive/memory/2026-10-16/project-review-todo.md` 和 `docs/reviews/retrieval-*-2026-10-09.md`。

## 当前不建议做

- 无交付阻塞时继续扩 ontology 概念。
- 在动画闭环和真机验收前同时铺开多款游戏教程。
- 把生成物、QA 留档、模型/音频/原始扫描、manifest/release 提交进 Git。

## 历史归档说明

- `animation-refactor-todo.md`（P0-P3 代码重构）和 `project-review-todo.md`（REV-01~08 与发布收口）已全部完成并归档到 `.claude/archive/memory/2026-10-16/`；其中保留的验收证据只在追溯时读。
- 未闭环的验收项已提炼到本文与 `tutorial-animation.md`，不要再重新执行已完成的重构/修复。
