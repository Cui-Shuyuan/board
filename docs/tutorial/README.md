# Tutorial 模块

《璀璨宝石》讲规系统由口播、动画两条离线链路组成，统一由
`animation/compile_tutorial.py` 编译；Unity 播放端只读取编译产物。

当前源模型为 **section-first**：track 只排序 section；每个 section 拥有自己的口播、动画与 QA。

## 数据与产物

```text
content/games/splendor/tutorial/tracks/{full,quick}.track.json           section 顺序与 track 元信息
content/games/splendor/tutorial/sections/<id>/section.json               section 元信息 / entry / tree / tts_asset
content/games/splendor/tutorial/sections/<id>/{track}.script.json        口播文字 / cue 切分 / beats / refs
content/games/splendor/tutorial/sections/<id>/{track}.anim.json          动画源：events / camera / anchors / qa
content/games/splendor/tutorial/sections/<id>/qa.json                    可选的外置手写 QA
content/games/splendor/tutorial/shared/stage/*.stage.json                跨 section 的 zone / template / 机位
content/games/splendor/tutorial/{full,quick}.runtime.json                编译产物：section 音频与字幕时间轴
content/games/splendor/tutorial/anim/v2/{full,quick}.compiled.json       编译产物：Unity 只读
```

- `build/` 是 `section_source.py` 生成的装配中间产物，不进 Git，不是源真值。
- 旧单文件 `script.{track}.json`、`anim/v2/{track}.anim.json`、`anim/v2/_stage/` 已删除；不要再去改这些路径。
- TTS 以 section 为单位连续生成，section 内多条 cue 共享同一音频，用 `audio_start` 切画面。
- 当前 full 67 cue / 12 section / 338 anchors；quick 24 cue / 11 section / 106 anchors。

## 常用命令

```bash
# 总控：section 文本差量 → 连续 TTS → runtime → compiled
python3 animation/compile_tutorial.py --game splendor --track quick
python3 animation/compile_tutorial.py --game splendor --track quick --dry-run
python3 animation/compile_tutorial.py --game splendor --track quick --skip-tts

# QA 门禁
python3 animation/compile_tutorial.py --game splendor --track quick --validate-qa
python3 animation/qa_anim_ask.py --game splendor --track quick --only quick_nobles/001

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

- 动画编写指南：`content/games/splendor/tutorial/anim/v2/LLM-ANIMATION-GUIDE.md`
- 数据模型细节：`content/games/splendor/tutorial/anim/v2/README.md`
- 生产说明：`content/games/splendor/tutorial/anim/README.md`
- 工具链总览：`animation/README.md`
