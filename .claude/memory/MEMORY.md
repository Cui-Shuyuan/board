# 项目记忆索引

新会话先按顺序读：

1. [current-state.md](current-state.md) — 当前进度、工作区状态、待办与不做事项（入口）。
2. [project-overview.md](project-overview.md) — 定位、功能、阶段与技术选型。
3. [architecture.md](architecture.md) — 本体 / Runtime / 检索 / Android / 语音接口。
4. [tutorial-animation.md](tutorial-animation.md) — 讲规动画 section-first 源模型、生产流程与当前状态。

再按任务读取：

- [conventions.md](conventions.md) — 规则 JSON / 本体 / pipeline / 动画 QA 规范。
- [game-status.md](game-status.md) — 九款游戏数据与 QA 状态。
- [tools.md](tools.md) — 服务、校验、索引、客户端、动画与 QA 命令入口。
- [flow-guide.md](flow-guide.md) — 动画收口后的 Flow Guide 产品方向。
- [user-preferences.md](user-preferences.md) — 协作方式与设计偏好。

动画任务补充入口：每次调整必须追加 `../../docs/animation-change-log/entries.jsonl`，并运行 `python3 tools/ops/check_animation_change_log.py`；偏好归纳暂不维护，等记录积累后再基于完整 entries 归纳。

## 默认不读

- `.claude/archive/` — 历史日志、旧版长文档、聊天记录，以及已完成的执行待办。需要追溯旧决策时再搜。
- `.claude/memory/` 之外按任务读取，不整库通读。

## 权威来源

- 代码：`backend/`、`clients/`、`animation/`、`tools/`。
- 规则数据：`content/ontology/`、`content/games/`；游戏目录：`content/catalog/`。
- 动画源：`content/games/splendor/tutorial/{tracks,sections,shared}/`。
- 编译/运行产物：`tutorial/{full,quick}.runtime.json`、`tutorial/anim/v2/{full,quick}.compiled.json`。
- 检索基准：`tools/qa/retrieval_gold.jsonl`、`retrieval_regressions.jsonl`、`retrieval_questions.jsonl`、`rule_fact_regressions.jsonl`。
- 冲突时以代码和 JSON 数据为准，并顺手更新本目录。
