# 动画素材重构第二轮复核（2026-10-17）

复核 HEAD：`cc1fa58`，包含 `5249481`、`c92e7d6`。

上轮 R1–R6 的修复已有实质进展。本轮对 material library、checker、worker toy、compile tutorial、section source 五个测试模块执行成功，并对两个新的组合边界做了独立最小复现。以下两项应在宣布代码验收完成前修复；不用重做已完成的结构迁移，也不需要修改 Splendor 旁白。

## R7 [P1] 嵌套 example 忽略 variant，生成错误状态且不报错

位置：`animation/material_library.py` 的 `_expand_nested_use`，尤其是对 `nested_asset` 直接调用 `_expand_asset` 以及创建 usage 时写死 `variant=None` 的位置。

顶层 `_expand_use` 会选择 variant 并替换步骤，但嵌套路径没有执行同样的解析。`example -> example` 是当前明确允许的依赖类型，因此这不是不支持的用法。

独立复现：

1. 子 example 的基础 steps 创建 1 个组件，基础 facts 断言 1 个。
2. 子 example 的 `two` variant 创建 2 个组件，variant facts 断言 2 个。
3. 直接调用子 example：正确生成 count=2，usage.variant 为 `two`。
4. 父 example 用 `use: [{ref: 子example, variant: "two"}]` 调用它：错误生成 count=1，子 usage.variant 为 null，加载和展开均未报错。

这不仅改变动作，还使后续 checker 使用基础 facts 而非指定变体的 facts，错误可能被校验当成合法结果。

修复要求：顶层与嵌套调用共用变体选择/参数/步骤解析语义；正确传递选中的 variant 及对应事实，默认变体、缺失变体和非法变体都必须一致处理。不要给嵌套调用增加另一套近似实现。

验收：同一 example 顶层调用、嵌套一层、嵌套两层时，选中相同 variant 的动作和事实一致；非法 variant 必须失败；变体的可省略步骤检查也应基于实际选中的步骤。

## R8 [P1] 严格时间检查使用源事件 dur，漏掉编译器默认时长

位置：`animation/check_materials.py` 的 `_check_timing` 和 `_material_end_time`。

两处都用 `ev.get("dur", 0.0)` 计算结束时刻，但原语在编译时可能赋予默认时长。检查器已有真实事件起点，却没有使用对应的有效持续时间。

独立复现（通过正式 section 源和编译器）：

- 第一个动作在 0 秒创建组件。
- 第二个动作在 0.9 秒执行 `draw`，省略 dur。
- runtime 的此 cue 音频窗口为 1.0 秒，没有 hold。
- 编译产物生成 `flip(at=0.9, dur=0.6)`，cue duration 为 1.5 秒。
- `check_track(..., strict_timing=True)` 仍返回 `(0, [])`。

也就是说，动画实际超出窗口 0.5 秒，严格门禁仍通过。同样的原始 dur 读取也会影响最小时长和重叠检查。

修复要求：由编译器统一提供每个源事件的有效起止时间及来源，或用唯一的规范化语义计算，checker 不再独立猜测默认值。事件展开为多个表现片段时，结束边界必须覆盖实际播放的所有相关片段，不能只取首片段。明确状态即时提交与视觉完成时间的区别。

验收：省略 dur、显式 dur、默认 draw/flip 持续时间以及相关宏展开均覆盖；上述无 hold 场景必须失败；显式 hold 才可按既定策略通过；最小时长/重叠判断与实际编译时长一致。

## 已知音频窗口问题的处理建议

`nobles/source.001` 的最晚动作结束 7.200 秒，音频窗口 5.075 秒，差额至少 2.125 秒。这是已披露的内容时序问题，不应与 R8 的检查器漏检混淆。

先修 R8 以得到可信的窗口诊断，再在 Unity 中确认该 cue 的真实切点。若维持现有动作节奏，可以考虑显式 hold；若强调旁白连贯，则需调整动作安排或口播。不要为了维持静态基线全等而永久接受画面可能被截断。

严格时间检查建议成为最终默认门禁。处理既有例外期间，如确需临时放行，应限定具体 cue、明确原因和待验收状态，避免所有新素材也默认享受宽松模式。这是建议的后续策略，不表示本轮已修改作者选择或播放内容。

## 交付要求

先补能暴露 R7/R8 的行为测试，再修复并运行相关回归。保持已有工作区和 QA 留档；明确报告自动化检查结果、已知音频例外及仍待 Board API / Unity 验收。只有独立执行过的项目才能报告通过。
