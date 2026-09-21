---
name: tutorial-compile
description: 讲规动画 v2 的增量总控编译入口——entry 树、cue graph 结构操作、TTS/runtime/animation 一次构建
metadata:
  type: project
---

# 讲规动画总控编译（2026-09-22）

## 定位

源数据只改两处：

- `games/{game}/tutorial/script.{track}.json`
- `games/{game}/tutorial/anim/v2/{track}.anim.json`

看效果前跑一次：

```bash
python3 scripts/compile_tutorial.py --game splendor --track full
```

它负责把源数据变成运行资产：增量 TTS、`tts_manifest`、`full.tts.lrc`、`full.runtime.json`、
`full.compiled.json`。不再让用户/AI 手工调六个文件。

## 已完成（2026-09-22）

- **entry 树编译器**：`compile_animation_v2.py` 不再按轨道顺序隐式继承状态/机位。
  每条 cue 的 `start_state`、`camera_in` 来自显式 `entry`，或同 world 的 `parent` 终态；
  轨道顺序只决定播放顺序。当前 110 条 cue 编译产物与旧顺序编译器逐字节一致（仅 `source_sha256` 变）。
- **`scripts/cue_graph_v2.py`**：链表式结构操作（insert / delete / split / merge），维护
  cue 列表顺序、`parent` / `entry` 指针、children 重接。只改动画轨道结构，不负责 TTS/runtime。
- **`scripts/compile_tutorial.py`**：增量总控入口。
  - 对照 `full.tts.lrc` 文本，只挑真正变化的 cue；
  - 只对变化 cue 调 TTS 增量；
  - 重建 manifest / tts.lrc / runtime / compiled；
  - `--dry-run` 只报计划；`--skip-tts` 跳过 TTS；`--validate-qa` 可选走 BoardAI 合法性问句。
- **marker cue 已拆分**：
  - `setup.starting_player.001.3`：独立 `marker_demo` 树展示起始玩家标记；
  - `setup.starting_player.001.4`：回主树全景，`create` 到玩家侧的 `player_marker`；
  - full 轨道现为 110 cue。
- **口径确认**：`<starting_player_marker>` 是 `<marker>`，不属于 `<development_area>`（只含
  development_card/noble），也不属于 `<hand>`；舞台落点是玩家侧的 `player_marker`。
- **全景 shot**：stage shot 可用 `zones: ["*"]`，按所有 on-stage zone 的并集取景，
  `fill` 表示并集包围盒占画面的比例；`shot_all_zones` 当前用 `fill: 0.95`。

## 待办

1. **真正跑一次 TTS 增量**：改一条 cue 文本，在能连 `tts_doubao` 的环境执行
   `compile_tutorial.py`（或 `--tts-python`），确认只生成该 cue 的 mp3/subtitle，其他 cue 不动。
2. **把 `cue_graph_v2` 的四种操作接到 `compile_tutorial`**：
   insert / delete / split / merge 自动改 source + 调 TTS 增量 + 重编译 + 回归。
   用户入口最终只留 `compile_tutorial.py`；`cue_graph_v2.py` 退为内部库。
3. **回归自动化**：编辑前后对比 compiled，断言除受影响 cue 外所有
   `start_state / end_state / camera_in / state_ops / clips` 逐字段不变。
4. **BoardAI 校验前置**：`--validate-qa` 目前可选；未来“改动 → API 校验 → 编译”成为默认顺序。
5. **7 条既有 stage 布局 warning**：`check_anim_v2` 报的 player/development、showcase、供应堆相邻重叠，
   等用户裁决是调 stage 还是标记为允许的叠加。
6. **首次正式构建后**：`full.tts.lrc` 的 generator 会变为 `compile_tutorial.py`；
   确认这是唯一一次性 diff。

## 相关文件/命令

```bash
python3 scripts/compile_tutorial.py --game splendor --track full --dry-run
python3 scripts/compile_tutorial.py --game splendor --track full --skip-tts
python3 scripts/compile_tutorial.py --game splendor --track full --validate-qa
python3 scripts/cue_graph_v2.py --help
python3 scripts/compile_animation_v2.py --game splendor --track full --check
```

相关记忆：[[tutorial-production-pipeline]]、[[tutorial-animation-state]]、[[tutorial-data-layer]]、
[[workspace-sync]]。
