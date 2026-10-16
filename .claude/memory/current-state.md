---
name: current-state
description: 新会话入口——当前进度、工作区状态、待办与不做事项
metadata:
  type: project
---

> 最后更新：2026-10-16 · 当前分支 `refactor/section-first-animation`，HEAD `188a60d`（2026-10-15）；完整旧待办已归档 `.claude/archive/memory/2026-10-16/`。

# 当前状态

## 一句话

Runtime / 搜索 / 规则数据已跑通；后端 `GameRulesService` 已完成服务化拆分，xUnit 161/161；Splendor full / quick 已迁移到 section-first + reusable materials 源模型，共享局面/动作/表现从 `tutorial/library/` 引用；full 67 cue、quick 24 cue 静态检查全绿，素材库单测与两轨迁移对账已完成。当前正在做 quick / full 真人视觉重审、Android 真机端到端验收，最后进入 Flow Guide。

## 最近进展

- **素材化重构与 R1–R6 复核修复已完成（2026-10-17）**：新增 `tutorial-material/v1`、`material_library.py`、`material_cli.py`、`check_materials.py`；编译链按调用边界检查 action/example 契约、递归保留嵌套 usage、强制类别约束与 namespace 隔离、QA HEAD 基线包含 library。Splendor 抽出 10 actions / 3 examples / 1 presentation，full 31、quick 20 处 usage（含嵌套）；迁移后两轨 compiled 与迁移前除 `source_sha256` 外逐字段一致；非卡牌夹具 detailed/compact/same 走同一编译链。
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
- Python animation/索引回归 227/227；后端 xUnit 161/161；`validate_rules.py --errors-only` 为 0 errors / 72 warnings。
- Android JVM 当前 12 个测试文件 / 93 个 `@Test` 全绿；真机端到端仍未验收。

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
