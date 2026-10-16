# 动画素材重构第二轮复核响应（R7–R8）

复核依据：`docs/tutorial/animation-reuse-review-round2-20261017.md`。
本轮只修 R7/R8 与相关回归，不改 Splendor 旁白、已审核动作时序或 compiled 播放数据。

## 0. 提交

- `ab40dc3 fix(animation): resolve nested example variants and effective timing ends`

回归证据仍为：Splendor full/quick compiled 与迁移前基线比较，除 `source_sha256` 外 **0 differences**。

## 1. R7：嵌套 example 使用同一套 variant 语义

**修复前**：顶层 `_expand_use` 会解析 `variant` 并替换 steps；嵌套 `_expand_nested_use` 直接展开 `nested_asset.steps`，且 usage 写死 `variant=None`。子 example 的 `variant: "two"` 被忽略，动作和 facts 都退回基础步骤。

**修复后**：

- 新增统一的 `_resolve_use_variant_steps(asset, use, where)`：
  - 顶层与嵌套调用共用同一套默认 variant / 显式 variant / 非法 variant 解析；
  - 只有 example 接受 variant；presentation/action 传 variant 的报错与顶层一致；
  - 返回实际选中的 steps，omit/status 校验基于实际选中 steps，而不是被替换掉的基础 steps。
- 顶层 `_expand_use` 与嵌套 `_expand_nested_use` 都使用该 helper。
- 嵌套 usage 现在正确写入 `variant`，后续 `check_materials` 使用对应 variant facts。
- example → example → action 多层调用同样保持 variant。

**验收测试**：

- `test_nested_example_variant_matches_top_level_semantics`：
  - 同一 example 直接调用、嵌套一层、嵌套两层全部选中 `two` variant；
  - 动作事件 count 一致；
  - 非法 variant 在嵌套路径报错；
  - variant 的 optional 步骤可 omit，基础 steps 的 step id 不会被错误当成可 omit。

## 2. R8：严格时间检查使用编译器有效起止时间

**修复前**：`_check_timing` 与音频窗口检查用 `ev.get("dur", 0.0)` 猜测结束时间，忽略编译器为 `draw`/`flip` 等宏赋予的默认时长；省略 dur 的 draw 实际播放 0.6s，严格窗口检查却按 0s 通过。

**修复后**：

- 编译器在 `record_event_states` 内存态下额外记录每条 cue 每个源事件的 `event_end_times`。
- 单事件的实际结束边界取以下最大者：
  - 声明起点 + `lead` + `dur`（与 compile cue duration 的基础模型一致）；
  - 该事件新生成的 clips 中最晚的 `at + lead + dur`；
  - 该事件新生成的 state_ops 中最晚的 `at`（包含 destroy 延时等）。
- `check_materials` 不再读取源事件 `dur` 计算结束：
  - 因果顺序使用编译后起点；
  - min_span / min_event_duration 使用编译后有效起止；
  - cue/audio window、重叠冲突使用实际区间 `[start, effective_end]`；
  - 音频窗口诊断能看见省略 dur 的宏展开。
- 状态即时提交与视觉完成时间不再混淆：状态写入用 state_ops 时刻，视觉完成用 clips 终点，最终边界取二者最大值并计入声明时长兜底。

**验收测试**：

- `test_omitted_draw_duration_is_seen_by_strict_audio_window`：
  - 0s 创建组件，0.9s 执行省略 dur 的 `draw`；
  - 编译器生成 0.6s flip，实际结束 1.500s；
  - cue 音频窗口 1.000s 且无 hold；
  - 默认检查输出 WARN，`--strict-timing` 正确返回 1 并报 1.500s 超窗。
- `test_compiler_event_end_times_include_default_and_explicit_durations`：
  - 省略 dur 的 `draw` 有效时长 0.6s；
  - 显式 `dur: 1.25` 的 `draw` 有效时长 1.25s；
  - 省略 dur 的 `flip` 有效时长 0.6s。

## 3. 已知音频窗口例外（复测后）

`--strict-timing` 现在发现 `nobles/source.001` **两个**源事件实际结束超出该 cue 音频窗口（无 hold）：

- `nobles/source.001` 的 `draw_market_replacement`：结束 5.300s，窗口 5.075s；
- `nobles/source.001` 的 `claim_noble`：结束 7.200s，窗口 5.075s。

这与上一轮汇报的 action 归属略有变化，是 R8 修正为“按实际 clips/state_ops 边界”后的准确结果。
quick 的 002/003 超出均由已有 `hold_after_audio` 覆盖，严格模式输出 WARN 不报错。

