# Board AI — 桌游规则操作系统

## 会话启动顺序

1. 读 `.claude/memory/MEMORY.md`。
2. 按索引读当前文档：
   - `current-state.md` — 进度、待办、工作区状态
   - `project-overview.md` — 定位、功能、阶段
   - `architecture.md` — 本体 / Runtime / 检索 / Android 与语音接口
   - `tutorial-animation.md` — 讲规动画 v3
   - `animation-refactor-todo.md` — 动画代码重构待办（新会话开工前读）
   - `game-status.md` — 九款游戏状态
   - `conventions.md` — JSON / 本体 / pipeline 规范
   - `tools.md` — 服务、脚本、校验、编译命令
   - `user-preferences.md` — 协作方式与设计偏好
   - `flow-guide.md` — 下一步产品方向
3. 只有需要追溯旧决策时，才搜 `.claude/archive/`。归档不是必读材料。

## 当前状态

见 `.claude/memory/current-state.md`。一句话：Runtime / 搜索 / 规则数据已跑通；后端 `GameRulesService` 已完成服务化拆分，161 条 xUnit 全绿；检索默认 CLS，相关性和规则事实实测见 `docs/reviews/retrieval-followup-2026-10-09.md`；当前工程活跃面还包括 Android UaaL 客户端、内容更新 v1、语音问答 v1；动画 full 已完成终局与真卡身份收口，下一步逐 cue 重审，Flow Guide 是动画收口后的下一产品方向。

## 关键文件

- `content/ontology/concepts.json` — 统一本体（当前 89 个概念）
- `content/ontology/flow.json` — 通用 trigger pipeline
- `content/games/{game}/concepts.json` — 游戏概念层
- `content/games/{game}/flow.json` — 游戏流程层
- `content/catalog/splendor.json` — Android 首页游戏目录（当前仅 Splendor）
- `content/manifests/splendor.json` — 内容同步 manifest 生成物，不入 Git（当前仅 Splendor）
- `backend/BoardAI.Api/` — .NET 9 Runtime 服务
- `clients/android/` — UaaL 原生 Android 壳 + Compose 控制层
- `clients/unity/` — Unity 6 客户端 / Unity as a Library 导出侧
- `content/games/splendor/tutorial/anim/v2/full.anim.json` — 当前动画源
- `content/games/splendor/tutorial/anim/v2/full.compiled.json` — Unity 实际读取的编译产物
- `.claude/archive/` — 历史日志、旧版长文档、聊天记录，默认不读

## 工作约定

- 新增概念前先查当前 ontology JSON 和 `conventions.md`，确认是本体扩展还是游戏层实例；两个 v0 文档仅供追溯早期设计。
- 写完规则 JSON 必跑 `python tools/content/validate_rules.py`。
- 改规则文件后由 `RulesDocumentStore` 自动失效，无需重新启动 API 服务；语义检索需重建 Qdrant 索引（admin/rebuild-index 或 `tools/indexing/rebuild_index.py`）。
- 每个满意节点用 git commit。
- 代码/数据优先考虑程序确定性，LLM 只做语言理解与表达。
- 中英文双语字段以中文为主。
- 所有 ontology 概念统一在 `content/ontology/concepts.json` 定义。
- 讲规动画按“文字脚本 → BoardAI 校验 → 原语 → 对账”顺序改，禁止先改 events 再补文字。
- 改任何一个 cue 后，必须由 AI/人根据改动点**手写最小事实问题**，提前写进该 cue 的 `qa` 字段（或 `_qa/questions.json`），再由脚本自动问运行中的 Board API `/api/chat` 判断合法性；脚本只负责发送和留档，不自动生成问题。
- 改 cue 的 script/events/state/contract 时，必须同步修改该 cue 的 `qa`。只改动画不改问题视为未完成，不允许提交；过期问题重复问等于没有校验。
- `offstage` / 可见性隐藏只用于引擎确实需要的隐藏，不得用来掩盖非法棋盘状态；隐藏后每色宝石、黄金、卡牌实物总数仍必须守恒。
