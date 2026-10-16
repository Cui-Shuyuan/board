# 动画素材复核修复报告（2026-10-17，R1–R6）

复核依据：`docs/tutorial/animation-reuse-review-20261017.md`。
本报告区分“代码修复完成”与“仍需外部环境完成的验收”。

## 0. 提交

- `5249481 fix(animation): close material review R1-R6`

修复期间保持 Splendor 已审阅 compiled 播放数据不变；迁移前基线对比仍为：除 `source_sha256` 外 **full/quick compiled 0 differences**。

## 1. R1：契约检查位于调用边界

**修复前**：action 后置条件对比整条 cue 的 `end_state`；前置条件只看 cue 开始且遇到之前已改区域就跳过；example facts 只要后面有状态操作就跳过。

**修复后**：

- 编译器新增可选的内存态 `record_event_states`，为每条 cue 记录 **每个源事件后的状态快照**，不改编译产物协议。
- `MaterialUsage` 新增 `event_start/event_count`；展开阶段按真实插入位置计算每个顶层与嵌套调用的稳定事件范围，不依赖事件字典相等或首次匹配。
- `check_materials.py` 用 call 的入口/出口快照检查 pre/post，不再用 cue 终态代替：
  - “创建后转移”合法组合通过；
  - “前一个动作使后一个前提失效”在 B 入口失败；
  - “后续动作修复终态”不能掩盖 A 的中间后置条件错误；
  - `quick_nobles/003` 的 example setup facts 在自己的 setup 边界检查，购买/拿取动作之后不再 NOTE 跳过。

新增测试：`animation/test_check_materials.py` 中的边界组合、前置失效、后续修复、相同调用稳定范围、隔离边界、构建门禁测试。

## 2. R2：嵌套 action 的契约与来源

**修复前**：`_expand_asset` 丢弃嵌套 usage，嵌套契约没有解析结果。

**修复后**：

- 展开结果为完整 usage 树：根 example、嵌套 action、更深层 action 均保留 `material / bindings / timing / contract / call_path / event_start / event_count`。
- `check_materials` 对每个 usage 独立检查契约；父 example 的 facts 不能替代子 action 的 pre/post。
- 支持 example → example → action 的多层组合，错误消息包含完整 `cue -> use -> step -> call_path`。

测试覆盖顶层 action 非法结果、一层/多层嵌套非法结果。

## 3. R3：presentation/action 类别约束

**修复前**：只检查素材自身 `steps[].events`，presentation 可通过 `steps[].use` 间接引入 action 状态操作。

**修复后**：

- 每一级素材自身的 events 按 kind 校验：action 只能状态原语，presentation 只能表现原语，example 自身只能声明状态事实（表现留在 cue/presentation）。
- 依赖边界增加显式组合矩阵：
  - action → action
  - presentation → presentation
  - example → action / example
- 直接、间接、多层嵌套都会在加载期失败，错误指出具体引用链和允许的子类型。

测试覆盖 presentation→action、action→presentation、example→presentation。

## 4. R4：嵌套 namespace 与别名冲突

**修复前**：同一顶层调用下所有嵌套使用共享一个 namespace，两个相同子素材会生成相同 `u1__pin`。

**修复后**：

- namespace 按稳定调用路径生成：`父 namespace + 步骤/use 段 + 子素材`；同素材同父下两次嵌套会得到不同命名空间。
- 不同父素材嵌套同一子素材也不会冲突。
- 同一 cue 内显式 `as/namespace` 规范化后碰撞会直接报错；嵌套 timing 计数器仍属于顶层调用上下文（调用方可以把 marker 列表分给 sibling 嵌套调用），不会跨 cue 泄漏。
- 真实实体身份只由 role/param binding 决定，不因临时 screen/annotation 对象重命名而改变。

测试覆盖同父两次嵌套、不同父嵌套、顶层/嵌套显式别名碰撞。

## 5. R5：QA HEAD 基线包含 library、变化签名覆盖素材

**修复前**：`_load_section_baseline_from_head` 只复制 tracks/shared/sections，不复制新增的 `library/`；HEAD 基线展开后只剩内联事件，无修改也选择 7 条 quick cue。

**修复后**：

- HEAD 基线复制完整依赖闭包：`tracks/ + shared/ + sections/ + library/`，全部通过 `git show HEAD` 读取，不借工作区 material。
- 若 section cue 引用 materials 但 library 缺失，`section_source` 明确失败，不再悄悄生成仅有内联事件的基线。
- QA 变化签名增加 per-cue material signature：material id / kind / variant / mode / bindings / timing / contract / call_path / 素材文件 sha256；共享事实或契约变化会命中受影响调用，未引用素材不会误伤。
- `compile_tutorial` 的 `--validate-qa` 路径传入当前与 HEAD 的 material signature。

实际验证：在无 tracked 修改工作区，`select_qa_changed_ids` 对 full/quick 均返回 `[]`；此前临时修改 `splendor.noble_multi` facts 时只选中 `quick_nobles/003`。

