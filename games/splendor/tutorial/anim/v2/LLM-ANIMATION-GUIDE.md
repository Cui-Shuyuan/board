# LLM 动画制作指导（v2 / v3 runtime）

本文是写给“负责写动画脚本的 LLM/人”的唯一开工入口。先读本文，再动
`full.anim.json`；不要先改 compiled，也不要让 Unity 侧兜底。

## 0. 总原则

1. **脚本是文字版动画。** 动画里出现的每个状态、机位、高亮、时间点，都必须能在
   源脚本中直接找到声明；编译器只做确定性的坐标/时间映射和差分编译。
2. **空间不写坐标。** 写 `zone + order`，由 stage 的 zone/layout 编译成 x/z。
3. **时间不写绝对秒数。** 写 `anchor`（+ 必要时 `offset`），由 TTS 字级时间解析成 `at`。
4. **没有“看起来没问题”。** 脏帧、1 帧机位、孤立高亮都当作脚本错误处理。

## 1. 源文件分工

| 文件 | 职责 |
|---|---|
| `games/splendor/tutorial/script.full.json` | 口播文字、cue 切分、group、beats、refs |
| `games/splendor/tutorial/anim/v2/full.anim.json` | 动画事件、camera、状态操作、时间锚点、parent/entry |
| `games/splendor/tutorial/anim/v2/_stage/*.stage.json` | zone 大小/位置/布局、template、命名机位 shots |
| `games/splendor/tutorial/full.runtime.json` | 编译产物：音频、时长、字级字幕 timing |
| `games/splendor/tutorial/anim/v2/full.compiled.json` | 编译产物，Unity 只读 |

## 2. 空间：只写 zone + order

```json
{ "op": "transfer", "anchor": "...", "source": "gem_supply_diamond",
  "destination": "player_holding", "quantity": 1 }
```

- 不要写 x/z。
- 同一区域多个件用 `order`/`slot` 表达，不靠坐标偏移表达。
- 空位、堆叠、添加位置由 stage 的 `layout` / `display` 决定。

## 3. 时间：只写 anchor + offset

所有事件（包括 camera）都不要再写裸 `at`：

```json
{ "op": "create", "anchor": "setup.cards.001.1.b1.start",
  "zone": "showcase", "template": "sample_card_1" }
```

锚点在轨道顶层 `time_anchors` 中预定义，命名约定：

```text
<cue_id>.start
<cue_id>.end
<beat_id>.start
<beat_id>.end
```

`offset` 只用于“锚点之后/之前的精确偏移”：

```json
{ "op": "highlight", "anchor": "action.take.same.001.b1.start",
  "offset": 0.25, "zone": "gem_supply_emerald" }
```

规则：

- `anchor` 解析失败 → 编译失败。
- beat 文本在 TTS 词流中找不到 → 编译失败，不猜。
- 同一 beat 文本多次出现且无法按顺序确定 → 编译失败。
- `script.{track}.json` 的 beat 增删后，重跑
  `python3 scripts/migrate_time_anchors_v2.py` 重新生成锚点。

## 4. 机位：shots 是唯一机位来源

stage 里定义：

```json
{ "id": "shot_card_face", "zones": ["showcase"], "fill": 0.72 }
```

cue 里用 anchor 指向 shot：

```json
{ "op": "camera", "anchor": "setup.cards.001.1.start", "shot": "shot_card_face" }
```

规则：

- 没有 camera 事件的 cue 继承上一条。
- 同 cue 内多机位必须是作者明确设计的节奏；不要写“wide 镜头一闪就切”的伪多镜头。
- 一个机位至少要有叙事意义；1 帧机位是脚本事故。

## 5. 高亮：只用于首次介绍/明确强调

允许：

- 新对象第一次出场；
- 台词正在专门点名它；
- 刚发生的状态变化结果需要强调。

不允许：

- “依次点一遍所有宝石堆/所有玩家区/所有市场”这种目录式罗列；
- 同一 cue 内重复高亮同一个 zone 来打节拍；
- 总结镜头里为了“看起来有动效”而高亮。

## 6. 脏帧规则

- `cut` / `world_cut` 是重置点，不允许继承上一棵树的画面。
- 想在某 cue 第一帧显示整幅图，必须在事件里显式 `show`；不能靠
  `enter.picture` 隐式带过来。
- `enter.picture` 是契约，不是 render 指令。
- 第一帧出现什么，必须能由当前 cue 自己的事件或明确继承解释。

## 7. 推荐工作流

```bash
# 1. 改文字脚本 / 动画脚本 / stage
# 2. TTS + runtime + compiled 一条链
python3 scripts/compile_tutorial.py --game splendor --track full

# 只检查计划，不写文件
python3 scripts/compile_tutorial.py --game splendor --track full --dry-run

# 3. 分项检查
python3 scripts/compile_animation_v2.py --game splendor --track full --check
python3 scripts/check_anim_v2.py --game splendor --track full
python3 scripts/validate_anim_rules_v2.py --game splendor --track full
python3 scripts/check_unity_scripts.py
```

## 8. LLM 写动画时的自检清单

- [ ] 每个事件写的是 `anchor`，不是裸 `at`。
- [ ] 用到的 anchor 都在顶层 `time_anchors` 有定义。
- [ ] 事件没有写 x/z，只写 zone/order。
- [ ] camera 用的是 stage 中已有的 shot id。
- [ ] 没有同 cue 内重复点同一个 zone 的高亮。
- [ ] `cut` / `world_cut` 没有隐式继承画面。
- [ ] 跑过 `compile_tutorial.py --dry-run` 和 `check_anim_v2.py`。
