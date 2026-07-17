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

## 项目路径
D:\workspace\board

**Why:** 这是项目的根本定位和架构基础。后续所有设计决策都以此为出发点。
**How to apply:** 遇到架构选择时，优先考虑"程序确定性"而非"LLM 智能性"。
