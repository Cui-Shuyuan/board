# 规则问答程序接管史诗：最终状态（2026-10-17）

## 结论

本分支完成了 01–05 的确定性实现、行为测试、薄结果交付和请求账本，并生成了 06 的预注册与能力矩阵；但**没有完成最终真实模型同轮验收**。因此：

- 不宣称史诗全部完成；
- 不宣称默认生产版本已升级；
- 不宣称正确率或延迟改善；
- 不把 0 次真实聊天运行写成通过。

分支：`feat/rules-runtime-takeover`
代码基线：`ffac498`（冻结 legacy 基线）
工作区原 HEAD：`578d072`（仅文档修订）

## 阶段提交

| 阶段 | 提交 | 交付 |
|---|---|---|
| 01 | `d1ff8e8` | 统一 resolution/coverage/payload 契约、证据消费修复、诊断归因、timeline |
| 02 | `3b71d97` | canonical flow index、local/game scope、成员枚举、分页、冻结 JSON |
| 03 | `9724b8d` | rule_lookup、condition/cost/target 依赖导航、compute 边界 |
| 04 | `80c550a` | legacy 薄证据交付、去重、完整性、总预算、捕获工具消息测试 |
| 05 | `abb58ae` | 请求账本复用、submit_answer 终态、证据 ID 校验、候选门禁移除 |
| 06 | 本报告所在提交 | 133 题恢复、预注册、能力矩阵、跨游戏检查脚本、最终状态 |

## 程序实际接管了什么

1. **结果形态**：query 显式给出 resolution/coverage/payload，目录成功不再因 `Matched` 为空被判空。
2. **流程导航**：程序按 snapshot 建 canonical path 索引，完成 local/game scope、同名局部节点消歧、直接子步骤、父循环/条件/顺序读取。
3. **成员枚举**：只从声明集合或 schema `specifies/extends/instance_of` 产生成员；description 提及不当 membership；第一页不宣称全集。
4. **规则查找**：对象 exact identity 与 rule intent 并存；程序自动正式读取最多 3 条候选，但候选读取不升级为 identity confirmed。
5. **依赖读取**：condition/cost/target 按 schema 字段读取，显式引用展开一层定义，不把 description 提到推断成收益/费用；缺失与预算裁剪显式记录。
6. **计算边界**：复用既有容量/计分/数量事实服务；缺公式/参数返回 partial/unsupported，不猜人数/回合。
7. **交付**：正式正文只进 evidence_catalog 一次，query 只引用 evidence_id；不切断长字符串；原子证据放不下就 oversized + continuation。
8. **调度**：相同规则版本 + 相同规范化 plan 不重复底层执行；终态 submit_answer 在同一模型调用提交并校验 evidence id；不因 unresolved 候选机械追加确认轮。
9. **可观测性**：模型/tool spans、token、工具复用、交付字符、候选自动读取、协议错误进入 trace；时间线区分区间并集和重叠。

## 模型少做了什么（确定性证明）

- 不必从 `Matched/Related/payload` 里猜正式证据，也不必复制同一正文到多个 query。
- 不必为了“候选已读”再发同 ID explain；候选阅读与身份解析分开。
- 不必自己拼流程父子/循环/分页；模型只提交 scope/entity/include/offset/limit。
- 不必在错误协议上无限重试；有界修复和总轮次上限由程序控制。
- 同一请求重复 plan 时，模拟轨迹显示模型仍调用，但程序实际工具执行从 2 降到 1（`05-orchestration-traces.json`）。
- submit_answer 终态不追加检查调用；模拟轨迹是 2 次模型调用（1 查询 + 1 终态）。
- 合法 clarify 可无 evidence 结束；伪造 evidence id 会触发有界修复。

## 行为测试与同轮数据

### 已执行

| 命令 | 结果 |
|---|---|
| `dotnet test backend/BoardAI.Api.Tests/BoardAI.Api.Tests.csproj` | `256/256` 通过 |
| `python3 -m unittest discover -s tools/eval -p 'test_*.py'` | `10/10` 通过 |
| `python3 tools/content/validate_rules.py --errors-only` | `0 errors / 72 warnings` |
| `git diff --check` | 通过 |
| Android `./gradlew testDebugUnitTest` | **未执行成功**：环境缺 Android SDK / `sdk.dir` |

说明：后端 256 个测试中曾偶发既有 `SnapshotCache_WhenContentChanges_ReleasesOldSnapshotForGc` GC 断言失败；多次重跑通过，未修改该测试。Android 失败是环境配置缺失，不写成通过。

### 同轮/冻结数据

