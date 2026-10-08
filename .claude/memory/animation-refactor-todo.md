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

## P0 验收记录（2026-10-07）

结论：**P0-1 ~ P0-5 全部通过当前工作区验收**；实现代码尚未提交，本轮工作区即验收对象。

- P0-1 通过：
  - `python3 animation/compile_tutorial.py --game splendor --track full --dry-run` → `changed text cues: 0`。
  - 83/83 manifest 路径经 `/` 归一化后文件均存在；新增 `test_compile_tutorial.py` 回归。
  - 注意：非 dry-run 跑一次会顺带把 manifest 里的旧 `\` 路径写回为 `/`（本次验收已产生该差异，属预期归一化）。
- P0-2 通过：
  - `compile_tutorial.py` / `qa_anim_ask.py` 里只剩文档示例中的 `splendor`，业务路径已全部参数化。
  - `test_compile_tutorial.py` / `test_qa_anim_ask.py` 覆盖 game/track、Windows 路径、game 自动发现与 profile。
- P0-3 通过：
  - Splendor 专用规则落到 `content/games/splendor/tutorial/checks/ledger.py`；
  - audit 词汇落到 `content/games/splendor/tutorial/animation/audit-profile.json`；
  - `validate_anim_rules_v2` 动态加载 game ledger，Splendor 结果仍为 83 cues / 0 warnings；
  - 新增非 Splendor synthetic profile 测试，audit 引擎无需改代码。
- P0-4 通过：
  - C# 未使用的 source-schema 类与 `AnimSchemaV2` 已删除，Unity 只保留 compiled 数据模型；
  - `python3 tools/ops/check_unity_scripts.py --json` 在 `TMPDIR` 指向 D: 盘时通过：23 个 C# 文件 / 0 errors。
- P0-5 通过：
  - compiler 现在优先从 stage template 的 `part_anchors` 解析锚点/尺寸，常量仅作 legacy fallback；
  - `compile_animation_v2 --check` 仍通过，`full.compiled.json` 未变；
  - 新增 stage part anchors / screen overlay template 覆盖测试。
- 验收回归：`python3 -m unittest discover -s animation -p 'test_*.py'` → 58 条全过；`check_anim_v2` / `validate_anim_rules_v2` / `audit_anim_v2` / `check_card_identity_v2` / `validate_rules` 全绿。

## P0 实际 bug 与去 Splendor 硬编码

### [x] P0-1 修复 manifest 路径的跨平台判断（第一个做）

- 现象：`python3 animation/compile_tutorial.py --game splendor --track full --dry-run` 当前误报 57 条 cue 文本变化。
- 根因：`content/games/splendor/media/tts/full/tts_manifest.json` 中 57/83 条 `file` 是 Windows `\` 路径；`compile_tutorial.py` 用 `(ROOT / m.get("file", "")).exists()` 判断，WSL/Linux 下必然失败，于是被当成音频缺失。
- 修改点：
  - 读取 manifest 后，把 `file` / `subtitle_file` 的 `\` 统一转成 `/`；
  - 或复用 `build_tutorial_runtime.normalize_relative()` 的逻辑；
  - 加回归测试：带 `\` 的 manifest 路径不应被判为 changed。
- 验收：`--dry-run` 输出 `changed text cues: 0`，且不写任何文件。

### [x] P0-2 去掉 `compile_tutorial.py` / `qa_anim_ask.py` 里的 Splendor 硬编码

- `animation/compile_tutorial.py:124` delta LRC 写死 `[game:splendor]`、`[track:full]`。
- `animation/compile_tutorial.py:218-219` 生成的 manifest 路径写死 `content/games/splendor/media/tts/...`。
- `animation/compile_tutorial.py:270` QA gate 写死 Splendor 的 `_qa/questions.json`。
- `animation/qa_anim_ask.py:30,104,145` 写死 QA 目录、`game_id=splendor`、两人局说明。
- 修改：`write_delta_lrc` / `run_tts_delta` / `run_qa_gate` 全部显式接收 `game`、`track`；`qa_anim_ask.py` 增加 `--game/--track`，`game_id` 由参数或 `--in` 路径推导；演示局说明改为从游戏配置/profile 读取。
- 验收：Splendor 行为不变；用非 Splendor 假路径单测能生成正确路径。

### [x] P0-3 游戏专用 audit / ledger 移出通用动画目录

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

### [x] P0-4 清理 C# 端重复且已漂移的 source schema

- `clients/unity/Assets/Scripts/Tutorial/Animation/Schema/AnimDefs.cs` 里的 `AnimSchemaV2` 当前 0 引用，且已和 Python schema 不一致：
  - `StateOps` 缺 `set_order`；
  - `PresentationOps` 缺 `hide`、`camera`；
  - 没有 semantic ops。
- 建议直接删除未使用的 source-schema 类和 `AnimSchemaV2` 常量；Unity 运行时只保留 compiled 数据模型。
- 如果确实需要 C# 端 schema，则由 Python schema 生成或加一致性测试，不要手工双维护。

### [x] P0-5 part anchors 单源化

- 编译器硬编码 `CARD_PART_ANCHORS` / `CARD_PART_SIZES`（`animation/compile_animation_v2.py:123,144`），但 stage 模板里已有 `part_anchors`（如 `splendor.cards_intro.stage.json:143`），两边没有打通，且数值已不一致。
- 建议：编译器按当前 stage template 的 `part_anchors` + 模板 width/height 归一化；全局常量只作为旧数据 fallback。
- 验收：Splendor 的 `prestige` / `cost_1..4` / `bonus` / noble 标注位置不回归；新卡面尺寸可通过 stage 数据覆盖。

## P1 Python 工具链热点

### [x] P1-1 拆 `compile_events`

- `animation/compile_animation_v2.py:1196` `compile_events` 约 635 行，20 多个 `if/elif op == ...`。
- 建议拆成 `_compile_camera` / `_compile_magnifier` / `_compile_show/hide` / `_compile_transfer` / `_compile_set_face` / `_compile_set_order` 等 handler；保留统一上下文（at/dur/lead/state_ops/pointer_resolution）。
- 保护网：每步 `compile_animation_v2 --check`，编译结果必须逐字节不变。

### [x] P1-2 拆 `anim_schema_v2.py`

- `_check_event` 264 行、`resolve_track` + `resolve_one` 315 行，混合了 target 归一化、语义宏 lowering、继承解析、契约合并和校验。
- 建议拆成 `normalize.py`（`_normalize_event` / `_lower_semantic_event`）、`inherit.py`（`resolve_track`）、`validate_track.py`。
- 宏 lowering 保持唯一定义；compiler 只消费 lowering 后的 primitive events。

### [x] P1-3 拆 `validate_anim_rules.run`

- `animation/validate_anim_rules.py:128` 约 270 行，单函数同时做 stack/create/transfer/purchase/贵族/守恒等多类事件检查。
- 建议改成 `Ledger` 类 + 每类事件的 handler，或至少拆成 `run_stack / run_transfer / run_purchase / run_scoring` 等阶段，便于后续移到游戏目录。

### [x] P1-4 统一 compiled state 回放工具

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

### [x] P1-5 收敛 LRC writer / 旧入口

- `compiler`、`compile_tutorial.write_tts_lrc`、`compile_tutorial.write_delta_lrc`、`tts_doubao.write_tts_lrc` 各有一套 LRC 生成/解析。
- `animation/rebuild_tutorial.py` 与 `compile_tutorial.py` 流程重叠，建议缩成别名或标记弃用。
- 建议统一为一个 LRC 模块，并让 `compile_tutorial` 直接调用 Python 函数，不再 subprocess 自己仓库的脚本。

## P1 验收记录（2026-10-07 续）

结论：**P1-1 ~ P1-5 已在当前工作区完成并通过回归**。

- P1-1 通过：`compile_events` 拆为统一 `EventContext` + `_compile_*` handler；Splendor full / `_schema_example` 的 `--stdout` 产物与重构前逐字节一致，`compile_animation_v2 --check` 通过。
- P1-2 通过：拆出 `schema_defs.py` / `normalize.py` / `inherit.py` / `validate_track.py`；`anim_schema_v2.py` 保留公共 re-export 与 CLI，`anim_schema_v2 --example` 行为不变，compiled 产物逐字节一致。
- P1-3 通过：Splendor ledger 的 `run` 拆为 `Ledger` 类 + `_on_stack/_on_create/_on_destroy/_on_transfer` + `_finish_cue/_check_purchases`；`validate_anim_rules_v2` 仍为 83 cues / 0 warnings。
- P1-4 通过：新增 `animation/compiled_state.py`，check_anim、check_card_identity、audit、compiler 共用 state_ops / renderable states / effective source；Splendor audit 输出与重构前一致，card identity 仍为 569 states / 0 error。
- P1-5 通过：新增 `animation/lrc.py` 统一 estimated / manifest / delta / TTS rewrite；`tutorial_script_tool`、`tts_doubao`、`compile_tutorial` 改为委托；`rebuild_tutorial.py` 标记 deprecated；`compile_tutorial` 直接调用 compiler / ledger Python 函数。estimated LRC、manifest TTS LRC、TTS rewrite 输出与旧实现逐字节一致。
- 回归：`python3 -m unittest discover -s animation -p 'test_*.py'` → 71 条全过（两条残余修复前为 69 条）；`check_anim_v2` / `validate_anim_rules_v2` / `audit_anim_v2` / `check_card_identity_v2` / `compile_tutorial --dry-run` 全绿，`--dry-run` 仍为 `changed text cues: 0`。

### P1 二次独立验收（复核）

- P1-1：从 `HEAD` 归档旧 `compile_animation_v2.py`，分别对当前 `full` / `_schema_example` 跑 `--stdout`，与当前新实现逐字节 diff 一致。
- P1-2：新拆分模块 / CLI / 测试可用；`anim_schema_v2 --example` 通过，旧/新 compiler 输出一致。
- P1-3：旧 `validate_anim_rules_v2` 与新实现输出一致；Splendor 均 `83 cues / 0 warnings`。
- P1-4：旧 / 新 `audit_anim_v2` 输出一致（`53 cues / 0 errors / 0 warnings`），旧 / 新 `check_card_identity_v2` 均为 `569 states / 0 error`。
- P1-5：estimated LRC、manifest TTS LRC、`rewrite_tts_lrc` 均与 `HEAD` 旧实现逐字节一致；`full.runtime.json` / `full.compiled.json` / LRC 无额外生成物差异。
- 整体：`compile_tutorial --game splendor --track full --skip-tts` 实际跑通，生成物零额外 diff；C# 编译检查 23 files / 0 errors。

### P1 残余观察与修复

- [x] 已修复：`animation/inherit.py` 顶部不再本地重定义 `_LOCAL_CUE_KEYS`，直接使用 `schema_defs._LOCAL_CUE_KEYS` 单一来源。
- [x] 已修复：`inherit.resolve_track` 改用 `compiled_state.resolve_effective_state_source(...)`；用 9 组继承边界样例（entry / initial / cut / 缺失 parent / 缺失 entry / tree / negative / demo / 链式继承）与旧实现对照，输出一致。
- 新增 `animation/test_inherit.py`：锁定 `_LOCAL_CUE_KEYS` 同源，并回归 parent / prev / initial 的契约继承。
- `tests/AnimationModelTests/` 是工作区新增的未跟踪 .NET 测试工程，属于 P2 工作；本轮 P1 验收不包含它。


## P2 C# Runtime 重构

### [x] P2-1 先给纯 C# 模型补测试

- `TimelineModel.cs` / `AnimDefs.cs` 不依赖 UnityEngine，可单独编入 .NET 测试工程。
- 先给 `TimelineEvaluator.Evaluate` 补帧测试：logical state、camera、move/flip/shuffle、overlay_show/overlay_hide、annotations、magnifier。
- 保护网建立后再拆文件。

### [x] P2-2 拆 `TimelineModel.cs`

- 当前 1068 行；`TimelineEvaluator.Evaluate` line 265 起约 370 行，混合了逻辑状态、相机、clip 求值、文字标注、overlay、magnifier。
- 拆分为 `EvaluateLogicalState` / `EvaluateCamera` / `EvaluateEntityClips` / `EvaluateOverlays` / `EvaluateAnnotations` / `EvaluateMagnifiers`，文件按类型拆开。
- 顺带把 line 309、531 的 `List.Sort` 改成稳定的显式顺序（at, compiled index），避免同一时间的 clips 顺序不确定。

### [x] P2-3 拆 `TutorialCuePlayer.cs`

- 当前 1593 行，同时负责内容根解析、音频/导航/seek、字幕、overlay、annotation、magnifier、debug UI。
- 用 partial class 或独立组件拆：
  - `TutorialContentLocator`
  - `CuePlaybackController`
  - `TutorialOverlayRenderer`
  - `TutorialAnnotationRenderer`
  - `TutorialMagnifierRenderer`
- 先做无行为变化的机械拆分，不换 OnGUI 渲染方式；拆分后再考虑是否迁到 UI Toolkit/Canvas。

### [x] P2-4 收口临时触控层

- `TutorialTouchControls.cs` 自己标注“临时移动端触控控制层”。
- UaaL 正式客户端已经用 Compose，建议明确它只服务 Editor/Desktop 调试，或直接移除。

## P2 验收记录（2026-10-07 续）

结论：**P2-1 ~ P2-4 已在当前工作区完成并通过编译/回归**。

- P2-1 通过：新增纯 .NET 测试工程 `tests/AnimationModelTests/`（无外部测试框架依赖），覆盖 logical state / camera / move / flip / shuffle / overlay_show+overlay_hide / annotations / magnifier / 同时间 clip 顺序；`dotnet run --project tests/AnimationModelTests` 全部通过。
- P2-2 通过：`TimelineModel.cs` 按类型拆为 `TimelineTypes.cs` / `VisualClipPlayer.cs` / `StageLookup.cs` / `TimelineEvaluator.cs` / `TimelineEvaluator.Annotations.cs` / `WorldRuntime.cs`；`Evaluate` 拆为 `EvaluateLogicalState` / `EvaluateCamera` / `BuildVisualItems` / `EvaluateEntityClips` / `EvaluatePicture` / `EvaluateOverlays` / `EvaluateMagnifiers`，并保留 annotation 独立解析。entity clips 与 overlay clips 排序改为显式 `(at, compiled index)`，同时间顺序稳定。
- P2-3 通过：`TutorialCuePlayer.cs` 改为 `partial class`，机械拆出 `Content` / `Playback` / `Overlay` / `Magnifier` / `Annotation` / `Subtitle` 六个 partial 文件；方法名/字段名集合与原文件一致，未迁移 OnGUI 或改动渲染方式。
- P2-4 通过：`TutorialTouchControls.cs` 头部注释明确为 Editor / Desktop 调试层；Android UaaL 由 Compose 控件负责且不挂载该组件；未移除旧入口。
- 回归：`dotnet run --project tests/AnimationModelTests` 全过；`TMPDIR=$(pwd)/.tmp-dotnet python3 tools/ops/check_unity_scripts.py --json` → 34 个 C# 文件 / 0 errors；Python 71 条单测全过。

### P2 二次独立验收（复核）

- P2-1：`dotnet run --project tests/AnimationModelTests` → `OK all checks passed`；测试覆盖 logical state / camera / move / flip / shuffle / overlay_show+overlay_hide+modifier / annotations / magnifier / 同时间编译顺序。
- P2-2：额外做了行为对照——从 `HEAD` 提取旧 `TimelineModel.cs` + 旧 `AnimDefs/StateModel/Easing`，与当前拆分后的 Core 文件分别构建临时纯 .NET 评测器，加载同一份 `full.compiled.json`，覆盖所有 cue 的 0/duration/clip 起止/state op/camera op 采样点；两边 digest 均为 `70A1862886448175FE7B711F4A18F242480785F1541FB141B76E31B8B2D56CB1`，行为一致。
- P2-2 结构核对：旧 `TimelineModel` 中的全部 class 均保留；旧 `BuildLogicalState` 重命名为 `EvaluateLogicalState`，新增 `EvaluateCamera / BuildVisualItems / EvaluateEntityClips / EvaluatePicture / EvaluateOverlays / EvaluateMagnifiers`，无丢失。
- P2-3：对旧 `TutorialCuePlayer.cs` 与新 partial 集合做名称集合对照：方法 71/71、public 字段 31/31，无缺失、无新增；`check_unity_scripts.py` 34 files / 0 errors。
- P2-4：`TutorialTouchControls.cs` 头部已明确 Editor/Desktop 调试用途；`TutorialCuePlayer.Awake` 仍在 `UNITY_ANDROID && !UNITY_EDITOR` 下关闭它，Compose 负责 Android 控件，行为未变。
- 回归：Python 71 条单测、`compile_animation_v2 --check`、四组 checker、`compile_tutorial --dry-run` 全绿。

## P3 工具链与测试缺口

### [x] P3-1 修 `check_unity_scripts.py` 在当前 WSL 环境的路径失败

- 复现：当前 WSL + Windows `dotnet.exe`（`~/.dotnet/dotnet -> /mnt/d/dotnet/dotnet.exe`）在修复前 `--json` 只返回 `{"ok": false, "files": 34, "errors": []}`；人工输出是临时工程位于 `/tmp` 时 Windows 编译器访问 `\\wsl.localhost\...` 源码报 `CS1504`，旧解析器未提取该格式。
- 修复：新增 `is_windows_executable` / `running_in_wsl` / `choose_project_parent` 纯函数；Windows dotnet + WSL 时临时工程放到 `ROOT/.tmp/check_unity_scripts`，并把 `--dir` 下 C# 文件复制进项目以避免任意 WSL 路径不可见；编译错误映射回原文件；未定位的 `CS*` 错误和 raw output tail 也会进入诊断；重复错误去重；`.tmp/` 加入 `.gitignore`。
- 测试：新增 `animation/test_check_unity_scripts.py` 15 条纯函数测试，覆盖 WSL 检测、路径选择、复制判定、错误映射和格式解析。
- 验收：`python3 tools/ops/check_unity_scripts.py` → `OK 34 个 C# 文件编译通过`；`--json` → `ok:true, files:34, errors:[]`。用 `mktemp -d` 构造语法错误再以 `--dir` 指向该目录，输出 `Broken.cs:9:21 CS1525`，不再是空 errors。
- 回归：`python3 -m unittest discover -s animation -p 'test_*.py'` 当时 117/117 通过（新增 15 条）。

