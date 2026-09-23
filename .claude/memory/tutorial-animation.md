---
name: tutorial-animation
description: 讲规动画 v3 当前模型、文件结构、生产流程与状态（2026-09-23）
metadata:
  type: project
---

# 讲规动画（当前版）

## 定位

讲规动画是离线编译的静态数字资产，播放端只做确定性播放，不实时调用 LLM。口播稿是主，动画是次；TTS 冻结后，音频时长是动画硬边界。

现状：
- 试点为《璀璨宝石》full 版，当前约 110 cue。
- full TTS 已全量生成；runtime、compiled、Unity 播放器、编译/校验/采样对账链路已跑通。
- quick 版尚未开始；正式视觉验收被用户主动跳过。

## 技术选型

- 客户端：Unity 6 原生安卓 App，URP。
- 视觉：2D/2.5D 实物照片 sprite，不做自由视角 3D。
- 相机：正交相机；`stage.board.camera_pitch = 90`（正俯视，地面 1:1）；pitch 可调，组件面片跟随相机。
- 音频：火山豆包 TTS，按 cue 生成 mp3 + 字级 subtitle。
- 动画制作：JSON + schema + validator + 固定原语；LLM 不写新 C# 协程。

## v3 数据模型

每条编译 cue = 三条时间轴：

1. **`state_ops`**
   - 逻辑状态时间轴，具体到 item_id 的 `put` / `remove`。
   - 内容：谁在哪个 zone、order 几、face 哪面。
   - 编译器在事件前后做 diff，显式写出 create/destroy/transfer/move_order 和脚本明确写出的 order 变化。
   - 没有隐式收拢：拿走一件就留空洞，前移必须显式写 `move_order`。

2. **`camera_ops`**
   - 命名机位时间轴。
   - stage 定义 `shots`；cue 的 camera 事件只写 `{"op":"camera","at":...,"shot":"..."}`。
   - 编译器解析为 center/ortho/pitch/rect。
   - 同一个 cue 内连续机位间隔必须 ≥ `MIN_CAMERA_SHOT_SECONDS`（当前 0.4s）。
   - 没有 camera 事件的 cue 继承上一 cue 终态机位。

3. **`clips`**
   - 纯视觉插值：位置 / 缩放 / 透明度 / 翻转 / 洗混。
   - 不得写 ZoneId / Order / Face，逻辑状态只由 `state_ops` 决定。

## 时间锚点 `time_anchors`

- 轨道顶层声明时间坐标；事件写 `anchor`，必要时加 `offset`；编译产物仍写数值 `at`。
- 当前 full 轨道约 468 个锚点，覆盖 cue 和 beat 的 start/end。
- 锚点命名：`<cue_id>.start`、`<cue_id>.end`、`<beat_id>.start`、`<beat_id>.end`。
- 解析来源：`script.{track}.json` 的 beats + `{track}.runtime.json` 的 TTS 字级 timing。
- 一次性迁移/重生成：`scripts/migrate_time_anchors_v2.py`。
- LLM 编写规范见 `games/splendor/tutorial/anim/v2/LLM-ANIMATION-GUIDE.md`。

## 父子 cue 与 entry 继承

- cue 通过 `parent` 组成树；子 cue 不写属性时继承父 cue。
- `entry` 指定入口状态来自哪条 cue 的终态；写 `"initial"` 从世界初始状态开始。
- 轨道顺序只决定播放顺序，不隐式决定状态继承。
- `events` 永不继承，只属于当前 cue。
- 跨 tree / `cut` / `world_cut` 作为重置点处理。
- 同 world 的不同 stage 可以共享 Store 状态，例如主树与 cards_demo。

## 文件结构（Splendor）

### 源数据

- `games/splendor/tutorial/script.full.json`：口播文本、分组、refs。
- `games/splendor/tutorial/anim/v2/full.anim.json`：动画事件、camera、parent/entry、tree、契约。
- `games/splendor/tutorial/anim/v2/_stage/*.stage.json`：各树舞台、zone、模板、命名机位。
- `games/splendor/tutorial/anim/v2/LLM-ANIMATION-GUIDE.md`：LLM 写作规范。

### 编译/运行产物

- `games/splendor/tutorial/full.tts.lrc`：TTS 后时间轴。
- `games/splendor/tutorial/full.runtime.json`：cue 顺序、音频、字幕、入口状态。
- `games/splendor/tutorial/anim/v2/full.compiled.json`：Unity 实际读取的 compiled。
- `games/splendor/media/tts/full/`：mp3 + subtitle.json。

### Unity 运行时

- `client/Assets/Scripts/Tutorial/Animation/`：v3 运行时。
- `client/Assets/Scripts/Tutorial/TutorialCuePlayer.cs`：音频/字幕/跳转/重播入口。
- 旧 `TutorialDirector` / v1 原语 / `tutorial.json` / 相关脚本已删除，不要再参考。

## 生产流程

### 标准顺序

1. 改文字脚本：`script.full.json`。
2. **手写这一 cue 的合法性问答，问运行中的 Board API**：
   - 问题只带这一个 cue 的最小事实（状态前提 + 动作 + 结果），手写进该 cue 的 `qa` 字段；
   - 用 `python3 scripts/qa_anim_ask.py --in games/splendor/tutorial/anim/v2/full.anim.json --only <cue>` 自动发送并留档；
   - 回答必须是「允许/合法」；不是就停下改脚本或改数据；
   - **问题必须由 AI/人根据改动点手写**，不能靠脚本生成器/模板批量造问题。
