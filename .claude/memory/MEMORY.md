# 项目记忆索引

新会话按顺序读：

1. [current-state.md](current-state.md) — 截至 2026-10-08 的当前进度（HEAD 以 git log 为准）、工作区状态、待办与不做事项
2. [project-overview.md](project-overview.md) — 项目定位、功能、阶段、技术选型
3. [architecture.md](architecture.md) — Runtime 服务拆分、自动 freshness、检索、Android/语音接口
4. [tutorial-animation.md](tutorial-animation.md) — 讲规动画 v3：time_anchors / QA 同置 / 发展卡身份保真 / 当前动画遗留
5. [game-status.md](game-status.md) — 九款游戏数据与 QA 状态
6. [conventions.md](conventions.md) — 规则 JSON / 本体 / pipeline / 命名规范
7. [tools.md](tools.md) — 服务、规则 freshness、后端测试、语音桥、Android、编译命令
8. [user-preferences.md](user-preferences.md) — 用户偏好与协作方式
9. [flow-guide.md](flow-guide.md) — 讲规动画之后的 Flow Guide 方向
10. [animation-refactor-todo.md](animation-refactor-todo.md) — 动画工具链 / Unity Runtime 重构待办；新会话动手前先读
11. [project-review-todo.md](project-review-todo.md) — 2026-10-08 项目审查：8 项运行可靠性待办、验收标准及问题解决后的产品优先级

## 默认不读

- `.claude/archive/` — 历史日志、旧版长文档、聊天记录。需要追溯旧决策时再搜。
- `.claude/memory/` 之外的原始规则书、QA 结果、生成物按任务需要读取。

## 权威来源

- 当前代码：`backend/`、`clients/`、`animation/`、`tools/`
- 当前规则数据：`content/ontology/`、`content/games/`
- 当前游戏目录：`content/catalog/`
- 当前内容 manifest：`content/manifests/`（生成物，不入 Git）
- 当前动画数据：`content/games/splendor/tutorial/anim/v2/`
- 当前检索基准：`tools/qa/retrieval_gold.jsonl`
- 语音桥：`tools/voice/README.md`、`docs/ops/start-services.md`
- 遇到文档与代码/数据冲突，以代码和 JSON 数据为准，并顺手更新本目录。
