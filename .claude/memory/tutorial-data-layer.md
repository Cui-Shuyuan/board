---
name: tutorial-data-layer
description: 教学动画数据层定稿——tutorial.json schema、校验脚本、Unity 动画原语库与播放器骨架
metadata:
  type: project
---

# 教学动画数据层

## 定稿内容（2026-09-06）

动画 = `tutorial.json` + sprite 图 + 音频。播放端只做确定性播放，不实时生成。

- `tutorial/schema/tutorial.schema.json` — tutorial.json 的 JSON Schema，唯一数据契约。
- `tutorial/examples/splendor.tutorial.json` — 最小示例。
- `scripts/validate_tutorial.py` — 校验 schema、id 唯一性、slot/sprite 引用、action 必需字段、素材存在性、时间轴顺序、sprite 动画冲突。进 Unity 前先跑。
- `client/Assets/Scripts/Tutorial/` — Unity 播放端：
  - `TutorialData.cs`：JsonUtility 兼容的数据模型。
  - `Easing.cs`：缓动函数库。
  - `TutorialPrimitives.cs`：动画原语库（position/rotation/scale/alpha/shuffle）。
  - `TutorialDirector.cs`：空场景自动搭景 + 数据驱动播放器。

## 关键决策

- **slot 坐标数据化**：不再依赖 Unity 编辑器手动摆空物体。slot 的 x/y 是版图归一化坐标，`board.origin` 决定零点；允许超出 [0,1] 表示版图外位置。
- **动画动作收敛为 8 个原语**：move / flip / rotate / scale / fade / highlight / shuffle / wait。教学动画只允许传参，不允许新写协程。
- **sprite 与 slot 分离**：sprite 只描述外观（文件 + 物理尺寸），slot 只描述逻辑位置，事件用 id 引用。
- **每章一个音频**，字幕按 t 升序；打断后从当前章节重播。
- 旧 `TutorialPlayer.cs` 原型与 `TutorialDirector` 二选一运行，建议新教程走 TutorialDirector。

## 工作流

1. LLM 生成 `tutorial.json`；
2. `python scripts/validate_tutorial.py --game xxx` 清零错误；
3. 再进 Unity 看效果，视觉问题只调参数/素材，不碰数据结构。

**Why:** 教学动画制作的最大问题是 LLM 自由度太大导致小毛病反复返工。schema + validator + 原语库把错误前置到程序层。
**How to apply:** 新增任何教学动画，先套 schema，再写数据；不要在 Unity 里为单个动画新写协程。
