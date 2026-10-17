# 规则问答程序接管 04：面向问题的正式证据交付

日期：2026-10-17
依赖：01–03
提交：本阶段独立提交

## 1. 程序实际接管了哪一步

legacy 的 `execute_plan` 结果不再把内部 `PlanItemResult` 原样发给模型。新增 `RuleToolResultProjector` 在程序侧完成：

- 正式证据按稳定 `evidence_id` 放入顶层 `evidence_catalog`；
- query 只携带 `evidence_ids` 引用，不重复发送正文；
- 同一 `(canonical_path, payload_type, content_hash)` 只出现一次，多个 query 保留关联；
- candidate 摘要不带正文；已正式读取的候选通过 `evidence_id` 链接到证据目录；
- 按最终序列化字符数做总预算，不按字符串截断；
- 放不下的证据显式变成 `oversized` 或 `budget_truncated` omission，并附程序生成的 continuation 字段；
- query 的 coverage 在有省略时降为 partial。

发给模型的薄结果形状：

```json
{
  "request_id": "...",
  "game": "testgame",
  "rules_version": "...",
  "queries": [
    {
      "query_id": "...",
      "relation": "explain",
      "resolution": "exact",
      "coverage": "complete",
      "resolved_targets": ["widget"],
      "evidence_ids": ["ev_..."],
      "candidates": [],
      "omissions": []
    }
  ],
  "evidence_catalog": [
    {
      "evidence_id": "ev_...",
      "type": "definition",
      "canonical_path": "widget",
      "rules_version": "...",
      "completeness": "complete",
      "content_hash": "...",
      "content": { "...": "完整正文" }
    }
  ],
  "delivered_evidence_count": 1,
  "omitted_evidence_count": 0,
  "max_pack_chars": 60000,
  "serialized_chars": 1234
}
```

## 2. 模型少做了哪一步

模型不再需要：

- 从 `Matched`、`Related`、payload 和内嵌正文中找哪份才是正式证据；
- 自己按 canonical path 去重并在多个 query 间复制同一份规则；
- 把被截断的半句规则当成完整规则；
- 猜测某个 candidate 是否被正式读取（candidate 带 `read_status` 和 `evidence_id`）；
- 把 `ok` + 空正文当成有数据。

本阶段没有真实模型同轮对照；调用数/效果仍未知。

## 3. 行为测试

新增 `RuleEvidenceDeliveryTests`：

- `Projector_PutsSharedEvidenceOnce_AndLinksBothQueries`
  - 两个 query 引用同一证据时，`evidence_catalog` 只有一份，两个 query 共享同一 `evidence_id`。
- `Projector_DoesNotTruncateLongRuleText_WhenItFitsBudget`
  - 6000+ 字符规则正文完整保留；末尾“每回合至多一次，除非另有说明”仍在。
- `Projector_OversizedAtomBecomesExplicitOmission_NotTruncatedContent`
  - 超出预算的原子证据不进入 catalog，不出现截断正文；
  - omission=`oversized` + continuation=`read_evidence`；
  - query coverage=partial；
  - 最终 `serialized_chars <= 2000`。
- `Projector_KeepsDifferentParameterizedInstancesSeparate`
  - 不同 canonical path、不同参数值的实例不误去重。
- `LegacyToolMessage_UsesThinEvidenceCatalog_DeliveredToModel`
  - 捕获实际发给模型的 tool message；
  - 断言使用 `evidence_catalog/queries/evidence_ids`，不再有旧 `Results/Matched` 顶层字段；
  - 同一正文只出现一次。

测试结果：后端 `249/249` 通过。

## 4. 前后 payload 对照

冻结真实查询（实际实现输出）见 [04-tool-result-comparison.json](04-tool-result-comparison.json)：

| 查询 | 内部扁平结果字符 | 模型可见结果字符 | 变化 | 证据条数 | 省略 |
|---|---:|---:|---:|---:|---:|
| Wingspan `rule_lookup:bird_card` | 14,517 | 13,328 | -8.2% | 5 | 1 |
| Agricola `cost:build_rooms` | 27,697 | 13,563 | -51.0% | 8 | 0 |
| Agricola `flow:harvest_feeding` | 3,204 | 3,400 | +6.1% | 1 | 0 |

- 大包 `cost:build_rooms` 缩减约一半且必要证据完整。
- `rule_lookup` 的候选规则正文本来就多，缩减有限；同时 1 条超出总预算，显式 partial + omission，未截断。
- 小 flow 查询因新增 ID、coverage、evidence 元信息反而略大；这是薄结果结构开销，不是正文重复，报告保留该事实，不宣称所有查询变小。

## 5. 提示与指纹

- legacy candidate prompt 增补 evidence_catalog/evidence_ids、partial/unknown 不可宣称全集、不得引用未交付证据。
- 当前候选 prompt SHA：`6b4c50133d5f08182432fdf02ccdadc1557e95230d19adc864248ef6aaea5fe1`。
- tool schema 本阶段未改，指纹仍为 `75a96123cdf2`。
- 冻结 `a992cfe/ffac498` prompt SHA `859260e5…f1343d5` 未被冒用。

## 6. 剩余不确定性

- continuation 目前是程序生成的显式区域/实体/类型描述；模型能否直接执行续读、如何防止被篡改和跨 snapshot 复用，要在 05 请求账本里验证。
- 总预算使用最终 JSON 字符数，不是模型 token；真实 token 仍需 provider usage 数据。
- 规则正文中大量中文在本轮日志/序列化中按未转义 JSON 计字符，与 provider 实际 token diff 未知。
- `rule_lookup` 的候选规则正文可能很大，预算会主动变成 partial；这是否影响最终回答需真实模型实验。
- 本阶段只验证 legacy 工具路径；program 内部仍通过现有证据 pack builder 消费 PlanItemResult，未切到新投影。
