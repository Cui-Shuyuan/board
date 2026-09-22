# v2 动画管线（v3 运行时模型）

> LLM/人编写动画脚本前，先读 [LLM-ANIMATION-GUIDE.md](LLM-ANIMATION-GUIDE.md)。

## 数据模型：三条时间轴

编译产物里每条 cue 由三部分组成：

1. **`state_ops`** — 逻辑状态时间轴。
   具体到 item_id 的 `put`/`remove`：谁在哪个 zone、order 几、face 哪面。
   编译器在每个事件前后做 diff，把 create/destroy/transfer/move_order、以及**脚本
   显式写出的 order 变化**全部写成确定性的 op。运行端每帧先应用它，再画面。

   **没有隐式收拢。** 拿走一件就留一个空洞；想让剩余组件前移，必须在脚本里
   显式写 `move_order`。默认追加位置是 `max(order)+1`，不是 `count()`。

2. **`camera_ops`** — 机位时间轴。
   stage 里定义命名机位 `shots`（zones + fill），cue 的 `camera` 事件只写
   `{"op":"camera","at":...,"shot":"shot_market"}`。编译器把 shot 解析成
   center/ortho/pitch/rect 写进 `camera_ops`。没有 camera 事件的 cue
   沿用上一条 cue 的终态机位（`camera_in`）。

   同一个 cue 内连续两个 camera 事件之间的间隔必须 >= `MIN_CAMERA_SHOT_SECONDS`
   （当前 `0.4s`）。比这更短的机位只持续一两帧，对观众没有信息量，属于书写事故；
   schema 校验会直接报错，而不是等到 Unity 里肉眼发现脏帧。

3. **`clips`** — 纯视觉插值。
   位置/缩放/透明度/翻转/洗混。**不得再写 ZoneId/Order/Face**：逻辑状态只由
   `state_ops` 决定。

## 时间锚点（全量）

空间有 `zone + order -> x/z`，时间也有同样的映射层：`anchor + offset -> at`。

- 轨道顶层 `time_anchors` 是“时间坐标声明”，一开始就全部写好；
  当前 full 轨道有 468 个锚点，覆盖每条 cue 的 start/end 和每个 beat 的 start/end。
- 所有事件不再写裸 `at`，而是写 `anchor`；只有原始时间点没有正好落在锚点上时，
  才补一个相对 `offset`。
- 编译器从 `script.{track}.json` 的 beat 文本 + `{track}.runtime.json` 的
  TTS word timing 里解析出 cue 内秒数。匹配时按**词流顺序**做
  “去标点/空白后的连续文本”匹配，所以 TTS 把一句话切成多个 subtitle event
  也能正确对上。
- 编译产物仍然是数值 `at`，Unity runtime 不感知锚点。

锚点命名约定：

```text
<cue_id>.start                   cue 起点（t=0）
<cue_id>.end                     cue 终点（runtime duration）
<beat_id>.start                  beat 第一个词 start
<beat_id>.end                    beat 最后一个词 end
```

示例：

```json
"time_anchors": [
  { "id": "setup.cards.001.1.start",
    "cue": "setup.cards.001.1", "edge": "cue_start" },
  { "id": "setup.cards.001.1.b1.start",
    "cue": "setup.cards.001.1", "beat": "setup.cards.001.1.b1", "edge": "start" }
]
```

```json
{ "op": "camera", "anchor": "setup.cards.001.1.start", "shot": "shot_card_face" }
{ "op": "create", "anchor": "setup.cards.001.1.b1.start",
  "zone": "showcase", "template": "sample_card_1" }
```

`edge` 当前支持：

- `cue_start` / `cue_end`；
- `start` / `end`：beat 对应字/词流的起止。

维护：

- 一次性迁移脚本：`scripts/migrate_time_anchors_v2.py`；
- 改完 `script.{track}.json` / TTS 后，如果 beat 增删或时间漂移，重跑该脚本：
  它会重新生成 `time_anchors`，并把仍是裸 `at` 的事件并入最近锚点；
