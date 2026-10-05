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
| `content/games/splendor/tutorial/script.full.json` | 口播文字、cue 切分、group、beats、refs |
| `content/games/splendor/tutorial/anim/v2/full.anim.json` | 动画事件、camera、状态操作、时间锚点、parent/entry |
| `content/games/splendor/tutorial/anim/v2/_stage/*.stage.json` | zone 大小/位置/布局、template、命名机位 shots |
| `content/games/splendor/tutorial/full.runtime.json` | 编译产物：音频、时长、字级字幕 timing |
| `content/games/splendor/tutorial/anim/v2/full.compiled.json` | 编译产物，Unity 只读 |

## 1.5 tree、状态继承与 demo 分支

### tree 是舞台边界，不是状态边界

- 每棵 tree 有自己的 stage、zone、模板、命名机位。
- 换 tree 只换舞台/可见性，不重置、不分叉逻辑状态。
- 同一个 world 可以被多棵树复用；不要把“一 tree 一 world”当成状态隔离手段。

### 状态继承

- `cue_n` 默认继承有效状态来源：`entry` 最优先；没有 `entry` 时用 `parent`；没有 `parent` 时用轨道顺序里的前一条 cue。
- 继承的是完整 `end_state`，包括所有 zone 的组件；cue 可以只改其中一部分，不必重述整张桌面。
- 状态继承与 tree 无关：`parent` 可以跨 tree，换 tree 不会切断 `cue_n` 继承 `cue_{n-1}`。
- `entry` 仍可用于显式分叉/假设分支，语义是复制来源 cue 的 `end_state` 快照。
- `cut` / `world_cut` 在没有显式 `entry` 时表示显式重置到空状态。

### 状态契约

- `script.enter` / `script.exit` 跟随有效状态来源：`entry` 对应来源的终态；否则 `parent`/前一条 cue 的终态。
- `events` 永不继承，只属于当前 cue。

### cue stage

- `stage` 解析：`cue.stage -> tree.stage`；换 tree 自然用新 tree 的 stage。
- 当前 stage 不需要包含 state 里所有 zone。没有对应 zone 的组件保留在逻辑状态中，运行时不可见；切回包含该 zone 的 tree 后恢复显示。
- 不要在动画数据里为了绕过校验而把组件搬到别的 zone。

### demo / hypothetical 分支

- 假设性内容必须显式标 `demo: true`。
- demo 允许“牌堆清空 / 假设我买了 3 张牌”等 hypothetical 状态。
- canonical 分支通过显式 `entry` 回真实来源，不能默认继承 demo 结局；audit JSON 的 `state_graph` 会输出 `state_source`、`is_demo`、`branch_id`。

## 2. 对象接口：`target` 同时支持实体与屏幕空间

所有“对象原语”统一指向一个
`target` 接口。`space` 只有两种实现：

- `"entity"`：世界实体/逻辑组件。字段为 `zone`（或 `zones`）加选择器
  `template`/`palette`/`concept`/`parts`/`order`。
- `"screen"`：屏幕空间展示对象（mask/展示牌/提示条等），按 `id` 引用。

```json
// 实体：destination 集合 + 创建身份
{ "op": "create", "anchor": "...",
  "target": { "space": "entity", "zone": "showcase",
              "template": "sample_card_1", "palette": "card_level_1" } }

// 实体：把宝石从供应堆转移到玩家持有区
{ "op": "transfer", "anchor": "...",
  "target": { "space": "entity", "zone": "gem_supply_diamond", "concept": "gem" },
  "destination": { "space": "entity", "zone": "player_holding" },
  "quantity": 1 }

// 实体：高亮一个市场卡位
{ "op": "highlight", "anchor": "...", "dur": 0.5,
  "target": { "space": "entity", "zone": "card_market", "order": 2 } }

// 屏幕空间：创建/替换一个展示对象
{ "op": "show", "anchor": "...",
  "target": { "space": "screen", "id": "sample_card" },
  "image": "media/card/一级发展卡_绿_30_cutout.png",
  "rect": { "x": 0.24, "y": 0.10, "w": 0.24, "h": 0.68 },
  "layer": 10 }

// 屏幕空间：同一套表现原语
{ "op": "highlight", "anchor": "...", "dur": 0.5, "grow": 1.08,
  "target": { "space": "screen", "id": "sample_card" } }
{ "op": "point", "anchor": "...", "indicator": "arrow", "part": "prestige",
  "target": { "space": "screen", "id": "sample_card" } }
{ "op": "hide", "anchor": "...",
  "target": { "space": "screen", "id": "sample_card" } }
```

