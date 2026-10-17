# Board AI — 桌游规则操作系统

## 会话启动顺序

1. 读 `.claude/memory/MEMORY.md`。
2. 按 MEMORY 的“先按顺序读”读当前文档：
   - `current-state.md` — 进度、工作区状态、待办、不做事项
   - `project-overview.md` — 定位、功能、阶段、技术选型
   - `architecture.md` — 本体 / Runtime / 检索 / Android 与语音接口
   - `tutorial-animation.md` — 讲规动画 section-first 源模型与生产流程
3. 再按任务读 `conventions.md`、`game-status.md`、`tools.md`、`flow-guide.md`、`user-preferences.md`。
4. 只有需要追溯旧决策或已完成执行项时，才读 `.claude/archive/`。归档不是必读材料。

## 当前状态

一句话见 `.claude/memory/current-state.md`：Runtime / 搜索 / 规则数据已跑通；后端 `GameRulesService` 已服务化拆分，xUnit 198/198；Splendor full 67 cue、quick 24 cue 已迁移到 section-first + reusable materials 源且静态检查全绿；库内 10 actions / 3 examples / 1 presentation，R1–R8 复核修复与构建门禁完成；下一步是 quick/full 逐 cue 真人视觉重审、Android 真机端到端验收，之后进入 Flow Guide。

## 关键文件

- `content/ontology/concepts.json` — 统一本体（当前 89 个概念）
- `content/ontology/flow.json` — 通用 trigger pipeline
- `content/games/{game}/concepts.json` — 游戏概念层
- `content/games/{game}/flow.json` — 游戏流程层
- `content/catalog/splendor.json` — Android 首页游戏目录（当前仅 Splendor，默认 track 为 quick）
- `content/manifests/splendor.json` — 内容同步 manifest 生成物，不入 Git
- `backend/BoardAI.Api/` — .NET 9 Runtime 服务
- `clients/android/` — UaaL 原生 Android 壳 + Compose 控制层
- `clients/unity/` — Unity 6 客户端 / Unity as a Library 导出侧
- `content/games/splendor/tutorial/{tracks,sections,shared}/` — 动画源真值（section-first）
- `content/games/splendor/tutorial/{full,quick}.runtime.json` — 编译产物：音频与字幕
- `content/games/splendor/tutorial/anim/v2/{full,quick}.compiled.json` — Unity 实际读取的编译产物
- `.claude/archive/` — 历史日志、旧版长文档、已完成执行待办，默认不读

## 工作约定

- 新增概念前先查当前 ontology JSON 和 `conventions.md`；两个 v0 文档仅供追溯早期设计。
- 写完规则 JSON 必跑 `python tools/content/validate_rules.py`。
- 改规则文件后由 `RulesDocumentStore` 自动失效，无需重启 API 服务；语义检索需重建 Qdrant 索引（admin/rebuild-index 或 `tools/indexing/rebuild_index.py`）。
- 每个满意节点用 git commit。
- 代码/数据优先考虑程序确定性，LLM 只做语言理解与表达。
- 中英文双语字段以中文为主。
- 所有 ontology 概念统一在 `content/ontology/concepts.json` 定义。
- 讲规动画按“文字脚本 → BoardAI 校验 → 原语 → 对账”顺序改，禁止先改 events 再补文字。
- 改任何 cue 后，必须由 AI/人根据改动点**手写最小事实问题**，提前写进该 cue 的 `qa` 字段（或 `sections/<id>/qa.json`），再由脚本自动问运行中的 Board API `/api/chat`；脚本只发送和留档，不生成问题。
- 改 cue 的 script/events/state/contract 时，必须同步修改该 cue 的 `qa`。只改动画不改问题视为未完成，不允许提交；过期问题重复问等于没有校验。
- 纯介绍/纯展示、不涉及规则断言的 cue 可显式写 `qa_exempt: true`，门禁跳过该 cue 的必需问题校验；默认 false，不继承给后续 cue。
- `offstage` / 可见性隐藏只用于引擎确实需要的隐藏，不得用来掩盖非法棋盘状态；隐藏后每色宝石、黄金、卡牌实物总数仍必须守恒。
- 每次动画调整（game / section / cue / 修改前 / 期望 / 原因 / 实际 / commit）必须追加到 `docs/animation-change-log/entries.jsonl`，并用 `python3 tools/ops/check_animation_change_log.py` 校验。偏好归纳先不做，等 entries 积累到一定量后再归纳。
- 生成物、QA 留档、模型/音频/原始扫描、manifest/release 不入 Git。