- 锚点解析失败、文本对不上、同一 beat 文本歧义都会直接编译失败，不会静默 fallback 到秒数。

## 父子 cue 属性继承

每条 cue 仍通过 `parent` 组成树。**子 cue 不写的属性自动继承父 cue 的对应值；写了就以子 cue 为准**：

- `entry`：这条 cue 的**入口状态**来自哪条 cue 的终态。
  - 写 `"initial"`：从该世界初始状态开始（孤立树/重置点常用）。
  - 写某条 cue id：从那条 cue 的 `end_state` 开始。
  - 不写：如果 `parent` 同 world 且不是 `cut/world_cut`，默认继承 `parent` 的终态；否则视为 `initial`。
  - 轨道顺序只决定播放顺序，不再隐式决定状态继承。
- `tree`、`timing`、`script.story/note` 等普通属性：缺省继承。
- `script.enter` / `script.exit`：缺省继承父 cue 的**终态**；如果子 cue 自己写了，则按
  **zone 级覆盖**——只替换它写到的 zone，其余 zone 仍继承父终态。所以子 cue 只声明变化的部分。
- `events`：是当前 cue 自己的状态增量，**永不继承**；缺省为空列表。
- `id`、`parent`、`transition`：结构字段。`transition` 缺省固定为 `continue`，不会继承父级的
  `world_cut` 等切换语义。
- `cut` / `world_cut` / 跨 tree 的 cue：状态不继承，作为重置点处理。

例：`setup.starting_player.001.1` 只显式写自己变化的 `camera`（`shot_holding`）和台词；
`setup.starting_player.001.2` 连 `tree`、`transition`、`enter/exit` 都不写，自动继承父节点，
镜头也自然保持 `shot_holding`。

`full.anim.json` 已按这套规则做过一次确定性最小化：当前文件里 110 条 cue 中，
91 条没有写 `enter`、73 条没有写 `exit`、99 条没有写 `tree/transition`——都是继承，不是遗漏。

## 全景 shot

stage 里定义 shot 时，`zones` 可以写 `["*"]`：

- 表示「当前 stage 中所有已定义 zone（不含 offstage）的并集」；
- 当 `"*"` shot 被 cue 的 camera 事件实际使用时，编译期会先收窄到**当前状态里真的有组件的 zone**；
  只有没有任何可见 zone 时才回退到全部 zone。这样空玩家区不会把桌面中景拉成整桌远景。
- 相机按这个并集取景，`fill` 是并集包围盒占据画面的比例；
- 当前主桌全景用 `fill: 0.95`，比 `shot_board` 依赖 `board.extent` 的取景更贴近实际 zone，
  不会因为空边距把镜头拉得更远。

这个 token 专门给“所有 zone 都要入镜，但又不要拉出多余留白”的全景镜头使用。
`zone:["board"]` 仍保留旧语义（按 `board.extent` + 固定比例），不要混用。

## 结构编辑工具（cue graph）

`scripts/cue_graph_v2.py` 是 v2 的链表式结构编辑工具，只负责轨道结构：

```bash
python scripts/cue_graph_v2.py insert --source full.anim.json   --after A --new B --cue-file B.json
python scripts/cue_graph_v2.py delete --source full.anim.json --cue B
python scripts/cue_graph_v2.py split  --source full.anim.json   --cue A --at 1.5 --new A.s2
python scripts/cue_graph_v2.py merge  --source full.anim.json   --first A --second A.s2
```

它维护的是：

- cue 在轨道里的顺序；
- `parent` / `entry` 指针；
- 删除/合并时把 children 重接到新的 state owner。

它**不替写** story/note、TTS 音频和 runtime；这些属于单独的口播层。结构改完后仍需按
`script.full.json → TTS → runtime → full.anim.json → compiled` 的链路补齐对应资产。

