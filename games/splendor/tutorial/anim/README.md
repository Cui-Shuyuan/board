# 讲规动画 v2

## 唯一权威链路

```text
games/splendor/tutorial/script.full.json / full.lrc     口播编辑源
                │
games/splendor/tutorial/full.runtime.json               音频/字幕/时长
                │
games/splendor/tutorial/anim/v2/full.anim.json          手写动画源：
  script.story/note/camera/enter/exit                   文字脚本 + 契约
  tree / transition                                     树与世界切换
  events                                                原语执行层
                │
scripts/compile_animation_v2.py                          确定性编译器
  scripts/anim_geometry_v2.py                            唯一几何源
                ▼
games/splendor/tutorial/anim/v2/full.compiled.json       运行时只读 compiled
                ▼
BoardGameTutorial.Animation.TutorialAnimPlayer           Unity 薄适配
```

旧 v1 `anim/full.json`、`anim/_stage/*`、`TutorialCueAnimPlayer`、`ZoneStore`、
`TutorialDirector`、`Teaching*`、`TweenLibrary`、旧 `tutorial.json` 已删除。

## 检查命令

```bash
# 1. 静态 schema / 文字结构与字段
python3 scripts/anim_schema_v2.py games/splendor/tutorial/anim/v2/full.anim.json

# 2. 编译并检查 compiled 是否最新
python3 scripts/compile_animation_v2.py --game splendor --track full
python3 scripts/compile_animation_v2.py --game splendor --track full --check

# 3. v2 契约 vs 编译快照（静态对账）
python3 scripts/check_anim_v2.py --game splendor --track full

# 4. Splendor 规则过账
python3 scripts/validate_anim_rules_v2.py --game splendor --track full

# 5. C# 编译
python3 scripts/check_unity_scripts.py

# 6. Unity 采样 + 对账（需 Windows 工作区同步、Unity 批处理）
./scripts/dump_anim_v2.sh --game splendor --track full
python3 scripts/check_anim_v2_sample.py --game splendor --track full
```

## 生产约定

新建/修改动画必须：

1. 先改 `full.anim.json` 的 `script.story/note/tree/transition/camera/enter/exit`；
2. 用 BoardAI API 确认规则合法性；
3. 再套 `events` 原语；
4. 跑静态/编译/规则/采样对账；
5. 视觉验收通过后再定稿。
