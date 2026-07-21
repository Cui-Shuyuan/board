---
name: ontology-direct-use
description: 设计约定：ontology 已有概念直接引用，不在游戏层重复封装
metadata:
  type: feedback
---

# ontology 概念直接引用，不在游戏层重复封装

用户反复纠正：当 ontology 中已有通用概念（如 `<setting>`、`<supply>`、`<player_board>`）时，游戏文件应直接以 `<ontology::concept_id>` 引用，或声明其实例；**不要新建 `<game_xxx>` 包装概念**。

典型案例：
- 错误：为《文明演化》单建 `<civolution_setting>`，而 ontology 已有 `<setting>`。
- 正确：把《文明演化》背景故事直接写入 `<ontology::setting>` 的定义，游戏中直接引用。

**Why:** 重复封装会让同一语义出现两层定义，增加维护成本，也容易写错。
**How to apply:**
- 新增游戏概念前先查 ontology，看是否已有对应通用概念。
- 游戏特有的内容/故事作为该概念定义的示例或实例出现，而不是再造一个游戏级 wrapper。
- 只有当 ontology 真的无法覆盖、需要引入新机制时，才向 ontology 扩展。

## 相关记忆
- [[ontology-design]] — 本体扩展约定与当前进度
- [[user-preferences]] — 用户的设计偏好
- [[civolution-progress]] — 第二款游戏的形式化进度
