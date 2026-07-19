---
name: project-overview
description: AI桌游讲师项目总览——核心架构、设计理念、技术选型
metadata: 
  node_type: memory
  type: project
  originSessionId: fbc4a732-5c5a-482a-a184-077fdaab9f56
---

# AI 桌游讲师项目

## 核心定位
不是一个 AI 聊天机器人，而是一个规则驱动的 AI Runtime / 桌游规则操作系统（Board Game Rules OS）。

## 核心设计原则
- **LLM 负责理解语言，程序负责执行规则**——规则查询、校验、模拟全由程序完成，LLM 只管把结果翻译成人话
- **数据驱动**：每款桌游就是一个目录（manifest.json + ontology/ + rules/ + tutorial/ + media/），增加桌游不改代码
- **规则用 JSON/DSL 表达，不是 Markdown**——程序需要结构化条件+效果
- **意图分类（Intent）优于规则分类**——七八个核心接口（ExplainConcept、CheckAction、SimulateAction 等）覆盖所有用户问题

## 技术选型
- 后端：C#（状态机友好，未来可接 Unity）
- 前端：PWA / Flutter（平板端）
- LLM 层：接口抽象 ILLM，支持 GPT/Claude/Gemini 等切换
- STT/TTS：独立模块，可插拔
- 部署：Windows 主机本地运行，平板走内网 HTTP/WebSocket
- 店内网络：路由器 Guest Network 隔离客人设备

## 应用场景

一家桌游主题小店。每张桌子配一台平板，客人坐下后选择游戏，AI 讲师开始按教程树（Tutorial Tree）讲解规则。客人可以随时按住说话提问（「为什么不能拿两个蓝？」「我下一步最好做什么？」），AI 回答。全程 Push-to-Talk 作为唯一语音输入方式，状态机只在 Idle → Recording → Thinking → Speaking → Idle 之间切换。

## 开发阶段

1. **第一阶段（基本完成）：定义世界模型** — 51 个本体概念覆盖 Level 0~3，可持续补充。
2. **第二阶段（基本完成）：Rule DSL** — 用结构化 JSON 表达具体游戏规则。首个游戏：璀璨宝石（Splendor），`concepts.json` 与 `flow.json` 已完成。
3. **第三阶段（进行中）：Runtime / Intent Interface** — 设计 LLM 与程序交互方式。LLM 负责语言理解与概念识别，程序负责规则判定；不做计算机视觉、不追踪实时状态，状态依赖问题由 LLM 反问客人。详见 [[interaction-model]]。
4. **第四阶段：Tutorial Tree** — 结构化教程内容，每个节点配 TTS/字幕/关键词。
5. **第五阶段：Controller** — 状态机连接 STT → LLM → Rule Engine → TTS。
6. **第六阶段：UI** — PWA/Flutter 前端，平板作为主要交互入口。

## 项目路径
D:\workspace\board

## 原始讨论
项目根目录下的 `聊天记录.txt` 包含了项目起始时的完整讨论，涵盖架构推演、技术选型、部署方案等全部细节。

**Why:** 这是项目的根本定位和架构基础。后续所有设计决策都以此为出发点。
**How to apply:** 遇到架构选择时，优先考虑"程序确定性"而非"LLM 智能性"。