- [02-frozen-query-contracts.json](02-frozen-query-contracts.json)：Agricola local flow、Wingspan member 枚举、catalog 分页实际输出。
- [03-frozen-query-contracts.json](03-frozen-query-contracts.json)：Agricola cost、Wingspan rule_lookup 自动读、Agricola compute 实际输出。
- [04-tool-result-comparison.json](04-tool-result-comparison.json)：同一查询内部扁平结果 vs 模型可见结果；大 cost 查询 27,697→13,563 字符，rule_lookup 14,517→13,328 且 1 条超出预算显式省略，小 flow 3,204→3,400（元信息开销，未夸大为缩小）。
- [05-orchestration-traces.json](05-orchestration-traces.json)：三组模拟模型轨迹，均 `IsReconciled=true`。
- [06-capability-matrix.json](06-capability-matrix.json)：9 款游戏的 runtime 能力/缺口。
- [06-pre-registration.json](06-pre-registration.json)：最终门槛、题集、预算和未完成原因。

### 最终 1,014 次真实聊天

**执行 0 次。** 没有合法的新封存 36 题集（实现前未建立），也没有运行所需的外部模型/双盲评审环境。任何“质量提升”“延迟下降”“默认可切换”的结论都没有数据支撑。

## 九款游戏能力概况

- scoped flow：9 款中 8 款有 flow；`seasons` 缺 flow。
- 成员依据：9 款均有显式集合或 schema 类型关系。
- 结构化 condition/cost/target：9 款中 4 款当前探测到。
- score_table：9 款中 5 款当前探测到。
- unresolved refs：9 款中 8 款存在需人工分类的 `<...>`；不能全部当规则缺失。
- duplicate flow local ids：`civolution`、`wingspan` 需要 canonical path；本实现已按路径区分，但这使旧局部 id 查询可能返回候选。
- 规则正文-only：很多规则只能解释、不能确定性计算；这是能力边界，不是本轮失败。

详细见 [06-capability-matrix.md](06-capability-matrix.md)。

## 06 未通过/未执行项

1. **新封存 36 题缺失**：实现前未建立，现在创建也无法恢复盲测资格。需要另起真正封存流程。
2. **真实同轮聊天 0 次**：1014 次预算完全未消耗；质量、错误规则、P95 延迟、token 成本均未知。
3. **双盲 judge 未运行**：只有确定性测试和模拟轨迹。
4. **Android/TTS 冒烟未运行**：环境缺 Android SDK；本阶段没有新增 Android 协议字段，但仍需真实端到端。
5. **continuation 执行未闭环**：continuation 已生成且带路径/范围，但尚无服务端不可伪造 token + 请求账本续读执行；当前仍是已知缺口。
6. **program 路径未重写**：符合“不另起完整问答架构”的约束；program 仍为实验路径。
7. **默认发布决策未做**：默认 `OrchestrationMode=legacy` 未改；当前分支只是候选实现，未合并/部署/宣称切换。

## 不确定性

- 模型可能继续用 plain final，而不调用 submit_answer；需要真实运行确认。
- evidence ID 校验只证明出处存在与已交付，不证明答案文本被规则蕴含。
- 工具 span 仍不能细分搜索/正式读取/装配；只到 tool 总时长，进一步细化依赖 04 交付器统计继续扩展。
- 预算按 JSON 字符计，不等于 provider token；真实输入 token/价格未知。
- 成员“可证明完整”只相对当前录入数据与 schema 成立，不证明规则书未录入成员存在。
- 规则库中陈旧/缺失引用需要内容治理，不能靠 runtime 关键词或游戏专用代码修补。

## 运行入口

```bash
cd /mnt/d/workspace/board
/mnt/d/dotnet/dotnet.exe test backend/BoardAI.Api.Tests/BoardAI.Api.Tests.csproj --no-restore
python3 -m unittest discover -s tools/eval -p 'test_*.py'
python3 tools/eval/rules_capability_report.py --out docs/reviews/rules-runtime-takeover/06-capability-matrix.json
python3 tools/eval/compare_versions.py --questions <133+36-jsonl> --old-api <baseline> --new-api <candidate> --out <run-dir> --repeats 3 --pre-registration docs/reviews/rules-runtime-takeover/06-pre-registration.json --budget-chats 1014
python3 tools/eval/combine_judges.py ...
python3 tools/eval/analyze_compare.py ...
```

## 继续推进的前置条件

1. 先建立真正封存的 36 题（9×4），在运行前冻结问题、可接受证据组合、语义判分标准和条件式答案。
2. 准备基线与候选两个可运行 API 实例，同规则/索引/模型/Thinking/采样，交错随机运行 1014 次并有重试/缓存/token/时间线记录。
3. 双盲 judge + 人工复核；确认答案正确性不降、无新增系统性错规则、P50 至少 -10%、P95 不恶化超过 5%、机械确认与重复底层查询下降。
4. 跑 Android JVM 与真机/ TTS 冒烟。
5. 只有全部通过后才更新默认发布声明；否则保留已验证的 01–05 确定性节点并列出未达标能力。
