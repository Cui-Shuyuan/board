---
name: ontology-design
description: 桌游本体 JSON 的设计约定、关键决策和当前进度
metadata: 
  node_type: memory
  type: project
  originSessionId: fbc4a732-5c5a-482a-a184-077fdaab9f56
---

# 桌游本体设计

## 文件位置
D:\workspace\board\ontology\ontology.json

## 统一本体
最初分为 Structure Ontology（静态结构）和 Procedure Ontology（流程时序）两个文件，后来合并为一个统一的本体文件。因为两类概念互相耦合，且 JSON 是给程序读的，不需要考虑 LLM 上下文长度。

## 基础约定
- 每个概念都有 `id`、`name`（中英双语）、`level`、`abstract`、`definition`、`fields`
- `fields.required` = 继承或引用此概念时必须提供值的字段。程序递归收集 parent 链上的所有 required 字段
- `parent` 字段表示继承关系。Level 0 概念无 parent
- `level`: 0 = 基础概念（不可再分），1+ = 游戏概念
- `abstract: true` = 基类，不可直接实例化

## 关键设计决策

### predicate 字段被删除
最初 State/Property/Event 有 `predicate` 字段，后删除。因为类型层级本身就承载了分类信息（如 Trigger extends Event，不需要 predicate="trigger" 再声明一遍）。

### 删掉的 Level 0 概念
- 原文 Randomness（随机）：认为 Shuffle/Dice Roll 是 Action 或 Event，不是独立概念。随机性是某些事件结果的特征，不是事件本身的种类
- 原文 Score（分数）：正在评估。可能拆解为 State（只读分数）+ Resource（可花费分数）+ Victory Condition（判定胜负）

### Procedure 嵌套模型
Round、Turn、Phase 不是固定的层级关系，而是自由嵌套：
- Round：可重复的循环单元，children 只能是 Phase
- Turn：排他行动权授予单一玩家，children 只能是 Phase，额外 required 字段 `player`
- Phase：唯一承载「规则上下文」的 Procedure。children 可以是 Round/Turn/Phase 任意组合，或叶子节点直接包含 Action 列表
- 一整局游戏 = 最外层的 Round

### Piece vs Token 的核心区分
- **Piece**：携带 Effect，可被 Play（打出/建造/激活），改变游戏状态。如发展卡、船只六角片
- **Token**：没有 Effect，不能被 Play，被动标记事实。如起始玩家标记、伤害标记
- 这不是「有没有 Property」的区分，而是「能否通过 Play 产生 Effect」的区分

### Resource 与 Token 的关系
- Resource 自带 `appearance`（optional），自己描述外观，不依赖 Token
- Token 的 `represents` 改为 optional——仅当 Token 指向 State 或抽象概念时使用
- 避免 Resource-Token 的 1:1 冗余配对

## 当前进度

### Level 0（7个，全部完成）
Object、Zone、State、Property、Event、Condition、Procedure

### Level 1 Structure（5个，全部完成）
Player、Resource、Piece、Aid、Token

### Level 1 Procedure（4个，全部完成）
Round、Turn、Phase（均 extends Procedure）、Transfer（extends Event）

### 待写
- Zone 子类：Reserve → Supply, Market, Deck, Pool, Discard Pile / Player Zone → Player Holding, Development Area
- Event 子类：Action, Trigger, Resolve, Activation, Play
- Piece 子类：Card, Tile
- Effect, Cost, Endgame Condition, Victory Condition

**Why:** 本体是整个系统的类型系统，后续 Rule DSL、Tutorial Tree、Controller 都建立在它之上。
**How to apply:** 新增概念时遵循相同的 JSON schema 约定。每个概念定义 fields.required（必答问题）和 fields.optional。