## 素材路径约定

- stage 模板的 `face_image` / `back_image` 必须直接写**处理过的** `_cutout.png`
  （裁到实物、alpha 成品、可选 mm 统一尺寸）。运行时只按字面路径加载，
  不再做“优先找 `_cutout.png`”的隐式回退。
- 一个模板对应多种颜色时（如 `gem` / `gem_sample`），在模板里写
  `face_image_by_palette`：

  ```json
  "face_image_by_palette": [
    {"palette": "gem_diamond", "face_image": "media/card/白宝石_cutout.png"}
  ]
  ```

  运行时按组件自己的 palette 查表；这是数据里的显式映射，不是颜色启发式。
- 面板类模板（`shape: panel`）没有 `face_image`，用 palette 染色，这是正常的。

## 演示 cue（`demo: true`）

3/4 人局宝石数量演示需要临时把主树的每色数量从 4 补到 5/7，再在本片收尾
（`setup.gems.005.1`）destroy 回 4。规则过账会跳过这些 cue 的“本局在场数量”
检查，但保证：

- 演示只出现在明确标的 `"demo": true` cue；
- 紧接的真实设置 cue（`setup.gems.005.1`）必须把数量恢复成实际值；
- 其它物理总量（每色全场 7 枚）仍然检查。

## 总控编译入口

源数据只改两处：

- `games/splendor/tutorial/script.full.json`：口播文本/refs
- `games/splendor/tutorial/anim/v2/full.anim.json`：动画事件/camera/parent/entry

看效果前跑一次：

```bash
python3 scripts/compile_tutorial.py --game splendor --track full
```

它会：

1. 对照当前 `full.tts.lrc` 文本，只找出真正变了的 cue；
2. 只为这些 cue 做增量 TTS（生成 mp3 + subtitle）；
3. 更新 `tts_manifest.json` / `full.tts.lrc`；
4. 重建 `full.runtime.json`；
5. 编译 `full.compiled.json`；
6. 可选 `--validate-qa` 调 BoardAI 做合法性校验。

只想检查将发生什么、不动文件：

```bash
python3 scripts/compile_tutorial.py --game splendor --track full --dry-run
```

如果当前环境没有 TTS 依赖或不想调 API，可加 `--skip-tts`；但要求 TTS 已被增量更新过的 cue 才会通过。

## 命令

```bash
python3 scripts/anim_schema_v2.py games/splendor/tutorial/anim/v2/full.anim.json
python3 scripts/compile_animation_v2.py --game splendor --track full
python3 scripts/compile_animation_v2.py --game splendor --track full --check
python3 scripts/check_anim_v2.py --game splendor --track full
python3 scripts/validate_anim_rules_v2.py --game splendor --track full
python3 scripts/check_unity_scripts.py
```

Unity 采样：

```bash
./scripts/dump_anim_v2.sh --game splendor --track full
python3 scripts/check_anim_v2_sample.py --game splendor --track full
```

`check_anim_v2` 现在包含：

- 契约 vs 编译快照；
- `state_ops` 完整性（start_state + ops == first_state/end_state）；
- `camera_ops` 结构与顺序；
- 边界脏帧检查（机位切换第一帧不得残留上一镜的“即将消失”组件）；
- **stage 布局重叠检查**：按编译态里每个 cue 的实际占用件，用同一套 `slot_at` 几何算出各 zone 的
  实际包围盒；同一状态下两个 zone 的矩形相交就报 warning（例如贵族市场向上调整前会被三级发展卡
  压住、2/3/4 人局实际件数不同都直接反映在检查里）。已知的堆叠/相邻取舍也是 warning，不阻断编译。

`check_anim_v2_sample` 会把 Unity 采样到的 `(zone, order, face)` 与
`end_state` 逐 item 对账，单 cue 内的 order 漂移会当场暴露。
