---
name: tutorial-animation
description: 讲规动画 section-first 源模型、数据接口、生产流程与当前状态
metadata:
  type: project
---

# 讲规动画（当前版）

## 定位与现状

- 离线编译的静态数字资产；口播稿主导，TTS 冻结后音频时长是动画硬边界。播放端只做确定性播放，不实时调 LLM。
- 试点《璀璨宝石》：
  - full：67 cue / 12 section / 338 time anchors；`check_anim_v2`、`validate_anim_rules_v2` 0 warnings，`audit_anim_v2` 53 cues 0 errors，`check_card_identity_v2` 553 states 0 error。
  - quick：24 cue / 11 section / 106 time anchors；检查 0 warnings，audit 17 cues 0 errors，卡身份 333 states 0 error；已是 catalog 默认 track。
- 两条 track 都已完成 section-first 迁移、编译和静态检查；正式视觉验收、full 逐 cue 重审仍未闭环。

## Section-first 源模型

源真值在 `content/games/splendor/tutorial/`：

- `tracks/<track>.track.json`：section 顺序与 track 元信息。
- `sections/<section_id>/section.json`：section 元信息、默认 entry、tree、`tts_asset`。
- `sections/<section_id>/<track>.anim.json`：cue / events / camera / anchors / parent / entry / 可选 `qa`。
- `sections/<section_id>/<track>.script.json`：口播文本、beats、refs。
- `sections/<section_id>/qa.json`：section 外置手写 QA，可空。
- `shared/<track>.base.anim.json`：track 公共 schema / worlds / trees。
- `shared/stage/*.stage.json`：跨 section 的 stage、zone、模板、命名机位。
- `build/`：`section_source.py` 装配出的中间产物，不进 Git、不是源真值。

规则：

- cue 在 section 文件里写 local id，编译后统一为 `<section_id>/<local_id>`；跨 section 引用必须写 qualified id。
- `entry: "section:<id>"` 回到 section 起点；section 第一条 cue 无显式 entry/parent 时继承 `section.entry`；section 内后续 cue 默认继承前一条。
- 素材统一放公共媒体池；TTS 以 section 为单位连续生成，section 内多条 cue 共享同一 `audio`，用 `audio_start` 切画面。

## 三条时间轴

1. **state_ops**：逻辑状态，`put` / `remove` 到具体 item_id；编译器在事件前后做 diff，显式生成 create/destroy/transfer/move_order 和脚本写出的 order 变化。没有隐式收拢；拿走一件就留空洞，前移必须写 `move_order`。
2. **camera_ops**：命名机位时间轴；stage 定义 shots，cue 只写 `{"op":"camera","at/anchor":...,"shot":"..."}`；编译器解析为 center/ortho/pitch/rect。同一 cue 连续机位间隔必须 ≥ `MIN_CAMERA_SHOT_SECONDS`（0.4s）。
3. **clips**：纯视觉插值（位置/缩放/透明度/翻转/洗混/高亮/标注）；不得写 ZoneId / Order / Face。

## 对象与标注接口

- 对象原语统一用 `target`：`{"space":"entity",...}` 或 `{"space":"screen",...}`。
- 实体状态：`create/ensure/destroy/transfer/stack/move_order/set_order/set_face`；表现：`show/hide/highlight/point/fade/scale`。
- `move` / `take` / `pay` / `flip` / `draw` 是 `transfer` 的编译期薄宏；`draw` 默认 0.6s。
- 标注：`point` / `shape`（arrow/circle/cross/forbid/box）/ `label`，编译为 `FrameState.Annotations`，显式 `annotation_space=world|screen`。
- 近期增强：
  - `label_mode: "add" | "replace"`：默认 add；replace 时同目标最新 label 生效。
  - `label_key`：让 `replace` 只替换指定逻辑文本，同一 overlay 不同 key 互不影响。
  - `marker_key`：允许同一目标上多个 point/shape 标注独立存在。
  - `arrow_direction: left|right|up|down` 与 `text`：箭头方向与尾部文字。
  - 屏幕 label 文本框按文本自动适配，避免裁剪。
- `part` / `part_u` / `part_v` 表达卡面语义锚点，mapping 的 `offset` / `nudge` 做屏幕微调。

## 时间锚点

