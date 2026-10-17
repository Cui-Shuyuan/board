# 01 — 统一查询结果契约，修正诊断与计时

依赖：无。目标：后续优化使用可消费的工具结果和可信基线；本阶段不改变回答策略。

## 修改入口

- `backend/BoardAI.Api/Services/GameRulesService.Models.cs`
- `RulesPlanService.cs`、`RuleEvidencePackBuilder.cs`、`ChatOrchestratorService.cs`、`ProgramControlledChatOrchestrator.cs`（均在上述 Services 目录）
- `tools/eval/analyze_evidence_layers.py`、`diagnose_unavailable.py`、`compare_versions.py`、`analyze_compare.py`
- 现有 request trace / LLM service 计时代码；先定位实际调用点。

## 工作包 A：冻结基线

保存 `ffac498` legacy 的代码、规则快照 hash、索引版本、实际 system messages/tool schema 指纹、模型/Thinking 配置、历史裁剪和生成参数。新实现与这一基线同轮比较；`a992cfe` 只作历史核对。

留出集、开发集、历史报告分开。报告明确 31 题不是总体随机样本，之前的单一 expected target 命中不是答案正确性的充分条件。

## 工作包 B：可区分的结果形态

梳理 `PlanItemResult` 的全部实际产出分支与消费者，至少覆盖 Matched、Related、FlowContext、Catalog、Facts、Condition/Cost/Target、Siblings/Loop/Boundary、Candidates 和各错误状态。以表格列出“谁生成、谁消费、身份来源、空值含义”。

在内部引入有类型的 payload，不要求保留当前扁平字段。建议最小契约如下，具体 C# 命名可按仓库习惯调整：

```text
QueryResult
  request_id, query_id, game, rules_version
  resolution: exact | candidates | not_found | unsupported | invalid
  resolved_targets: [canonical_path]
  payloads: Definition | CatalogPage | FlowSlice | RelationFacts | ComputedFact
  coverage: complete | partial | unknown
  omissions: [{reason, canonical_path?, field_path?, continuation?}]
  candidates: [{canonical_path, label, score, reason}]
```

- resolution 描述目标解析；coverage 描述此次请求范围的数据覆盖，**不是“回答一定充分”**。
- CatalogPage 是目录页，不自动等于“某类别的全部成员”。集合枚举由计划 02 补充，不能为修复丢字段而改造事实含义。
- 每种正式 payload 必须有来源及版本；候选摘要没有正式证据身份。
- 已完成且为空的合法集合，与未支持/未找到/超预算必须区分。
- 消费端穷举处理 payload 类型。未知类型明确报 unsupported/记录诊断，禁止 `ok + 丢弃正文` 静默流转。
- 兼容现有 program 消费是适配工作，不重写其编排，不据此扩大主线。

## 工作包 C：修正归因

拆开这些可观测层，不用一个 hit 字段混合：请求目标、精确解析结果、检索候选、正式读取、交付给模型、最终引用、答案评审。

路径匹配使用 `(game, rules_version, canonical_path)`。禁止去掉 ontology 前缀或只用末段 slot 匹配。历史数据缺规范身份时标为 unknown，不补造精确命中。评测允许人工维护一组可接受的证据组合，保留实际答案评审；这些组合不得进入线上检索逻辑。

最早失败定位顺序：

1. 已交付可接受证据：不得再归因“候选未命中”；回答失败留待区分证据仍不充分/表达/决策。
2. 已正式读到而未交付：装配/预算阶段。
3. 已解析或候选命中而未正式读取：导航/读取阶段。
4. 完整可观测记录显示定位未命中：定位阶段。
5. 观测缺失或 expected 标注不足：unknown，保留人工检查入口。

修正旧脚本后重新分析已有产物，不重跑聊天。新增一份纠正报告，不覆盖原报告；无法复原的信息明确 unknown。

## 工作包 D：性能时间线

每次请求使用单调时钟记录 span：模型每次调用、搜索/向量化、正式读取、引用展开、装配/序列化、协议修复、补查、等待/重试。记录输入输出 token、工具结果字符数、cache hit、实际底层读次数。

- 串行请求检查 span 与总时长能对账；并行 span 不能直接相加冒充总耗时。
- 区分 API 聊天完成与语音首音频/播放完成；TTS 不混入聊天对照指标。
- 服务商不给 reasoning token 或首 token 时长时标为 unavailable；响应 headers 时间不是首 token 时间。
- 日志的完整审计元信息不必全部发给模型。

## 行为测试与验收

必须先复现再修复：

- `list` 成功含 Catalog，消费者得到 CatalogPage，不能得到空证据成功。
- exact hit 无 Candidates，正式读取/交付命中不能被判检索失败。
- 两个 owner 的同名 slot 不互相命中；ontology 与游戏同名 id 隔离。
- target 已交付但被拒答，分类保留 respond/sufficiency 或 unknown。
- 完整空集合、缺失字段、预算裁剪、未知 payload 各有不同状态。
- 用模拟延迟验证时间线对账与并行统计，不用真实 API 的抖动写脆弱耗时单测。

运行后端测试与修改过的评测脚本测试。输出结果契约表、修正后的旧数据分析、基线清单。除明确修复的漏消费外，不得把本阶段包装成回答正确率提升。

完成后进入 02；若无法规范解析某类 payload，明确列出并在该分支停止静默成功，不允许先丢弃再推进。
