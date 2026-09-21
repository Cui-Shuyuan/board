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
- 边界脏帧检查（机位切换第一帧不得残留上一镜的“即将消失”组件）。

`check_anim_v2_sample` 会把 Unity 采样到的 `(zone, order, face)` 与
`end_state` 逐 item 对账，单 cue 内的 order 漂移会当场暴露。
