# Animation Toolchain

动画生产链工具：schema、编译、time anchors、TTS、检查与动画 QA。
标注原语 `point` / `shape` / `label` 统一编译为 `FrameState.Annotations`，显式区分 `world` / `screen` 锚定；`shape` 支持 `arrow` / `circle` / `cross` / `forbid` / `box`。
`magnifier` 是 screen 空间放大镜原语：把目标实体所在世界区域实时渲染进屏幕 rect；`shape`（circle/box）、`rect`（位置/大小）、`zoom`、`layer` 等全部由脚本规定，桌面上的高亮 / 飞牌会原样出现在放大镜里，不需要再造影子对象。

- 源数据：`content/games/{game}/tutorial/anim/`
- 编译产物：`content/games/{game}/tutorial/`
- Unity 播放器：`clients/unity/Assets/Scripts/Tutorial/`
- 本目录只放跨游戏动画生产工具，不放 Unity 工程，也不放 per-game 动画数据。

## 审计

```bash
python3 animation/audit_anim_v2.py --game splendor --track full
python3 animation/check_card_identity_v2.py --game splendor --track full
python3 -m unittest animation/test_audit_anim_v2.py
```

四个 v2 checker 的分工：

- `animation/check_anim_v2.py`：契约 vs compiled、机位顺序、脏帧、stage 布局；
- `animation/validate_anim_rules_v2.py`：逐 cue 事件重放规则，每条 cue 从自己的 `start_state` 起算；
- `animation/audit_anim_v2.py`：跨 cue 实物守恒、`card_market` 补牌、`point`/`shape`/`highlight` pointer 解析；
- `animation/check_card_identity_v2.py`：Splendor 每个 state 内的 face-up 真卡身份唯一性（方案 B）。
