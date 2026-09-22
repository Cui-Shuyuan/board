# 项目记忆索引

新会话按顺序读：

1. [current-state.md](current-state.md) — 当前进度、待办、工作区状态
2. [project-overview.md](project-overview.md) — 项目定位、功能、阶段、技术选型
3. [architecture.md](architecture.md) — 本体/Runtime/检索/交互架构
4. [tutorial-animation.md](tutorial-animation.md) — 讲规动画 v3 当前模型与生产流程
5. [game-status.md](game-status.md) — 九款游戏数据与 QA 状态
6. [conventions.md](conventions.md) — 规则 JSON / 本体 / pipeline / 命名规范
7. [tools.md](tools.md) — 服务、脚本、校验、编译命令
8. [user-preferences.md](user-preferences.md) — 用户偏好与协作方式
9. [flow-guide.md](flow-guide.md) — 讲规动画之后的 Flow Guide 方向

## 默认不读

- `.claude/archive/` — 历史日志、旧版长文档、聊天记录。需要追溯旧决策时再搜。
- `.claude/memory/` 之外的原始规则书、QA 结果、生成物按任务需要读取。

## 权威来源

- 当前代码：`backend/`、`client/`、`scripts/`
- 当前规则数据：`ontology/`、`games/`
- 当前动画数据：`games/splendor/tutorial/anim/v2/`
- 当前检索基准：`qa/retrieval_gold.jsonl`
- 遇到文档与代码/数据冲突，以代码和 JSON 数据为准，并顺手更新本目录。