3. 改动画树/契约/events：`full.anim.json`。
4. 编译与检查（可用 `--validate-qa` 做机器侧补充）。
5. Unity 采样对账。
6. 截图做视觉验收。

> 新建或修改动画必须按“文字版 → Board API 问答校验 → 原语 → 对账”的顺序。禁止先改 events 再补文字，也禁止用 `offstage`/隐藏来掩盖非法状态。

### Board API 合法性问答（人工步骤，不是自动脚本）

这一层是**独立裁判**：`validate_anim_rules*` 是精确算术层，Board API 问答负责抓“规则理解错了”的问题。

- 每改一个真的改状态的 cue，都由 AI/人手写问题；问题无法自动生成，因为要先判断这条 cue 到底在做什么、哪些前提必须带。
- 问法遵守一 cue 一事、只带最小必要状态、不用教程自造词；规则自动发生的事就说成自动。
- 问题作为 cue 数据的一部分写在该 cue 的 `qa` 字段里；`qa_anim_ask.py` 自动从 `full.anim.json` 提取、发送、留档 `ask_log_<tag>.md/.jsonl`，不再需要临时拼问句。

### 总控命令

```bash
python3 scripts/compile_tutorial.py --game splendor --track full
python3 scripts/compile_tutorial.py --game splendor --track full --dry-run
python3 scripts/compile_tutorial.py --game splendor --track full --skip-tts
python3 scripts/compile_tutorial.py --game splendor --track full --validate-qa
```

`compile_tutorial.py` 负责：对照文本只挑变化 cue → 增量 TTS → 更新 manifest/tts.lrc → 重建 runtime → 编译 compiled。

### 检查命令

```bash
python3 scripts/anim_schema_v2.py games/splendor/tutorial/anim/v2/full.anim.json
python3 scripts/compile_animation_v2.py --game splendor --track full
python3 scripts/compile_animation_v2.py --game splendor --track full --check
python3 scripts/check_anim_v2.py --game splendor --track full
python3 scripts/validate_anim_rules_v2.py --game splendor --track full
python3 scripts/check_unity_scripts.py
```

### Unity 采样对账

```bash
./scripts/dump_anim_v2.sh --game splendor --track full
python3 scripts/check_anim_v2_sample.py --game splendor --track full
```

`check_anim_v2` 检查契约 vs 编译快照、`state_ops` 完整性、`camera_ops` 顺序与边界脏帧。
`check_anim_v2_sample` 逐 item 对账 Unity 采样的 `(zone,order,face)` 与 `end_state`。

## 关键设计规则

- 组件介绍默认独立 world，场上天然只有该组件。
- 所有演示可以有独立 tree；跨 tree 作为 cut 重置，同 world 不同 stage 可共享状态。
- 换树/起树第一条 cue 必须在 `at=0` 显式声明 camera（校验器 error）。
- 素材路径必须直接写处理过的 `_cutout.png`；多色模板用 `face_image_by_palette` 显式映射。
- `demo: true` 的 cue 只用于临时数量演示，紧跟的真实 setup cue 必须恢复实际数量。
- 动画职责越少越好；文字、契约、events 必须一一对应。
- 任意跳转由编译期入口状态 + 运行时维护当前状态结合实现。

## 工作方式（不要走回头路）

- 动画脚本是**手写的静态资产**，story / note / tree / 契约 / events / camera 全由人或 LLM 写入源 JSON；程序只做体检、过账、编译、对账、取景链检查。
- 旧的 `scripts/batch*.py` / `.claude/anim_batches/` 一律不要运行——它们会整份重写 `full.anim.json`，已退役。
- 合法性问句手写：一 cue 一事、只带最小状态；机器拼的版本不稳定，已废弃。
- `camera` 写“要入镜的 zone”（逗号分隔）；`camera_fill` 是这些 zone 占画面中央的比例，默认 0.8，特写 0.6–0.72；镜头默认沿父链继承。
- 用户验收节奏：AI 写 → 用户看 → AI 改 → 改完即成为固定资产，只用于确定性播放。
- 每条 cue 的文字至少包含：念什么（story） + 画面要变成什么（enter/exit） + 为什么这么演（note） + 在哪棵树/怎么切树（tree） + 看哪几个 zone（camera）。
- 动画文字与结构必须一一对应；后续应增加“文字与结构一致性”lint，防止“文字说了、结构没做”或反过来。

## 当前遗留

- time_anchors 全量迁移尚未 commit。
- TTS 增量尚未真实跑过一次。
- cue_graph insert/delete/split/merge 尚未接入 compile_tutorial。
- 尚无编辑前后 compiled 自动回归断言。
- 7 条 stage 布局 warning 待用户裁决。
- 第二批取景待手写：买牌进发展区、发展区+贵族结算、拿三色宝石、市场一格；需顺手把入镜 zone 写进契约。
- `action.nobles.forced.001.1` 仍有“贵族特写 → 整桌 → 又回贵族特写”的跳切。
- 两处原语缺口：错误示范的撤销/临时状态层；4 人局例子只有 A/B 玩家区。
- 起始玩家标记尺寸待用户实测。
- 主桌 extent 偏大导致整桌镜头偏小；多棵树落地后可再收紧。
- 打断问答到播放器的接线、Android 真机测试未完成。