### 2.1 标注：显式区分 world / screen 锚定

`point` / `shape` / `label` 是标注原语。它们统一编译成运行时
`FrameState.Annotations`，每条带 `annotation_space`：

- `world`：锚在桌面实体上，每帧按 item 当前世界位置 + 部位锚点投影到屏幕；
  镜头/卡牌移动时标注跟着动。
- `screen`：锚在 mask/屏幕对象或屏幕槽位上，不经过相机；镜头移动时标注不动。

目标仍由 `target` 给出（`target.space` 决定收件人类型）；标注语义上建议再写一次
顶层 `space`，编译器也会从 target 推导。`shape` 支持 `arrow` / `circle` /
`cross` / `forbid` / `box`，其中 `box` 是外框。`part` 用语义部位名
（`whole` / `prestige` / `cost_1`..`cost_4` / `bonus` / `condition` / `noble_prestige` / `condition_2` / `condition_3`）表达锚点，不用 x/y 硬编码。

```json
// 桌面卡牌外框：镜头移动要跟
{ "op": "shape", "shape": "box", "space": "world",
  "anchor": "action.cards.market.001.1.start",
  "target": { "space": "entity", "zone": "card_market", "order": 2 },
  "part": "whole" }

// 指向 mask 上某处：镜头移动不能跟
{ "op": "shape", "shape": "arrow", "space": "screen",
  "anchor": "action.cards.cost.001.1.start",
  "target": { "space": "screen", "id": "purchase_card" },
  "part": "cost_2",
  "offset": { "x": 0.02, "y": -0.03 } }

// 世界文字：跟着桌面卡牌
{ "op": "label", "space": "world", "text": "这张卡提供 2 分",
  "anchor": "...", "target": { "space": "entity", "zone": "card_market", "order": 2 },
  "part": "prestige" }
```

规则：

- `create` / `ensure` / `destroy` / `transfer` / `stack` /
  `move_order` / `set_face` 改变逻辑状态，只实现实体对象。
  `shuffle` 也写在实体事件序列里，但它是牌堆的**纯视觉**抖动：只生成抖动 clip，
  不重排逻辑 order；源数据里的 `real_templates` 顺序就是抽牌顺序。
- `show` / `hide` / `highlight` / `point` / `shape` / `fade` / `scale` 是对象表现原语，
  实体和屏幕空间都实现。
- **费用框按宝石种类数选择 `part`**：发展卡左下角的价格是一列彩色圆盘，一个颜色一个
  圆盘；1/2/3/4 种宝石的圆盘总高度不同，所以不能再用一个固定的 `cost` 框。写
  `cost_1`..`cost_4`（数字 = 该卡 cost 里不同宝石颜色数），编译器按真卡扫描件测量好的
  四个框自动取锚点和尺寸，不要再手写 `part_u` / `part_v` / `part_w` / `part_h` 去凑。
  框的左/上/下边界会刻意略微超出卡面，避免看上去局促；这不是越界错误。
  例：红 38（2 红 + 2 白）→ `part: "cost_2"`；白 20（3 白 + 1 蓝 + 1 黑）→
  `part: "cost_3"`；三级红 70（6 红）→ `part: "cost_1"`；四色卡 → `part: "cost_4"`。
- `shape: "box"` 仍可用 `part_w` / `part_h` 覆盖默认矩形尺寸（占目标 rect 的宽/高比例），
  矩形以 `part` 的语义锚点为中心；不写则自动使用该 `part` 的测量尺寸。费用框统一用
  `part: "cost_N"` + box 即可，不要按 cue 手写近似坐标：
  `{ "op": "shape", "shape": "box", "part": "cost_3",
     "target": { "space": "screen", "id": "purchase_card" } }`。
- **贵族 mask 的标注**：左上角声望用 `part: "noble_prestige"` + circle；下方条件按条件宝石种类数
  用 `condition_2` / `condition_3` + `shape: "box"`（2 种或 3 种颜色），不要用通用 `condition` 圆框，
  也不要写近似坐标。
- `label` 实体和屏幕空间都实现：实体 target → world label 跟卡走；screen target
  （stage overlay 槽位）→ screen label 固定不动。