测试：`animation/test_rev04_gate.py` 新增 material signature 变化与文件 hash 变化用例。

## 6. R6：contains count 与事实对象身份

**修复前**：`count` 随 selector 传给 `_item_matches` 导致所有带 count 的 contains 匹配失败；count=0 还会落到 missing-item。

**修复后**：

- `_check_zone_contract` 分离对象属性选择器与数量约束：
  - 无 count：至少一个匹配；
  - count=0：恰好 0 个匹配且不报 missing；
  - count=N：恰好 N 个匹配；
  - 非法 count 类型/负数报错。
- example facts 的 `facts.objects`：
  - 逻辑 id 映射到未占用实际实体 Id；
  - 重复逻辑 id、重复占用同一实际 Id、选择器歧义都会失败；
  - 两个同模板对象通过逻辑 id/facts 可证明对应两个不同实体。

测试覆盖 count 0/1/2、数量不符、两个逻辑 id 对应两个实际实体、同 selector 歧义。

## 7. 核心验收缺口补充

1. **相同事实、不同节奏**：worker toy 新增 `same` track，与 `detailed` 使用相同对象、zone、amount 和资源区，仅 timing/镜头不同；测试断言终点语义状态相等、duration/camera shot 不同。原 `compact`（amount=3）保留为独立参数化测试。
2. **阶段事实与实体身份**：`_check_fact_objects` 已实现逻辑 id → 唯一实际实体绑定；worker fixture 的 material gate 对 detailed/compact/same 三轨全部通过。
3. **时间预算**：`check_materials` 支持 causal order、`timing.min_span`、`timing.min_event_duration`、cue duration 窗口、可选 `timing.conflict_check` 同类实体重叠写检查；新增 section-audio window 诊断：
   - `--strict-timing` 将无 `hold_after_audio` 的音频窗口超出转为错误；
   - 有 `hold_after_audio` 的超出输出 WARN 并标明由播放器 hold 覆盖。
   - 当前严格模式下发现既有 `nobles/source.001` 的两个 action 超出音频窗口，见“剩余事项”。
4. **连续/独立示例**：`mode=isolated` 现在要求 cu 文件显式 `entry`；测试覆盖显式 `entry: initial` 重置后下一条 cue 从空状态开始，证明恢复边界。
5. **构建失效**：source_hash 覆盖材料/stage；check_materials 将磁盘 compiled 与内存重编译结果比较，schema/compiler/timing 导致产物不同会报 stale/divergent；构建门禁测试证明失败时恢复旧 compiled，不把半成品留作最新成功结果。
6. **主流程门禁**：`compile_tutorial._compile_track_with_material_gate` 在 animation compiler 后强制执行 `check_materials`；失败时恢复旧 compiled 并返回非零。Splendor full/quick 的 `--skip-tts` 总控构建已实际跑通。

## 8. 实际验证

- `animation` unittest：**224/224 OK**
- `material_cli validate --game splendor`：14 materials
- `check_materials --game splendor`：full 31 usages / 118 checks；quick 20 usages / 109 checks；0 errors
- `check_materials --game splendor --strict-timing`：发现既有 `nobles/source.001` 2 个无 hold 的音频窗口超出，返回 1（见剩余事项）
- `section_source --check`：full 67 / quick 24
- `compile_animation_v2 --check`：两轨 up to date
- `check_anim_v2` / `validate_anim_rules_v2`：两轨 0 warnings
- `audit_anim_v2`：full 53 cues / quick 17 cues 0 errors
- `check_card_identity_v2`：full 553 states / quick 333 states 0 error
- Splendor compiled 与迁移前基线：除 `source_sha256` 外 0 differences
- `compile_tutorial --skip-tts`：full/quick 成功；纯动画变化 TTS regenerated=0
- 其他：`validate_rules` 0 errors / 72 warnings，`check_no_secrets`、change log、`check_unity_scripts` 通过

## 9. 仍需外部环境 / 人工确认

- **Board API 合法性 QA 未执行**：本机 API 未运行；R1–R6 为 checker/编译链修复，未新增规则事实，但独立裁判层仍未在本轮留档。
- **Unity 采样与真人视觉验收未执行**：包括 `hold_after_audio` 在真实播放器中的实际 hold 时长、音频/动画切点、quick 多贵族节奏。
- **`nobles/source.001` 音频窗口超出**：`--strict-timing` 报 2 个无 hold 的 action 超出现有音频窗口（5.300s vs 5.075s；7.200s vs 5.075s）。为保持已审核播放行为，本轮没有擅自加 `hold_after_audio` 或改动作时序；需用户/作者决定加 hold、调整音频或接受现有切分，并重新视觉验收。
- **严格音频窗口门禁的最终策略**：当前默认构建输出 WARN 并成功，`--strict-timing` 可失败；是否将 `nobles/source.001` 的已知例外修完后把 strict 设为默认，需要内容与视觉复核后决定。
