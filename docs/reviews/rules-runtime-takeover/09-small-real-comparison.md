> 历史路径说明（2026-10-23）：本文保留当时本地 scratch 路径 `.tmp/...`，这些不是当前运行依赖。新实验请使用 `$BOARD_RUNTIME_ROOT/tmp/...`；2026-10-23 之前的原始 agent/eval 产物已压缩到 `D:\board\archive\agent-workspace-evidence-20261023-004025.tar.zst`，需要时解压后按原相对路径读取。

# 规则问答程序接管：31 题小规模真实同轮对照

日期：2026-10-17
分支：`feat/rules-runtime-takeover`
候选提交：`59574e3`
基线提交：`ffac498`
运行环境：Windows 本机 Board API ×2 + Qdrant；DeepSeek `deepseek-v4-flash`，Thinking=`low`；两版共用同一规则内容与 Qdrant。

## 1. 运行配置

- 开发集：`tools/qa/program_evidence_repair_dev_questions.jsonl`，31 题。
- 基线：`ffac498`，API 端口 5001。
- 候选：`59574e3`，API 端口 5002。
- 每版每题重复 3 次，候选/基线同轮交错，总聊天 **186/186**。
- `pair-workers=1`，避免并发竞争污染延迟。
- HTTP 错误 0；重试没有覆盖原始 HTTP 失败；judge 错误 0。
- 原始 runs/judge 在忽略目录 `.tmp/rules-runtime-takeover-small-final/comparison/`；仓库保存 manifest、judge 共识摘要和自动分析摘要。

## 2. 答案质量

双盲 judge：`deepseek-v4-flash` + `deepseek-chat`，保守共识（两者都 pass 才算 pass）。

| 指标 | 基线 ffac498 | 候选 59574e3 | 差值 |
|---|---:|---:|---:|
| run 级共识严格通过率 | 59.1% | **67.7%** | **+8.6pp** |
| 问题多数票通过率 | 64.5% | 67.7% | +3.2pp |
| 错误规则/编造率 | 36.6% | 29.0% | -7.5pp |
| 不必要追问率 | 0.0% | 0.0% | 0 |
| 无关扩展率 | 9.7% | 6.5% | -3.2pp |
| 人数反推率 | 1.1% | 0.0% | -1.1pp |
| direct expected hit（语义不等价） | 61.9% | 88.1% | +26.2pp |

配对分析：

- 每次运行通过率差值均值：**+8.6pp**
- 问题级 Bootstrap 95% CI：**[-3.2pp, +20.4pp]**
- McNemar：旧版单独通过 2，新版单独通过 3，p=1.0

### 预注册门槛判断

- 点估计不低于基线：**通过**（+8.6pp）。
- 配对 CI 下界高于 -3pp：**未通过**；下界为 -3.23pp，差 0.23pp。
- 新系统系统性错误复核：错误规则率点估计下降，但仍有具体回归，不能宣称完全通过。

因此本轮结论是：**质量方向为正，但统计保护边界未严格通过，不能据此进入完整 1,014 次实验或宣称生产默认升级。**

### 已观察到的质量回归/风险

- Seasons“水晶怎么获得”：新候选 0/3 共识通过，旧版 3/3；主要新增了证据未直接支持的“卡牌也能直接给水晶”和终局计分扩展。两条 judge 对部分轮次分歧，但这一题是明确待查。
- Ark Nova 终局计分卡：新版 1/3，旧版 2/3；新版某些回答加入未支持细节。
- Agricola 家庭成长：新版 2/3，旧版 3/3；其中一版把条件表述得不够准确。
- Wingspan 打鸟：新版 2/3，旧版 3/3；一版额外声称蛋的来源，证据未支持。
- 这些是需要下一轮定位的具体新增内容错误，不能用总通过率提升抵消。

## 3. 协议与工具使用

候选 93 次 run：

- 终态结果：answer 87、clarify 4、unavailable 2。
- 协议修复模型调用：31/93 run 发生（**33.3%**）。
- 协议修复调用耗时：发生修复的 run 平均 **1,110.6ms**（中位 1,090.1ms）。
- 最终 `terminal_protocol_failed`：**0**。
- 普通 final 被拒绝后都能在有界修复内完成终态。
- 合法 clarify 4 次，judge 不必要追问率为 0；unavailable 2 次均为负例/未覆盖对象，judge 通过。

