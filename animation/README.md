# Animation Toolchain

动画生产链工具：section-first 装配、schema、编译、time anchors、TTS、检查与动画 QA。

- 源真值：`content/games/{game}/tutorial/{tracks,sections,shared,library}/`
- 装配中间产物：`content/games/{game}/tutorial/build/`（不进 Git）
- 编译产物：`content/games/{game}/tutorial/{track}.runtime.json` + `anim/v2/{track}.compiled.json`
- Phase 5 固定时间轴 + plan：`{quick,full}.audio-slices.json`（timing points、source ranges、字幕 refs、per-cue policy、audio groups、可选多 visual segment）+ `{quick,full}.cue-timeline.json`（Android/Unity 共用 plan）；`{quick,full}.timing-policy.json` 仅保留全局配置
- Unity 播放器：`clients/unity/Assets/Scripts/Tutorial/`
- 本目录只放跨游戏动画工具，不放 Unity 工程，也不放 per-game 动画数据。

当前 Splendor：full 83 cue / 14 section / 402 anchors；quick 32 cue / 11 section / 138 anchors。两轨引用同一批 `library/` 素材；迁移后 compiled 与迁移前逐字段一致（除 `source_sha256`）。`check_materials.py` 已接入 `compile_tutorial.py` 构建门禁，失败会恢复旧 compiled。非卡牌夹具 `tests/fixtures/worker_toy/` 验证机制无关性与同事实不同节奏。

## 原语与检查

- 标注原语 `point` / `shape` / `label` 统一编译为 `FrameState.Annotations`，显式区分 `world` / `screen` 锚定。
- `label` 可用 `size` 单独指定字号（1080p 参考像素）；Splendor 的章节标题 cue 使用居中 `section_title` overlay + `size: 136`，普通说明仍沿用统一默认字号。
- `overlay_show` 可用 `dim_alpha` 做全屏压暗：`dim_alpha: 0.8` 时背景保留约 20% 可见度；该模式保留真实场景、隐藏 zone debug 层，适合章节标题卡。
- quick 与 full 都已迁移到固定时间轴 + 共享 plan。full 复用已有 section TTS，
  没有重新生成音频；quick 使用 cue-level TTS。
- 固定文件是 timing points、source range、字幕 refs、per-cue policy 和
  visual segment 映射的唯一来源；`{track}.timing-policy.json` 只保留全局配置。
- full 的 83 cue 与 quick 的 32 cue 都有确定性 plan，构建入口为
  `python3 animation/build_cue_timeline.py --game splendor --track {quick,full}`。
- 旧 `section_audio` / `audio_plan` / `pause_after_speech_seconds` /
  `hold_after_audio` 生产播放路径已经删除。
- 快速查看完整管线：`docs/tutorial/audio-timeline-pipeline.md`。

TTS 音色基线：Splendor 教程成片使用 seed2 音色 `zh_female_vv_uranus_bigtts`（resource `seed-tts-2.0`）。重生成教程 TTS 时必须显式设置 `DOUBAO_TTS_PROVIDER=seed2`、`DOUBAO_SPEAKER=zh_female_vv_uranus_bigtts`、`DOUBAO_RESOURCE_ID=seed-tts-2.0`，避免被 `.env` 里的 standard QA 音色 `BV700_streaming` 覆盖。
- `shape` 支持 `arrow` / `circle` / `cross` / `forbid` / `box`；`label` 支持 `label_mode` / `label_key`，`point`/`shape` 支持 `marker_key`、`arrow_direction` 与尾部 `text`。
- `magnifier` 是 screen 空间放大镜原语：把目标实体所在世界区域实时渲染进屏幕 rect；`mask`（items/full）、`rect`、`zoom`、`layer` 由脚本规定。桌面高亮/飞牌会原样出现在放大镜里。

共享素材工具：

```bash
python3 animation/material_cli.py validate --game splendor
python3 animation/material_cli.py impact --game splendor --material splendor.action.seed_initial_market --check
python3 animation/material_cli.py expand-cue --game splendor --track quick --cue quick_nobles/001 --anchors
python3 animation/check_materials.py --game splendor --track quick
python3 animation/check_materials.py --game splendor --strict-timing
# 严格模式当前已知例外：nobles/source.001 两个无 hold 动作超音频窗口，见 round2 response
```

Phase 5 quick/full plan 构建：

```bash
# quick: cue-level TTS（已有产物时可不重跑）
python animation/tts_doubao.py --provider seed2 --voice zh_female_vv_uranus_bigtts --resource-id seed-tts-2.0 --input content/games/splendor/tutorial/quick.cue.tts.lrc --out-dir content/games/splendor/media/tts/quick.cues --track quick --prune
python3 animation/build_cue_timeline.py --game splendor --track quick
python3 animation/build_cue_timeline.py --game splendor --track full
python3 tools/content/build_content_manifest.py --game splendor
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
