# Tutorial 模块

《璀璨宝石》讲规系统由口播、动画两条离线链路组成，统一由
`animation/compile_tutorial.py` 编译；播放端 Unity 只读取编译产物。

## 数据与产物

```text
content/games/splendor/tutorial/script.full.json              口播文字 / cue 切分 / beats / refs
content/games/splendor/tutorial/full.lrc / full.tts.lrc       估算与 TTS 后的 LRC
content/games/splendor/tutorial/full.runtime.json             音频 / 字幕 / 时长
content/games/splendor/tutorial/anim/v2/full.anim.json        动画源（events / camera / 契约 / qa）
content/games/splendor/tutorial/anim/v2/_stage/*.stage.json   zone / template / 机位
content/games/splendor/tutorial/anim/v2/full.compiled.json    Unity 只读编译产物
```

- 工具链总览：`animation/README.md`
- 动画编写指南：`content/games/splendor/tutorial/anim/v2/LLM-ANIMATION-GUIDE.md`
- 数据模型细节：`content/games/splendor/tutorial/anim/v2/README.md`
- 生产说明：`content/games/splendor/tutorial/anim/README.md`

## 常用命令

```bash
# 总控：文字差量 → 增量 TTS → runtime → compiled
python3 animation/compile_tutorial.py --game splendor --track full
python3 animation/compile_tutorial.py --game splendor --track full --dry-run   # 只看计划
python3 animation/compile_tutorial.py --game splendor --track full --skip-tts  # 跳过 TTS

# 独立编译 / 检查
python3 animation/compile_animation_v2.py --game splendor --track full --check
python3 animation/check_anim_v2.py --game splendor --track full
python3 animation/validate_anim_rules_v2.py --game splendor --track full
python3 animation/audit_anim_v2.py --game splendor --track full
```

修改 cue 时，必须按「文字脚本 → 手写 Board API 合法性 QA → 原语 events → 编译/对账」的顺序执行。