工具/证据：

- 实际工具执行均值：1.247 次/run。
- 相同 plan 复用：本轮 0 次；本轮模型没有出现完全相同的重复 plan。
- 候选自动正式读取均值：2.54 条/run。
- 交付证据字符均值：9,768.9。
- continuation handle 实际使用：3/93 run。
  - 2 次来自 Wingspan 先手标记分页，1 次来自 Seasons 水晶分页；
  - 3 次都从第一页进入下一页并取得新证据；
  - 示例 Seasons：第一页自动读 `transmutation_chart/crystal_track/sorcerer_token`，续读取得 `energy_stockpile/energy`，coverage=complete；
  - 示例 Wingspan：第一页读先手标记相关候选，续读取得 `player_setup/round_end/setup`，offset 继续到 6；
  - 未观察到续读重复第一页。
- `read_evidence` 续读本轮未被模型实际使用；其执行路径由确定性测试覆盖。

## 4. 耗时归因

整体延迟（run 级）：

| 指标 | 基线 | 候选 | 变化 |
|---|---:|---:|---:|
| P50 | 3,594ms | 4,009ms | **+11.5%** |
| P95 | 8,010ms | 6,676ms | **-16.7%** |
| 配对均值差 | - | - | -6.5ms |
| 配对中位差 | - | - | +443ms |

预注册性能门槛：

- P50 至少降低 10%：**未通过**（反而 +11.5%）。
- P95 不增加超过 5%：**通过**（-16.7%）。

候选程序自耗时（`timelineCategories` 的 self，已扣除子 span）：

| 类别 | 均值 |
|---|---:|
| 模型调用 self | 3,592.6ms |
| search self | 78.4ms |
| read self | 394.7ms |
| assembly self | 2.7ms |
| serialization self | 0.3ms |
| navigation self | 0.6ms |
| 请求 trace elapsed 均值 | 4,099.4ms |

解释：

- 模型调用约占 trace elapsed **87.6%**；程序内部 search/read/assembly/serialization/navigation 合计约 **11.6%**。
- Program 内部主要成本是正式读取 394.7ms，其次是检索 78.4ms；assembly 自身只有 2.7ms，没有再把包含子调用的 assembly 100ms 与 search/read 70ms 重复解释。
- 协议修复是候选额外成本：在有修复的 1/3 run 中约 1.1s，直接拉高 P50，P95 反而因长尾改善而下降。
- `model_self` 包含所有模型调用，包括修复模型调用，因此修复调用最终成功提交 answer 时仍被统计。

## 5. 结论与下一步

本轮：

- 意图排序、终态拦截、continuation 分页和 self-time 归因均在真实开发集上被实际触发；
- 候选质量点估计优于基线，错误规则率、无关扩展、人数反推均下降；
- 但质量 CI 下界未过 -3pp，P50 未降低 10%，仅 P95 达标；
- 因此不进入完整 1,014 次实验，不切默认发布，不宣称史诗完成。

下一步应只针对本轮暴露的问题：

1. 协议修复率 33.3% 和单次修复约 1.1s：定位是提示/终态 schema 负担还是模型习惯，目标是减少首次 plain final，而不是截断合法输出。
2. Seasons 水晶、Ark Nova 终局计分、Wingspan 打鸟等新增无支持细节：检查对应 evidence 交付是否缺约束，优先修证据完整性/提示，不针对题目写答案。
3. 比较时继续保持 31 题小开发集，不碰新封存的 36 题；若据此调参，仍作为开发数据。

原始产物路径：

- `.tmp/rules-runtime-takeover-small-final/comparison/runs.jsonl`
- `.tmp/rules-runtime-takeover-small-final/comparison/judge-*.jsonl`
- `.tmp/rules-runtime-takeover-small-final/comparison/judge-consensus.jsonl`
- `.tmp/rules-runtime-takeover-small-final/comparison/analysis/`
