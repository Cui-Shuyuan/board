# Board AI — 桌游规则操作系统

## 项目记忆

**先读取 memory/ 目录下的所有文件了解项目全貌。** 关键文件：
- `memory/project-overview.md` — 核心架构与设计理念
- `memory/ontology-design.md` — 本体 JSON 约定与当前进度
- `memory/user-preferences.md` — 用户的设计偏好与工作方式

## 当前状态

正在进行桌游本体的 JSON 建模。核心文件：
- `ontology/ontology.json` — 统一本体定义（Level 0 基础概念 + Level 1 游戏概念）

已完成的 Level 0（7个）：Object、Zone、State、Property、Event、Condition、Procedure
已完成的 Level 1 Structure（5个）：Player、Resource、Piece、Aid、Token
已完成的 Level 1 Procedure（4个）：Round、Turn、Phase、Transfer

## 工作约定

- 每个满意节点用 git commit
- 代码/数据优先考虑程序确定性，LLM 只做语言理解
- 中文为主语言，英文备选
- 所有概念在 `ontology/ontology.json` 中统一定义，不再分拆文件