- `point` / `shape` 的 `offset` 写成数字时仍是**时间偏移**；写成
  `{"x": ..., "y": ...}` 时是标注的屏幕微调（viewport 比例）。为避免歧义，也可以显式写
  `nudge`。
- 实体的 `show`/`hide` 只改表现层透明度（复用 fade），不创建/销毁逻辑状态；
  创建/销毁仍用 `create`/`destroy`。
- `show` 带 `picture` 且无 `target` 时是整个舞台的整幅图原语，不走对象接口。
- `camera` / `wait` 是全局原语，没有对象目标。
- 世界对象的空间仍然只写 `zone`（或 `zones`）+ 选择器；不要写 x/z。
- 同一区域多个件用 `order`/`slot` 表达，不靠坐标偏移表达。`slot` 只用于 `create` 的落点；
  `transfer` 的落点是事件顶层 `order`（例如市场补回空位），不要把它塞进 `target` 当选择器。
- 空位、堆叠、添加位置由 stage 的 `layout` / `display` 决定。
- 数据迁移脚本：`python3 animation/migrate_object_targets_v2.py <track>.anim.json --write`。

### 牌堆 order 契约（`display.mode = "stack"`）

- `order 0` = 牌堆底，最大 `order` = 牌堆顶；`transfer` 默认取最大 `order` = 抽顶，
  取走不重排、空洞留在原地。
- `stack` 的 `real_templates[0]` = 最先被抽的牌；编译时按反序赋 `order`，让它拿到最大 `order`。
- `shuffle` 只做视觉抖动，不改牌序；跳转/重播必须得到同一套 `order`。
- 从牌堆取具体真牌用 `template`/`parts` 点名；盲抽不写选择器，绝不要写
  `target.order` 当“源是第几张”；供应堆虽然共用同一套 `stack` 渲染，但没有牌堆的“顶”语义。

### 2.2 屏幕文字说明的统一格式

所有面向观众的说明文字（行动提示、上限提示、规则补充等）统一用 `label` + screen
overlay 槽位，不要用 `overlay_show` 贴文字图片，也不要在 cue 里自己画黑底方框。
运行时的 label 绘制路径只有一条，会自动使用统一格式：

- 字号约为屏高的 `4.6%`（手机 1080p 下约 50px），比普通字幕提示大一档；
- 白色正文 + 深色描边，保证去掉底色后仍能看清；
- 无背景框 / 无黑底；
- 自动换行：运行时按每行最多 **15 个字** 自动插入换行；stage overlay 的 rect 是文字槽位，
  建议宽度 `0.5~0.7`、高度至少 `0.12`（两行大字建议 `0.15~0.16`），
  并在 x/y 留安全边距。

示例：

```json
// stage overlays
{ "id": "hint_text", "space": "screen",
  "rect": { "x": 0.03, "y": 0.42, "w": 0.62, "h": 0.16 } }

// cue event
{ "op": "label", "text": "这里写需要观众看清的说明文字",
  "anchor": "<cue>.start", "target": { "space": "screen", "id": "hint_text" } }
```

不要按 cue 单独调字号或加框；如果文字太长，优先缩短文案或调整槽位宽高。
Splendor full 现有参考：`hint_action_first`、`hint_limit`。

### 2.3 放大镜：放大桌面上的一部分实体

`magnifier` 把目标实体所在的世界区域**实时**渲染到屏幕上的放大镜 rect 里；用 `mask:"full"` 时还会保留那块区域的桌面色上下文。
它不是截图或影子副本：桌面实体高亮 / 变大时，放大镜里看到的是同一帧的同一个实体；
实体被 `transfer` 飞走时，放大镜里的那件也会同时飞走。

```json
{ "op": "magnifier", "anchor": "<cue>.start", "offset": 4.6,
  "id": "magnifier_nobles",
  "shape": "box",
  "mask": "full",
  "target": { "space": "entity", "zone": "noble_market" },
  "rect": { "x": 0.39, "y": 0.158, "w": 0.18, "h": 0.36 },
  "zoom": 1.15, "padding": 0.10, "layer": 10 }
```

规则：

- `mask`：放大镜是否保留桌面背景。
  - `"items"`（默认）：只显示事件 target 匹配到的实体，实体之外透明。透明处显示的是主画面内容，不保证等于放大镜对应的那块桌面；目标飞走 / 被拿走后可能露出错误背景。
  - `"full"`：保留桌面上下文。`box` 用桌面色铺满整个 `rect`；`circle` 先铺一个与圆等大的桌面色底，再叠加事件 target 匹配到的实体，最后画金色圆环。同一区域里的非 target 实体（例如贵族圈下方市场行的牌）不会进入镜片；目标飞走 / 被拿走后，原位置只剩桌面色，不会透出后面的主画面。Splendor full 的 `magnifier_nobles` 用这个模式：`shape:"box"`，矩形贴着贵族区下沿，覆盖其下方的市场。
