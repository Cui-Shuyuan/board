# v2 动画管线（v3 运行时模型）

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

3. **`clips`** — 纯视觉插值。
   位置/缩放/透明度/翻转/洗混。**不得再写 ZoneId/Order/Face**：逻辑状态只由
   `state_ops` 决定。

## 父子 cue 属性继承

每条 cue 仍通过 `parent` 组成树。**子 cue 不写的属性自动继承父 cue 的对应值；写了就以子 cue 为准**：

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

`full.anim.json` 已按这套规则做过一次确定性最小化：当前文件里 109 条 cue 中，
90 条没有写 `enter`、72 条没有写 `exit`、98 条没有写 `tree/transition`——都是继承，不是遗漏。

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
