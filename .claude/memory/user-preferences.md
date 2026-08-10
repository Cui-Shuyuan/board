---
name: user-preferences
description: 用户的设计偏好和工作方式
metadata: 
  node_type: memory
  type: user
  originSessionId: fbc4a732-5c5a-482a-a184-077fdaab9f56
---

# 用户偏好

## 设计哲学
- 追求确定性和可验证性——「程序永远 100%，LLM 可能 99.99999% 但永远不是 100%」
- 喜欢先把核心数据结构设计好再写代码，避免返工
- 愿意花时间讨论和推敲概念边界的细微差别（如 Piece vs Token vs Resource）
- 设计目标是长期可扩展的平台，不是一次性 demo
- **ontology 概念优先直接复用**：当 ontology 已有通用概念时，不在游戏层重复封装一个 `<game_xxx>` 包装，避免同一语义两层定义

## 工作方式
- 偏好结对编程式协作——AI 写代码/设计，用户审查决策
- 重要决定会暂停讨论，提出关键反例帮助打磨设计
- 用 git 做版本控制，每个满意节点 commit
- **写脚本批量修改文件之前，一定先 commit 当前未提交的修改**（2026-08-10 用户强调）——脚本有 bug 需要 `git checkout` 恢复时，不会误丢未提交的工作。教训：do_after 批量转换脚本两次 bug（闭合行 `],` 未识别、逗号丢失），两次靠 checkout 恢复，幸好每次都先重新应用了未提交的修改

## 项目代号选择
聊天记录中提到 BoardBrain、MeepleAI、RuleOS 等代号，尚未正式选定。

## 母语
中文。本体中的中文描述为主要语言，英文为备选。

**Why:** 用于新会话中快速理解用户的设计偏好。
**How to apply:** 保持同样的讨论节奏——推敲概念边界，优先数据结构，追求程序确定性。