`nobles/source.001` 仍是 **待内容/视觉决策**：

- 若维持动作节奏：需要显式 `hold_after_audio`；
- 若强调旁白连贯：需要调整动作或口播；
- 本轮没有擅自修改，因为可能改变已审核画面切点。

## 4. 本轮实际验证

- `animation` unittest：**227/227 OK**
- `material_cli validate --game splendor`：14 materials
- `section_source --check`：full 67 / quick 24
- `compile_animation_v2 --check`：两轨 up to date
- `check_materials --game splendor`：full 31 usages / 118 checks；quick 20 usages / 109 checks；默认 0 errors
- `check_materials --game splendor --strict-timing`：1 个已知 `nobles/source.001` 例外，返回 1
- `check_anim_v2` / `validate_anim_rules_v2`：两轨 0 warnings
- `audit_anim_v2`：full 53 cues / quick 17 cues 0 errors
- `check_card_identity_v2`：full 553 states / quick 333 states 0 error
- `compile_tutorial --skip-tts`：full/quick 成功；TTS regenerated=0
- Splendor compiled 与迁移前基线：除 `source_sha256` 外 0 differences
- `validate_rules`：0 errors / 72 warnings
- `check_no_secrets`、change log、`check_unity_scripts`：通过

## 5. 仍需外部 / 人工验收

- **Board API 合法性 QA 未执行**：本机 API 未运行。
- **Unity 采样 / 真人视觉验收未执行**：尤其是 `nobles/source.001` 在真实播放中的切点，以及 quick 002/003 的 `hold_after_audio` 实际 hold 时长。
- **`nobles/source.001` 内容决策**：加 hold / 调时序 / 接受现有切分，三选一，并重新视觉验收。
- **严格时间门禁默认策略**：R8 修完后诊断已可信；建议处理完上述例外后，再将 `--strict-timing` 接入默认构建。

## 6. Board API 合法性 QA（后端已启动后补跑）

后端在 Windows 主机 `127.0.0.1:5000` 监听；WSL 无法直达 Windows loopback，因此使用 Windows Python `D:\Python\Python312\python.exe` 和 `BOARDAI_API=http://127.0.0.1:5000/api/chat` 执行 `qa_anim_ask.py`。QA 留档按约定不入 Git。

### quick

```bash
python animation/qa_anim_ask.py --game splendor --track quick --jobs 4 --tag round2-quick
```

结果：**10/10 通过**，0 可疑。

### full

```bash
python animation/qa_anim_ask.py --game splendor --track full --jobs 4 --tag round2-full-v2
```

结果：43 问，**40 通过 / 3 可疑**：

1. `take_gems/limit.001.2`
   - 手写问题期望：`不合法`（A 手上无钻石却选择还 2 枚钻石）。
   - LLM 回答：“合法，还哪几种颜色完全由你自己选。”
   - 定向重跑仍为“合法”。这是本 cue Board API 独立裁判层的**真实失败**，不是解析器误判。
2. `setup_gems/003.1`
   - 期望：`有问题`（不能先按四人局摆 7 颗再收回）。
   - 本次回答“合法”；定向重跑回答“有问题”。判定不稳定，问题本身也有“后面会收回”造成的歧义，建议人工重写手写问句或明确“当前设置瞬间的状态”。
3. `nobles/repeat.001.1`
   - 期望：`合法`（下回合结束时补拿最后一块贵族）。
   - 本次回答“有问题”；定向重跑的两个同名问题均回答“合法”。属于 LLM 解释不稳定。

定向重跑：

```bash
python animation/qa_anim_ask.py --game splendor --track full \
  --only take_gems/limit.001.2,setup_gems/003.1,nobles/repeat.001.1 \
  --jobs 1 --tag round2-retry
```

结果：4 问中 3 通过，唯一持续失败仍是 `take_gems/limit.001.2`。

### QA 元数据修正

跑 QA 时发现两条既有负向题只写问题、没有 `expect`，脚本因此把正确负答误报为可疑：

- `take_gems/limit.001.2` 增加 `"expect": "不合法"`；
- `setup_gems/003.1` 增加 `"expect": "有问题"`。

只补期望词，未改问题文本、动画事件或旁白。补完后 full QA 能正确把这些题纳入通过/失败判定，也暴露出上述真实模型失败。

**结论**：Board API 层已实际执行，不是“未运行”。quick 全绿；full 仍有 `take_gems/limit.001.2` 的独立裁判失败，以及两条不稳定/歧义问题。因此不能宣称 Board API QA 全部通过；需要人工决定是修问题措辞、修模型侧提示，还是接受该负向题由人工复核。
