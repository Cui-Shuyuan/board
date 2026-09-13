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

## 从 flow.json 生成草稿（2026-09-06 追加）

`scripts/flow_to_tutorial.py` 确定性翻译 flow.json：transfer/random_draw/top_draw/play → move，shuffle → shuffle，state_change → highlight，其余 → wait。章节取 flow 的叶子 phase/round，事件取叶子动作节点。产物 slot 坐标为自动网格占位、sprite 为 `media/auto/{id}.png` 占位，需人工校准/替换。

## 下一阶段演进（2026-09-13）

现有 `tutorial.json` 是运行时动画层（L3/L4）的 v0，下一阶段要在它前面补口播稿层，并把状态前移到编译期：

- **L1 口播脚本层**：播放单元树、narration、source_refs、quick/full 版本标记。叶子按 1～3 句口播切，不按规则小节切。已产出 `games/splendor/tutorial/full.lrc`（LRC-like，109 cues），由 `scripts/validate_timed_script.py` 校验/解析；`scripts/split_lrc_long_cues.py` 负责过长 cue 拆分。
- **TTS 生成**：`scripts/tts_doubao.py` 使用豆包语音合成 2.0 WebSocket 双向流式接口，按 cue 输出 `mp3 + subtitle.json`；full 版 109 条已全量生成，`--write-lrc` 已生成 `full.tts.lrc`。
- **运行时 cue 数据**：`scripts/build_tutorial_runtime.py` 把 `full.tts.lrc + tts_manifest.json + subtitle.json` 编译为 `games/splendor/tutorial/full.runtime.json`；Unity 播放器 v0 为 `client/Assets/Scripts/Tutorial/TutorialCuePlayer.cs`。
- **分层编辑源**：`games/{game}/tutorial/script.{track}.json`（group_path -> cue -> beat），编辑工具 `scripts/tutorial_script_tool.py`（split/merge/set-pause），完整重生成 `scripts/rebuild_tutorial.py`；`full.lrc` 现在是由 source JSON 生成的 estimated LRC。
- **L2 动作库层**：同一语义动作只实现一次；quick/full 复用动作内容，但各自按 TTS 音频时长编译时间轴。
- **编译期起始画面**：为支持任意跳转，编译器离线计算每个叶子开头所有 sprite 的完整画面；运行时只加载「当前叶子起始画面 + 本节时间轴」，不维护历史。
- **TTS 顺序**：口播定稿 → TTS → 冻结音频/时长 → 动画生成；音频冻结后不再为动画改时间轴。
- 现有 validator/8 原语继续保留，但 schema 需扩展到口播稿与版本选择。

详见 `tutorial/下一阶段工作指导.md` 与 [[tutorial-production-pipeline]]。

## 工作流

1. `python scripts/flow_to_tutorial.py --game xxx` 生成草稿；
2. 替换素材、校准 slot 坐标；
3. `python scripts/validate_tutorial.py --game xxx` 清零错误；
4. 再进 Unity 看效果，视觉问题只调参数/素材，不碰数据结构。

**Why:** 教学动画制作的最大问题是 LLM 自由度太大导致小毛病反复返工。schema + validator + 原语库把错误前置到程序层。
**How to apply:** 新增任何教学动画，先套 schema，再写数据；不要在 Unity 里为单个动画新写协程。
