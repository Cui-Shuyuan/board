---
name: animation-refactor-todo
description: 动画工具链 / Unity Runtime 代码重构待办（2026-10-07 审查结果）；新会话从这里开工
metadata:
  type: project
---

# 动画代码重构待办

> 基线：`5af7b15`。审查时 Splendor full 83 cue、388 anchors；`compile_animation_v2 --check` 通过；`check_anim_v2` / `validate_anim_rules_v2` / `audit_anim_v2` / `check_card_identity_v2` 全绿；Python 44 条单测通过。
>
> 重构原则：
> 1. 小步提交，一次只做一类；不要顺手改动画数据。
> 2. 动 Python 编译器时每步跑 `compile_animation_v2 --check`，不得改变 `full.compiled.json` 的编译结果。
> 3. 动 C# 前先补纯 .NET 测试；`TimelineModel.cs` / `AnimDefs.cs` 不依赖 UnityEngine，可直接测试。
> 4. 动 Unity 渲染前先留 `dump_anim_v2` 采样或截图基线。
> 5. 不为了重构而重跑/重生成 TTS、compiled 或内容 manifest。

## P0 实际 bug 与去 Splendor 硬编码

### [ ] P0-1 修复 manifest 路径的跨平台判断（第一个做）

- 现象：`python3 animation/compile_tutorial.py --game splendor --track full --dry-run` 当前误报 57 条 cue 文本变化。
- 根因：`content/games/splendor/media/tts/full/tts_manifest.json` 中 57/83 条 `file` 是 Windows `\` 路径；`compile_tutorial.py` 用 `(ROOT / m.get("file", "")).exists()` 判断，WSL/Linux 下必然失败，于是被当成音频缺失。
- 修改点：
  - 读取 manifest 后，把 `file` / `subtitle_file` 的 `\` 统一转成 `/`；
  - 或复用 `build_tutorial_runtime.normalize_relative()` 的逻辑；
  - 加回归测试：带 `\` 的 manifest 路径不应被判为 changed。
- 验收：`--dry-run` 输出 `changed text cues: 0`，且不写任何文件。

### [ ] P0-2 去掉 `compile_tutorial.py` / `qa_anim_ask.py` 里的 Splendor 硬编码

- `animation/compile_tutorial.py:124` delta LRC 写死 `[game:splendor]`、`[track:full]`。
- `animation/compile_tutorial.py:218-219` 生成的 manifest 路径写死 `content/games/splendor/media/tts/...`。
- `animation/compile_tutorial.py:270` QA gate 写死 Splendor 的 `_qa/questions.json`。
- `animation/qa_anim_ask.py:30,104,145` 写死 QA 目录、`game_id=splendor`、两人局说明。
- 修改：`write_delta_lrc` / `run_tts_delta` / `run_qa_gate` 全部显式接收 `game`、`track`；`qa_anim_ask.py` 增加 `--game/--track`，`game_id` 由参数或 `--in` 路径推导；演示局说明改为从游戏配置/profile 读取。
- 验收：Splendor 行为不变；用非 Splendor 假路径单测能生成正确路径。

### [ ] P0-3 游戏专用 audit / ledger 移出通用动画目录

- `animation/audit_anim_v2.py` 实际写死 Splendor：五色宝石、二级/三级容量、贵族数。
  - `GEM_COLORS`、`expected_inventory`、`nobles: int = 3` 等。
- `animation/validate_anim_rules.py` 是 Splendor 2 人局账本，不是通用规则层：
  - `CN`、`GEMS_PER_COLOR`、`LEVEL_CAP`、`FROZEN_FROM` 均为 Splendor 数据。
- 建议：
  - 通用框架留在 `animation/`；
  - 游戏相关规则抽到 `content/games/{game}/tutorial/animation/audit-profile.json`（宝石色、牌堆容量、贵族数等），Audit 按 profile 运行；
  - 或把 ledger 移到 `content/games/splendor/tutorial/checks/`；
  - 保留“独立裁判层”的设计，不要和 compiler 合并。
- 验收：Splendor 结果不变；新增游戏不需要改 animation 通用代码就能跑 audit。

### [ ] P0-4 清理 C# 端重复且已漂移的 source schema

- `clients/unity/Assets/Scripts/Tutorial/Animation/Schema/AnimDefs.cs` 里的 `AnimSchemaV2` 当前 0 引用，且已和 Python schema 不一致：
  - `StateOps` 缺 `set_order`；
  - `PresentationOps` 缺 `hide`、`camera`；
  - 没有 semantic ops。
- 建议直接删除未使用的 source-schema 类和 `AnimSchemaV2` 常量；Unity 运行时只保留 compiled 数据模型。
- 如果确实需要 C# 端 schema，则由 Python schema 生成或加一致性测试，不要手工双维护。

### [ ] P0-5 part anchors 单源化

- 编译器硬编码 `CARD_PART_ANCHORS` / `CARD_PART_SIZES`（`animation/compile_animation_v2.py:123,144`），但 stage 模板里已有 `part_anchors`（如 `splendor.cards_intro.stage.json:143`），两边没有打通，且数值已不一致。
- 建议：编译器按当前 stage template 的 `part_anchors` + 模板 width/height 归一化；全局常量只作为旧数据 fallback。
- 验收：Splendor 的 `prestige` / `cost_1..4` / `bonus` / noble 标注位置不回归；新卡面尺寸可通过 stage 数据覆盖。

## P1 Python 工具链热点

### [ ] P1-1 拆 `compile_events`

- `animation/compile_animation_v2.py:1196` `compile_events` 约 635 行，20 多个 `if/elif op == ...`。
- 建议拆成 `_compile_camera` / `_compile_magnifier` / `_compile_show/hide` / `_compile_transfer` / `_compile_set_face` / `_compile_set_order` 等 handler；保留统一上下文（at/dur/lead/state_ops/pointer_resolution）。
- 保护网：每步 `compile_animation_v2 --check`，编译结果必须逐字节不变。

### [ ] P1-2 拆 `anim_schema_v2.py`

- `_check_event` 264 行、`resolve_track` + `resolve_one` 315 行，混合了 target 归一化、语义宏 lowering、继承解析、契约合并和校验。
- 建议拆成 `normalize.py`（`_normalize_event` / `_lower_semantic_event`）、`inherit.py`（`resolve_track`）、`validate_track.py`。
- 宏 lowering 保持唯一定义；compiler 只消费 lowering 后的 primitive events。

### [ ] P1-3 拆 `validate_anim_rules.run`

- `animation/validate_anim_rules.py:128` 约 270 行，单函数同时做 stack/create/transfer/purchase/贵族/守恒等多类事件检查。
- 建议改成 `Ledger` 类 + 每类事件的 handler，或至少拆成 `run_stack / run_transfer / run_purchase / run_scoring` 等阶段，便于后续移到游戏目录。

### [ ] P1-4 统一 compiled state 回放工具

- 目前多处各自实现：
  - `check_anim_v2.py:113` `apply_state_ops`
  - `check_card_identity_v2.py:65` 自己遍历 state_ops
  - `audit_anim_v2.py:257` `build_state_graph` 自己推状态来源
  - `compile_animation_v2.py:995` compiler 也有一份状态继承判断
- 建议新增共享小模块（如 `animation/compiled_state.py`），只负责：
  - 应用 compiled `state_ops`；
  - 遍历每个可渲染中间状态；
  - 解析 effective state source（entry -> parent -> 前一条 -> initial）。
- 注意：规则断言仍各自实现，不要把 checker 合并成 compiler 的一部分。

### [ ] P1-5 收敛 LRC writer / 旧入口

- `compiler`、`compile_tutorial.write_tts_lrc`、`compile_tutorial.write_delta_lrc`、`tts_doubao.write_tts_lrc` 各有一套 LRC 生成/解析。
- `animation/rebuild_tutorial.py` 与 `compile_tutorial.py` 流程重叠，建议缩成别名或标记弃用。
- 建议统一为一个 LRC 模块，并让 `compile_tutorial` 直接调用 Python 函数，不再 subprocess 自己仓库的脚本。

## P2 C# Runtime 重构

### [ ] P2-1 先给纯 C# 模型补测试

- `TimelineModel.cs` / `AnimDefs.cs` 不依赖 UnityEngine，可单独编入 .NET 测试工程。
- 先给 `TimelineEvaluator.Evaluate` 补帧测试：logical state、camera、move/flip/shuffle、overlay_show/overlay_hide、annotations、magnifier。
- 保护网建立后再拆文件。

### [ ] P2-2 拆 `TimelineModel.cs`

- 当前 1068 行；`TimelineEvaluator.Evaluate` line 265 起约 370 行，混合了逻辑状态、相机、clip 求值、文字标注、overlay、magnifier。
- 拆分为 `EvaluateLogicalState` / `EvaluateCamera` / `EvaluateEntityClips` / `EvaluateOverlays` / `EvaluateAnnotations` / `EvaluateMagnifiers`，文件按类型拆开。
- 顺带把 line 309、531 的 `List.Sort` 改成稳定的显式顺序（at, compiled index），避免同一时间的 clips 顺序不确定。

### [ ] P2-3 拆 `TutorialCuePlayer.cs`

- 当前 1593 行，同时负责内容根解析、音频/导航/seek、字幕、overlay、annotation、magnifier、debug UI。
- 用 partial class 或独立组件拆：
  - `TutorialContentLocator`
  - `CuePlaybackController`
  - `TutorialOverlayRenderer`
  - `TutorialAnnotationRenderer`
  - `TutorialMagnifierRenderer`
- 先做无行为变化的机械拆分，不换 OnGUI 渲染方式；拆分后再考虑是否迁到 UI Toolkit/Canvas。

### [ ] P2-4 收口临时触控层

- `TutorialTouchControls.cs` 自己标注“临时移动端触控控制层”。
- UaaL 正式客户端已经用 Compose，建议明确它只服务 Editor/Desktop 调试，或直接移除。

## P3 工具链与测试缺口

### [ ] P3-1 修 `check_unity_scripts.py` 在当前 WSL 环境的路径失败

- 当前环境（WSL + `/mnt/d` + 指向 `dotnet.exe` 的 `~/.dotnet/dotnet`）运行会报 23 个 `CS1504` access denied，原因是临时工程在 `/tmp`，Windows dotnet 走 UNC 路径访问源码失败。
- 建议：检测 Windows dotnet.exe 后把临时工程放到 Windows 可访问目录，或直接生成 Windows 风格路径；至少在失败时给出明确报错，不要伪装成“编译错误”。

### [ ] P3-2 补 checker / geometry / orchestration 单测

- 当前只有 `test_compile_animation_v2.py`、`test_audit_anim_v2.py`。
- 建议补：
  - `anim_geometry_v2` 的 slot/camera 边界；
  - `check_anim_v2` 的 state_ops/camera/dirty frame；
  - `check_anim_v2_sample` 的契约对账；
  - `compile_tutorial` dry-run 与路径处理；
  - `qa_anim_ask` 的 expect/verdict 解析。

### [ ] P3-3 归档一次性迁移脚本

- `migrate_time_anchors_v2.py`、`migrate_object_targets_v2.py` 已完成迁移使命。
- 可移到 `animation/archive/` 或删除，减少“看起来还在用”的入口。

## 建议执行顺序

1. P0-1（路径 bug）-> P0-2（去 Splendor 硬编码）。
2. P0-3（audit profile）-> P0-4/P0-5（schema / part anchors 单源）。
3. P1-1 -> P1-2 -> P1-4 -> P1-3 -> P1-5。
4. P2-1（C# 测试）-> P2-2 -> P2-3 -> P2-4。
5. P3 按需穿插。

## 每次重构的验收命令

```bash
python3 -m unittest discover -s animation -p 'test_*.py' -v

python3 animation/compile_animation_v2.py --game splendor --track full --check
python3 animation/check_anim_v2.py --game splendor --track full
python3 animation/validate_anim_rules_v2.py --game splendor --track full
python3 animation/audit_anim_v2.py --game splendor --track full
python3 animation/check_card_identity_v2.py --game splendor --track full

python3 animation/compile_tutorial.py --game splendor --track full --dry-run
# 期望：changed text cues: 0
```

## 不要做

- 不要为了“统一代码”把 compiler、ledger、audit 合并成同一套状态机；独立判定层是刻意保留的。
- 不要先拆 C# 再补测试。
- 不要在重构提交里顺手改 TTS / compiled / 动画观感。
- 不要直接删旧数据入口，先 alias / 归档一个版本。
