# Tutorial 模块

《璀璨宝石》讲规系统通过 `animation/compile_tutorial.py` 编译口播与动画，
再由 `animation/build_cue_timeline.py` 生成 Android/Unity 共用的课程计划。
Unity 按课程 DSP 时钟同步声音、字幕和视觉。

当前源模型为 **section-first + reusable materials**：track 只排序 section；每个 section 拥有自己的口播、动画与 QA；共享局面/动作/表现从 `library/` 引用。

## 数据与产物

```text
content/games/splendor/tutorial/tracks/{full,quick}.track.json           section 顺序与 track 元信息
content/games/splendor/tutorial/sections/<id>/section.json               section 元信息 / entry / tree / tts_asset
content/games/splendor/tutorial/sections/<id>/{track}.script.json        口播文字 / cue 切分 / beats / refs
content/games/splendor/tutorial/sections/<id>/{track}.anim.json          动画源：events / camera / use / example / anchors / qa
content/games/splendor/tutorial/sections/<id>/qa.json                    可选的外置手写 QA
content/games/splendor/tutorial/shared/stage/*.stage.json                跨 section 的 zone / template / 机位
content/games/splendor/tutorial/library/actions/*.action.json            可复用状态动作 tutorial-material/v1
content/games/splendor/tutorial/library/examples/*.example.json          确定性教学示例/事实/变体
content/games/splendor/tutorial/library/presentations/*.presentation.json 可复用纯表现素材
content/games/splendor/tutorial/{full,quick}.runtime.json                编译产物：cue 顺序/导航元数据与整轨兼容回退输入
content/games/splendor/tutorial/anim/v2/{full,quick}.compiled.json       编译产物：Unity 动画只读
content/games/splendor/tutorial/quick.cue.tts.lrc                        quick cue-level TTS 合成源
content/games/splendor/tutorial/{full,quick}.audio-slices.json           唯一固定时间轴（timing points / slices / refs / per-cue policy / audio groups / visual segments）
content/games/splendor/tutorial/{full,quick}.timing-policy.json          仅全局流水线配置，不承载 per-cue 时间
content/games/splendor/tutorial/{full,quick}.cue-timeline.json           课程 plan（Android/Unity 共用）
```

- `build/` 是 `section_source.py` 生成的装配中间产物；编辑入口为 section 与共享素材。
- `quick` 与 `full` 使用固定时间轴和共享 plan。`quick` 使用 cue-level TTS；
  `full` 使用 section TTS，以及 `setup/title_2`、
  `cards_group/title_3_2` 的独立标题音频。
- 一个 cue 可包含多个逻辑 MP3 slice，也可通过固定文件中的 visual segment
  引用多个 compiled animation segment。所有秒数、source range、字幕 refs、
  per-cue timing policy 和 visual mapping 都只在该固定文件中。
- Android 导航和 Unity 播放使用 `{track}.cue-timeline.json`。
  整条 plan 缺失/非法时，两端使用整轨 runtime 兼容模式。
- `CueCoursePlaybackController` 管理课程位置，`UnityCourseAudioTransport`
  调度连续 PCM 块；cue 标记驱动字幕、视觉与章节进度。
- full 时长 607.408 秒，音频规划为 30 个块、53 个合并连续交界；
  quick 时长 197.008 秒，音频规划为 32 个块。
- 当前编译基线：full 83 cue / 14 section / 402 anchors；quick 32 cue / 11 section / 138 anchors。

## 常用命令

```bash
# 总控：section 文本差量 → 连续 TTS → runtime → compiled
python3 animation/compile_tutorial.py --game splendor --track quick
python3 animation/compile_tutorial.py --game splendor --track quick --dry-run
python3 animation/compile_tutorial.py --game splendor --track quick --skip-tts

# 共享素材校验、影响范围与展开来源
python3 animation/material_cli.py validate --game splendor
python3 animation/check_materials.py --game splendor --track quick
python3 animation/material_cli.py impact --game splendor --material splendor.action.seed_gem_supply --check
python3 animation/material_cli.py expand-cue --game splendor --track quick --cue quick_nobles/001 --anchors

# QA 门禁
python3 animation/compile_tutorial.py --game splendor --track quick --validate-qa
python3 animation/qa_anim_ask.py --game splendor --track quick --only quick_nobles/001

# quick/full：固定时间轴 / 共享 plan / 内容发布
python animation/tts_doubao.py --provider seed2 --voice zh_female_vv_uranus_bigtts --resource-id seed-tts-2.0 --input content/games/splendor/tutorial/quick.cue.tts.lrc --out-dir content/games/splendor/media/tts/quick.cues --track quick --prune
python3 animation/build_cue_timeline.py --game splendor --track quick
python3 animation/build_audio_slices.py --game splendor --track full
python3 animation/build_cue_timeline.py --game splendor --track full
python3 tools/content/build_content_manifest.py --game splendor

# 独立编译 / 检查
python3 animation/section_source.py --game splendor --track quick --check
python3 animation/compile_animation_v2.py --game splendor --track quick --check
python3 animation/check_anim_v2.py --game splendor --track quick
python3 animation/validate_anim_rules_v2.py --game splendor --track quick
python3 animation/audit_anim_v2.py --game splendor --track quick
python3 animation/check_card_identity_v2.py --game splendor --track quick
```

修改 cue 时，必须按「文字脚本 → 手写 Board API 合法性 QA → 原语 events → 编译/对账」的顺序执行。

## 入口

- [音频与时间轴制作流程](audio-timeline-pipeline.md)
- [Cue Timeline 数据协议](cue-timeline-protocol.md)
- [课程音频播放器](continuous-cue-audio.md)
- [2026-10-20 设备与构建验收](validation/2026-10-20-cue-audio.md)
- [新游戏作者指南与可复制模板](animation-materials-authoring-guide.md)
- 动画编写指南：`content/games/splendor/tutorial/anim/v2/LLM-ANIMATION-GUIDE.md`
- 数据模型细节：`content/games/splendor/tutorial/anim/v2/README.md`
- 生产说明：`content/games/splendor/tutorial/anim/README.md`
- 工具链总览：`animation/README.md`

## 历史记录

- [Cue 音频方案、审查与阶段文档](history/2026-10-20-cue-audio/README.md)
- [素材化重构报告](animation-reuse-refactor-report.md)
- [素材化复核 R1–R6](animation-reuse-review-response-20261017.md)
- [素材化复核 R7–R8](animation-reuse-review-round2-response-20261017.md)