- `shape`：`"circle"`（默认，圆形透镜）或 `"box"`（矩形透镜）。
  `circle` 在屏幕像素上取 `rect` 内最大的正方形作圆窗，`mask:"items"` 和 `"full"` 都会按这个正方形取景并画金色圆环，不会出现方形黑框；
  `box` 直接使用整个 `rect`，画金色矩形外框，矩形窗口内按 `mask` 决定是透明还是铺桌面色。
- `rect`：屏幕比例坐标（左上角原点），同时规定放大镜的位置和大小。
- `zoom`：放大倍率。`zoom=1` 表示镜头刚好框住目标包围盒；`>1` 推近放大，`<1` 拉远。
- `padding`：目标世界包围盒外扩量；调小会让画面更满、目标更大。
- `target` 仍只支持 `space="entity"`；取 `zone` + 选择器匹配到的全部实体，
  按它们的 world 包围盒确定镜头中心，再生成 lens 参数。
- `layer` 用于同一 cue 内多个放大镜的前后层叠；`id` 省略时默认 `magnifier`。
- 可选 `dur`（秒）：放大镜淡入时长；省略时立即出现。放大镜随 cue 结束消失。
- `magnifier` 是纯表现原语，不改变逻辑状态；桌面对象的高亮 / 飞牌仍必须由
  `highlight` / `transfer` 等原语自己声明，放大镜只负责把它们实时放大给观众看。

## 3. 时间：只写 anchor + offset

所有事件（包括 camera）都不要再写裸 `at`：

```json
{ "op": "create", "anchor": "setup.cards.001.1.b1.start",
  "target": { "space": "entity", "zone": "showcase", "template": "sample_card_1" } }
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
  "offset": 0.25,
  "target": { "space": "entity", "zone": "gem_supply_emerald" } }
```

规则：

- `anchor` 解析失败 → 编译失败。
- beat 文本在 TTS 词流中找不到 → 编译失败，不猜。
- 同一 beat 文本多次出现且无法按顺序确定 → 编译失败。
- `script.{track}.json` 的 beat 增删后，重跑
  `python3 animation/migrate_time_anchors_v2.py` 重新生成锚点。

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

- shot 可选 `view_offset_x` / `view_offset_z`（世界单位）：在 `zones` 算出的机位中心上整体平移，用来把 zone 压到画面一侧，给另一侧留 mask（例：贵族介绍左 mask、右玩家发展区）。
- 没有 camera 事件的 cue 继承**状态来源 cue 的 `camera_out`**（同一 resolved stage 且非 `cut/world_cut`）；否则使用当前 stage 的默认机位。
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
python3 animation/compile_tutorial.py --game splendor --track full

# 只检查计划，不写文件
python3 animation/compile_tutorial.py --game splendor --track full --dry-run

# 3. 分项检查
python3 animation/compile_animation_v2.py --game splendor --track full --check
python3 animation/check_anim_v2.py --game splendor --track full
python3 animation/validate_anim_rules_v2.py --game splendor --track full
python3 tools/ops/check_unity_scripts.py
```

## 8. LLM 写动画时的自检清单

- [ ] 每个事件写的是 `anchor`，不是裸 `at`。
- [ ] 用到的 anchor 都在顶层 `time_anchors` 有定义。
- [ ] 事件没有写 x/z，只写 zone/order。
- [ ] camera 用的是 stage 中已有的 shot id。
- [ ] 没有同 cue 内重复点同一个 zone 的高亮。
- [ ] `cut` / `world_cut` 没有隐式继承画面。
- [ ] cue 换 tree 时只改 stage/可见性；状态按 `entry -> parent -> 前一条 cue` 继承，换 tree 不重置状态。
- [ ] 当前 stage 可以不包含全部 state zone；缺失 zone 的组件应保留在逻辑状态中、运行时隐藏，而不是搬卡或 offstage。
- [ ] 假设性增减已标 `demo: true`；canonical 已显式 `entry` 回真实分支。
- [ ] 跑过 `compile_tutorial.py --dry-run` 和 `check_anim_v2.py`。