### [x] P3-2 补 checker / geometry / orchestration 单测

- 已补：
  - `animation/test_anim_geometry_v2.py`：14 条，覆盖 slot 网格/row/block/stack 边界、容量 0、越界 overflow、zone_box 零尺寸、union/offstage、board/named-zone 相机、缺失 zone fallback，以及 part anchor 的 legacy fallback、stage 覆盖、event `part_u/v` 优先、零尺寸模板回退。
  - `animation/test_check_anim_v2.py`：9 条，直接测 `camera_at`、`picture_at`、`check_camera_ops`、`check_state_ops`、`check_dirty_boundaries`、`camera_eq`，覆盖排序、缺失 frame、负 at、put/remove 状态重放、脏帧命中与同机位/物体存活不命中。
  - `animation/test_check_anim_v2_sample.py`：6 条，抽出 `reconcile_sample()` 后覆盖缺失 sample、多余 sample（新增 fail-closed）、picture 不一致、state-sync 不一致、count-only/items 两种契约、face 与 visible 结构。
  - `animation/test_compile_tutorial.py`：新增 6 条，覆盖 dry-run 无变更不改写、dry-run 有文本变更时仍以 `dry_run=True` 调用 TTS 路径、缺失源返回 2、`prune_removed` dry-run/实际删除、Windows 分隔符规范化。
  - `animation/test_qa_anim_ask.py`：新增 8 条，覆盖空回复、非预期词、expect 词族选择、多个候选取最后 standalone、否定短语、自纠正、cue `qa` 单条/对象/列表/mixed 及 questions.json 原样加载。
