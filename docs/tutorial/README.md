# Tutorial 模块

v1 `tutorial.json` 动画 schema 已退役，旧示例与旧 schema 已删除。

当前《璀璨宝石》讲规系统分两条链路：

1. **口播链路**
   - `games/splendor/tutorial/script.full.json`
   - `games/splendor/tutorial/full.lrc` / `full.tts.lrc`
   - `games/splendor/tutorial/full.runtime.json`
   - 工具：`scripts/tutorial_script_tool.py`、`scripts/tts_doubao.py`、`scripts/build_tutorial_runtime.py`、`scripts/validate_timed_script.py`

2. **动画链路**
   - 源：`games/splendor/tutorial/anim/v2/full.anim.json`
   - stage：`games/splendor/tutorial/anim/v2/_stage/*.stage.json`
   - 编译产物：`games/splendor/tutorial/anim/v2/full.compiled.json`
   - Unity 运行时：`client/Assets/Scripts/Tutorial/Animation/`
   - 生产说明：`games/splendor/tutorial/anim/README.md`

旧版 `下一阶段工作指导.md` 已归档到 `.claude/archive/tutorial/`；新动画一律以
`games/splendor/tutorial/anim/README.md` 和 `.claude/memory/tutorial-animation.md` 为准。
