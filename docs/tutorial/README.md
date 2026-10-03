# Tutorial 模块

《璀璨宝石》讲规系统分两条链路：

1. **口播链路**
   - `content/games/splendor/tutorial/script.full.json`
   - `content/games/splendor/tutorial/full.lrc` / `full.tts.lrc`
   - `content/games/splendor/tutorial/full.runtime.json`
   - 工具：`animation/tutorial_script_tool.py`、`animation/tts_doubao.py`、`animation/build_tutorial_runtime.py`、`animation/validate_timed_script.py`

2. **动画链路**
   - 源：`content/games/splendor/tutorial/anim/v2/full.anim.json`
   - stage：`content/games/splendor/tutorial/anim/v2/_stage/*.stage.json`
   - 编译产物：`content/games/splendor/tutorial/anim/v2/full.compiled.json`
   - Unity 运行时：`clients/unity/Assets/Scripts/Tutorial/Animation/`
   - 生产说明：`content/games/splendor/tutorial/anim/README.md`

动画制作以 `content/games/splendor/tutorial/anim/README.md` 和
`.claude/memory/tutorial-animation.md` 为准。