- 轨道/源 section 写 `anchor`（可加 `offset`），编译产物写数值 `at`。
- 锚点 `id` 命名：`<cue_id>.start`、`<cue_id>.end`、`<beat_id>.start`、`<beat_id>.end`。
- 解析来源：section script 的 beats + `{track}.runtime.json` 的 TTS 字级 timing。
- 一次性迁移脚本已归档：`animation/archive/migrate_time_anchors_v2.py`，不再运行。

## 状态继承 / tree / demo

- tree 只决定舞台/可见性和镜头，不是状态边界；换 tree 不重置状态。
- 状态来源：显式 `entry` > `parent` > 轨道前一条 cue；整个链可跨 tree。
- 当前 stage 没有的 zone 不渲染，但组件保留在逻辑状态里，切回后继续显示；禁止用隐藏掩盖非法状态。
- `events` 永不继承；`cut` / `world_cut` 无显式 entry 时表示重置到空状态。
- `demo: true` 允许牌堆清空/假设买牌等分支；canonical 必须显式 entry 回真实来源。

## 生产流程

1. 改 section 文字脚本：`sections/<id>/<track>.script.json`。
2. 针对本 cue 改动手写最小合法性 QA，优先放进该 cue 的 `qa` 字段；外置历史问题放 `sections/<id>/qa.json`。改动 events/state/contract 后 QA 必须同步改。
3. 改 `sections/<id>/<track>.anim.json` 与 `shared/stage/`。
4. 编译与检查：`compile_tutorial.py`（增量 section TTS / runtime / compiled）、`compile_animation_v2.py --check`、`check_anim_v2`、`validate_anim_rules_v2`、`audit_anim_v2`、`check_card_identity_v2`。
5. 发送 QA：`python3 animation/qa_anim_ask.py --game splendor --track <track> --only <qualified_cue>`；脚本只发送/留档，不生成问题。
6. Unity 采样对账与截图视觉验收。

常用命令：

```bash
python3 animation/compile_tutorial.py --game splendor --track quick
python3 animation/compile_tutorial.py --game splendor --track quick --dry-run
python3 animation/compile_tutorial.py --game splendor --track quick --validate-qa
python3 animation/compile_animation_v2.py --game splendor --track quick --check
python3 animation/check_anim_v2.py --game splendor --track quick
python3 animation/validate_anim_rules_v2.py --game splendor --track quick
python3 animation/audit_anim_v2.py --game splendor --track quick
python3 animation/check_card_identity_v2.py --game splendor --track quick
```

每一条动画调整必须在 `docs/animation-change-log/entries.jsonl` 追加记录，并运行 `python3 tools/ops/check_animation_change_log.py`。

## 牌堆 order 契约

- 牌堆只有一套顺序：order 0 = 牌堆底，order 最大 = 牌堆顶（下一张被抽）。
- 抽顶 = transfer 取最大 order；抽走 order39 后下一张就是 order38，任何牌都不重排。
- 放回/加牌到顶 = 新 order = 当前最大 order + 1。
- 视觉台阶只由 order 0..7 产生；`lift = min(order, max_visible - 1)`。
- `stack` 事件中的 `real_templates[0]` 表示最先被抽的牌，实现时反序赋 order 拿最大 order。
- `shuffle` 只抖动，不重排。

## 工作方式 / 避坑

- 动画是手写静态资产；优先改源 JSON，再重编译 compiled。不要为单条 cue 在 Unity C# 里加特判。
- 合法性 QA 必须手写、一 cue 一事、只带最小状态；不能让旧问题重复问新动作。
- 纯展示 cue 可显式 `qa_exempt: true`，默认 false 且不继承。
- `offstage` / 隐藏只用于引擎确实需要的隐藏；五色宝石、黄金、卡牌实物总数必须守恒。
- 画面元素必须能追溯到源 JSON 的显式事件/原语。
- 动画文字与结构要一一对应；`camera` 写要入镜的 zone，`camera_fill` 默认 0.8，特写 0.6–0.72。

## 当前遗留

- 工作区有未提交的 quick 精修与新增 QA 留档；需按流程提交。
- quick 正式视觉验收、full 67 cue 从头重审、Unity 采样截图确认仍未闭环。
- `cue_graph_v2.py` 的 insert/delete/split/merge 仍未接入 `compile_tutorial.py` 总控。
- 尚无编辑前后 compiled 自动回归断言。
- Android PTT → ASR → chat → TTS → 回动画的真机链路仍未验收。