- 验收：新增文件先逐个运行通过；`python3 -m unittest discover -s animation -p 'test_*.py'` 全量 160/160 通过。随后 `compile_animation_v2 --check`、`check_anim_v2`、`validate_anim_rules_v2`、`audit_anim_v2`、`check_card_identity_v2`、`compile_tutorial --dry-run` 全绿（dry-run `changed text cues: 0`）。

### [x] P3-3 归档一次性迁移脚本

- 将 `animation/migrate_time_anchors_v2.py`、`animation/migrate_object_targets_v2.py` 移到 `animation/archive/`，新增 `animation/archive/README.md` 说明用途、完成状态与不应再运行的原因；未直接删除。
- 窄范围检查 `animation/`、`tools/`、`clients/unity/`、`docs/`、`.claude/memory/`：无运行时 import/调用依赖；文档中的旧顶层路径已更新为 `animation/archive/...`。
- 验收：两个脚本不再出现在 `animation/` 顶层；`python3 -m unittest discover -s animation -p 'test_*.py'` 160/160 全绿；`compile_animation_v2 --check`、四组 checker、`compile_tutorial --dry-run`、`check_unity_scripts --json` 均通过。

## 建议执行顺序

1. P0-1（路径 bug）-> P0-2（去 Splendor 硬编码）。
2. P0-3（audit profile）-> P0-4/P0-5（schema / part anchors 单源）。
3. P1-1 -> P1-2 -> P1-4 -> P1-3 -> P1-5。
4. P2-1（C# 测试）-> P2-2 -> P2-3 -> P2-4。
5. P3-1 路径修复 -> P3-2 补单测 -> P3-3 归档；均已完成，见上方验收记录。

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
