# Board AI — 桌游规则操作系统

## 会话启动顺序

1. 读 `.claude/memory/MEMORY.md`。
2. 按索引读当前文档：
   - `current-state.md` — 进度、待办、工作区状态
   - `project-overview.md` — 定位、功能、阶段
   - `architecture.md` — 本体 / Runtime / 检索 / 交互
   - `tutorial-animation.md` — 讲规动画 v3
   - `game-status.md` — 九款游戏状态
   - `conventions.md` — JSON / 本体 / pipeline 规范
   - `tools.md` — 服务、脚本、校验、编译命令
   - `user-preferences.md` — 协作方式与设计偏好
   - `flow-guide.md` — 下一步产品方向
3. 只有需要追溯旧决策时，才搜 `.claude/archive/`。归档不是必读材料。

## 当前状态

见 `.claude/memory/current-state.md`。一句话：Runtime 规则问答已跑通；当前重心是《璀璨宝石》讲规动画 v3 生产闭环收尾；之后进入 Flow Guide。

## 关键文件

- `ontology/concepts.json` — 统一本体（当前 89 个概念）
- `ontology/flow.json` — 通用 trigger pipeline
- `games/{game}/concepts.json` — 游戏概念层
- `games/{game}/flow.json` — 游戏流程层
- `backend/BoardAI.Api/` — .NET 9 Runtime 服务
- `client/` — Unity 6 安卓客户端
- `games/splendor/tutorial/anim/v2/full.anim.json` — 当前动画源
- `games/splendor/tutorial/anim/v2/full.compiled.json` — Unity 实际读取的编译产物
- `.claude/archive/` — 历史日志、旧版长文档、聊天记录，默认不读

## 工作约定

- 新增概念前先查两个 v0 文档和 `conventions.md`，确认是本体扩展还是游戏层实例。
- 写完规则 JSON 必跑 `python scripts/validate_rules.py`。
- 改规则文件后按“重建索引 → 重启 API”处理。
- 每个满意节点用 git commit。
- 代码/数据优先考虑程序确定性，LLM 只做语言理解与表达。
- 中英文双语字段以中文为主。
- 所有 ontology 概念在 `ontology/concepts.json` 统一定义，不再分拆。
- 讲规动画按“文字脚本 → BoardAI 校验 → 原语 → 对账”顺序改，禁止先改 events 再补文字。
- 改任何一个 cue 后，必须由 AI/人根据改动点**手写最小事实问题**，提前写进该 cue 的 `qa` 字段（或 `_qa/questions.json`），再由脚本自动问运行中的 Board API `/api/chat` 判断合法性；脚本只负责发送和留档，不自动生成问题。
- `offstage` / 可见性隐藏只用于引擎确实需要的隐藏，不得用来掩盖非法棋盘状态；隐藏后每色宝石、黄金、卡牌实物总数仍必须守恒。
