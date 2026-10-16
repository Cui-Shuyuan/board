# Animation Toolchain

动画生产链工具：section-first 装配、schema、编译、time anchors、TTS、检查与动画 QA。

- 源真值：`content/games/{game}/tutorial/{tracks,sections,shared,library}/`
- 装配中间产物：`content/games/{game}/tutorial/build/`（不进 Git）
- 编译产物：`content/games/{game}/tutorial/{track}.runtime.json` + `anim/v2/{track}.compiled.json`
- Unity 播放器：`clients/unity/Assets/Scripts/Tutorial/`
- 本目录只放跨游戏动画工具，不放 Unity 工程，也不放 per-game 动画数据。

当前 Splendor：full 67 cue / 12 section / 338 anchors；quick 24 cue / 11 section / 106 anchors。两轨引用同一批 `library/` 素材；迁移后 compiled 与迁移前逐字段一致（除 `source_sha256`）。另有无 TTS 的非卡牌夹具 `tests/fixtures/worker_toy/` 验证机制无关性。

## 原语与检查

- 标注原语 `point` / `shape` / `label` 统一编译为 `FrameState.Annotations`，显式区分 `world` / `screen` 锚定。
- `shape` 支持 `arrow` / `circle` / `cross` / `forbid` / `box`；`label` 支持 `label_mode` / `label_key`，`point`/`shape` 支持 `marker_key`、`arrow_direction` 与尾部 `text`。
- `magnifier` 是 screen 空间放大镜原语：把目标实体所在世界区域实时渲染进屏幕 rect；`mask`（items/full）、`rect`、`zoom`、`layer` 由脚本规定。桌面高亮/飞牌会原样出现在放大镜里。

共享素材工具：

```bash
python3 animation/material_cli.py validate --game splendor
python3 animation/material_cli.py impact --game splendor --material splendor.action.seed_initial_market --check
python3 animation/material_cli.py expand-cue --game splendor --track quick --cue quick_nobles/001 --anchors
python3 animation/check_materials.py --game splendor --track quick
```

常用检查：

```bash
python3 animation/section_source.py --game splendor --track quick --check
python3 animation/compile_animation_v2.py --game splendor --track quick --check
python3 animation/check_anim_v2.py --game splendor --track quick
python3 animation/validate_anim_rules_v2.py --game splendor --track quick
python3 animation/audit_anim_v2.py --game splendor --track quick
python3 animation/check_card_identity_v2.py --game splendor --track quick
python3 animation/check_anim_v2_sample.py --game splendor --track quick
```

- `material_library.py`：加载/校验 `tutorial-material/v1`，展开 cue 的 `use` 与 example variant，并把共享素材文件纳入 source hash。
- `check_materials.py`：用 compiled state 校验 action 前后置与 example 事实。
- `material_cli.py`：素材库全量校验、影响范围、过期检查、按素材重编和 cue 展开来源。
- `section_source.py`：解析 tracks/sections/shared/library，生成 build 中间产物。
- `compile_animation_v2.py`：确定性编译器，从 section 源生成 runtime / compiled。
- `check_anim_v2.py`：契约 vs compiled、机位顺序、脏帧、stage 布局。
- `validate_anim_rules_v2.py`：逐 cue 事件重放规则。
- `audit_anim_v2.py`：跨 cue 实物守恒、`card_market` 补牌、`point`/`shape`/`highlight` pointer 解析。
- `check_card_identity_v2.py`：Splendor 每个 state 内的 face-up 真卡身份唯一性。
- `check_anim_v2_sample.py`：Unity 采样与 compiled 逐 item 对账。

历史一次性迁移脚本见 `animation/archive/`，不要重新运行。
