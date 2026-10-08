---
name: tutorial-animation
description: 讲规动画 v3 当前模型、文件结构、生产流程与状态（2026-10-07）
metadata:
  type: project
---

# 讲规动画（当前版）

## 定位

讲规动画是离线编译的静态数字资产，播放端只做确定性播放，不实时调用 LLM。口播稿是主，动画是次；TTS 冻结后，音频时长是动画硬边界。

现状：
- 试点为《璀璨宝石》full 版，当前 83 cue（cue id 已语义化；旧文档的数字编号只作历史对照），388 个 time anchors。
- full TTS、runtime、compiled、Unity 播放器、编译/校验/采样对账链路已跑通。
- `time_anchors` 已迁移并 commit（`38971d4`）；源数据保留 anchor，compiled 输出数值 `at`。
- QA 与 cue 同置流程已落地：问题优先/可写在 `full.anim.json` 的 `qa` 字段，历史问题继续放 `_qa/questions.json`；`qa_anim_ask.py` 可直接提取 cue 内问题，`compile_tutorial.py --validate-qa` / `--validate-qa-all` 当前从 `_qa/questions.json` 提供机器侧门禁。
- quick 版尚未开始；正式视觉验收被用户主动跳过，仍未闭环。
- **2026-10-01：展示树收成 main + screen mask。** full 当前只有 `main` 一棵 tree；
  发展卡/宝石/购买样卡等非实体展示改为屏幕空间 `show/hide` 对象，
  贵族和起始玩家标记保留真实组件，在 main 树用近景 shot 介绍。
- **2026-10-01：对象原语统一 target 接口。** 所有对象原语统一写
  `target`，由 `{"space":"entity"|"screen", ...}` 选择接收者；
  编译器把接口展开成 flat 字段，Unity 运行时用 `IAnimVisualObject`
  同时处理实体和屏幕对象。屏幕对象也支持 highlight/point/fade/scale。

## 技术选型

- 客户端：Unity 6 原生安卓 App，URP。
- 视觉：2D/2.5D 实物照片 sprite，不做自由视角 3D。
- 相机：正交相机；`stage.board.camera_pitch = 90`（正俯视，地面 1:1）；pitch 可调，组件面片跟随相机。
- 音频：默认火山豆包标准语音合成小模型 v1（`standard`），按 cue 生成 mp3；旧 `seed2` 路径仍可切回并额外输出字级 subtitle。standard 路径当前没有字级 subtitle / 词级时间戳，运行时按无精确字幕降级。已有 full 资产的实际来源以 `tts_manifest.json` 为准（当前该文件记录 `seed-tts-2.0`）。
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
   - 纯视觉插值：位置 / 缩放 / 透明度 / 翻转 / 洗混 / 高亮 / 指示物。
   - `object_space` 标记片段作用在 `entity` 还是 `screen` 对象；表现原语
     通过 `IAnimVisualObject` 对两种对象执行同一套逻辑。
   - 不得写 ZoneId / Order / Face，逻辑状态只由 `state_ops` 决定。

### 对象接口 `target`

- 源事件统一用 `target` 选对象：
  - 实体：`{"space":"entity","zone":"...","template":"...","parts":[...],"order":n}`；
  - 屏幕：`{"space":"screen","id":"overlay_id"}`。
- `create/ensure/destroy/transfer/stack/move_order/set_face` 是实体状态原语；
  `shuffle` 写在实体事件序列里，但当前只生成视觉抖动 clip、不重排逻辑 order；
- `show/hide/highlight/point/fade/scale` 是对象表现原语，两种空间都实现；
- `shape` 是标注原语（`arrow`/`circle`/`cross`/`forbid`/`box`），`point` 也按标注渲染；
  `label` 实体和屏幕空间都实现。
- 所有标注编译为运行时 `FrameState.Annotations`：`annotation_space` 显式为 `world`
  （跟桌面实体投影）或 `screen`（不经过相机，跟 overlay / 屏幕槽位）。`part`/`part_u`/`part_v`
  表达卡面语义锚点，mapping 形式的 `offset`/`nudge` 表示屏幕比例微调。
- 全局原语：`camera`、`wait`、无 target 的整幅图 `show`。
- 常用原语：`move` / `take` / `pay` / `flip` / `draw` 是 `transfer` 的编译期薄宏；`set_order` 按绝对槽位重排单个对象并可选 `layer`；edge flip 支持 `axis` / `direction` / `destination`，`draw` 不写 `dur` 时默认 0.6s。
- 旧数据迁移：`python3 animation/archive/migrate_object_targets_v2.py <track>.anim.json --write  # 历史一次性脚本`。

## 时间锚点 `time_anchors`

- 轨道顶层声明时间坐标；事件写 `anchor`，必要时加 `offset`；编译产物仍写数值 `at`。
- 当前 full 轨道 388 个锚点，覆盖 cue 和 beat 的 start/end。
- 锚点命名：`<cue_id>.start`、`<cue_id>.end`、`<beat_id>.start`、`<beat_id>.end`。
- 解析来源：`script.{track}.json` 的 beats + `{track}.runtime.json` 的 TTS 字级 timing。
- 迁移与提交状态：`38971d4` 已把 `time_anchors` 全量迁移并 commit；`full.runtime.json` / `full.compiled.json` 输出数值 `at`，源数据保留 anchor。
- 一次性迁移/重生成：`animation/archive/migrate_time_anchors_v2.py`。
- LLM 编写规范见 `content/games/splendor/tutorial/anim/v2/LLM-ANIMATION-GUIDE.md`。

## 父子 cue、tree 与状态继承

- `tree` 是舞台/可见性边界：每棵树有自己的 stage、zone、模板、命名机位。
- tree 切换只换 stage/镜头，不重置、不分叉逻辑状态。
- 状态默认线性继承：`entry` 显式指定时以 `entry` 为准；否则 `parent`；没有 `parent` 时默认继承轨道顺序里的前一条 cue `cue_{n-1}`。整条链不因换 tree 而切断。
- cue_n 可以只改其中一部分状态，其余全部继承上一条；例如继承整张桌面后替换其中一张卡，是合法写法。
- `entry` 仍可用于显式分叉/假设分支；语义是复制来源 cue 的 `end_state` 快照。
- 状态契约 `script.enter/exit` 跟随有效状态来源（entry/parent/前一条 cue），不要求 tree 相同。
- 当前 stage 没有某个 zone，不代表状态里不能有该 zone 的组件。组件保留在逻辑状态中，运行时隐藏；切回包含该 zone 的 tree 后再显示。禁止为了通过校验把卡牌挪到别的位置。
- `stage` 解析：`cue.stage -> tree.stage`；换 tree 自然换 stage。
- `events` 永不继承，只属于当前 cue。
- `cut` / `world_cut` 在没有显式 `entry` 时仍表示显式重置到空状态；正常换 tree 不需要 cut。
- `demo: true` 分支允许“牌堆清空 / 假设买牌”等假设性增减；canonical 分支通过显式 `entry` 回真实来源，demo 假设不写回 canonical。

## 文件结构（Splendor）

### 源数据

- `content/games/splendor/tutorial/script.full.json`：口播文本、分组、refs。
- `content/games/splendor/card_registry.json`：发展卡真卡身份表；一张真卡一个 stage 模板，禁止再用 `market_card_{lv}_{bonus}` 代表多张。
- `content/games/splendor/tutorial/anim/v2/full.anim.json`：动画事件、camera、parent/entry、tree、契约、可选的 `qa` 字段。
- `content/games/splendor/tutorial/anim/v2/_stage/*.stage.json`：各树舞台、zone、模板、命名机位。
- `content/games/splendor/tutorial/anim/v2/LLM-ANIMATION-GUIDE.md`：LLM 写作规范。
- `content/games/splendor/tutorial/anim/_qa/questions.json`：历史手写合法性问句。
- `content/games/splendor/tutorial/anim/_qa/ask_log_*`：问答留档。

### 编译/运行产物

- `content/games/splendor/tutorial/full.tts.lrc`：TTS 后时间轴。
- `content/games/splendor/tutorial/full.runtime.json`：cue 顺序、音频、字幕、入口状态。
- `content/games/splendor/tutorial/anim/v2/full.compiled.json`：Unity 实际读取的 compiled。
- `content/games/splendor/media/tts/full/`：mp3 + subtitle.json + `tts_manifest.json`。

### Unity 运行时

- `clients/unity/Assets/Scripts/Tutorial/Animation/`：v3 运行时。
- `clients/unity/Assets/Scripts/Tutorial/TutorialCuePlayer.cs`：音频/字幕/跳转/重播入口。

## 生产流程

### 标准顺序

1. 改文字脚本：`script.full.json`。
2. **手写这一 cue 的合法性问答，问运行中的 Board API**：
   - 问题只带这一个 cue 的最小事实（状态前提 + 动作 + 结果），优先手写进该 cue 的 `qa` 字段；历史遗留问题继续保留在 `_qa/questions.json`；
   - 用 `python3 animation/qa_anim_ask.py --in content/games/splendor/tutorial/anim/v2/full.anim.json --only <cue>` 自动发送并留档；
   - 回答必须是「允许/合法」；不是就停下改脚本或改数据；
   - **问题必须由 AI/人根据改动点手写**，不能靠脚本生成器/模板批量造问题；
   - **cue 改一次，qa 必须跟着改一次**。只改 events/state 不改问题 = 未完成，不允许提交。
3. 改动画树/契约/events：`full.anim.json`。
4. 编译与检查（可用 `--validate-qa` / `--validate-qa-all` 做机器侧补充）。
5. Unity 采样对账。
6. 截图做视觉验收。

> 新建或修改动画必须按“文字版 → Board API 问答校验 → 原语 → 对账”的顺序。禁止先改 events 再补文字，也禁止用 `offstage`/隐藏来掩盖非法状态。

### Board API 合法性问答（人工写问题 + 自动发送留档）

这一层是**独立裁判**：`validate_anim_rules*` 是精确算术层，Board API 问答负责抓“规则理解错了”的问题。

- 每改一个真的改状态的 cue，都由 AI/人手写问题；问题无法自动生成，因为要先判断这条 cue 到底在做什么、哪些前提必须带。
- **qa 与 cue 同步更新是硬约束。** 旧问题问新动作会直接失去校验意义；只要 event/state/contract 变了，就必须重新审视并改写问题。
- 问法遵守一 cue 一事、只带最小必要状态、不用教程自造词；规则自动发生的事就说成自动。
- 问题可/优先作为 cue 数据的一部分写在该 cue 的 `qa` 字段里；`animation/qa_anim_ask.py` 可从 `full.anim.json` 自动提取、发送、留档 `ask_log_<tag>.md/.jsonl`。历史问题仍在 `_qa/questions.json`，`compile_tutorial.py --validate-qa` 当前从该文件选中受影响 cue 做门禁，`--validate-qa-all` 跑全部手写 QA。

### 总控命令

```bash
python3 animation/compile_tutorial.py --game splendor --track full
python3 animation/compile_tutorial.py --game splendor --track full --dry-run
python3 animation/compile_tutorial.py --game splendor --track full --skip-tts
python3 animation/compile_tutorial.py --game splendor --track full --force-full-tts
python3 animation/compile_tutorial.py --game splendor --track full --validate-qa
python3 animation/compile_tutorial.py --game splendor --track full --validate-qa-all
```

`compile_tutorial.py` 负责：对照文本只挑变化 cue → 增量 TTS → 更新 manifest/tts.lrc → 重建 runtime → 编译 compiled。

### 检查命令

```bash
python3 animation/anim_schema_v2.py content/games/splendor/tutorial/anim/v2/full.anim.json
python3 animation/compile_animation_v2.py --game splendor --track full
python3 animation/compile_animation_v2.py --game splendor --track full --check
python3 animation/check_anim_v2.py --game splendor --track full
python3 animation/validate_anim_rules_v2.py --game splendor --track full
python3 tools/ops/check_unity_scripts.py
```

### Unity 采样对账

```bash
./animation/dump_anim_v2.sh --game splendor --track full
python3 animation/check_anim_v2_sample.py --game splendor --track full
```

`check_anim_v2` 检查契约 vs 编译快照、`state_ops` 完整性、`camera_ops` 顺序与边界脏帧。
`check_card_identity_v2` 检查每个 state 内 face-up 发展卡真卡身份不重复；发展卡身份保真（方案 B）已主体完成：28 个独立卡位各有独立真卡模板/扫描件。
`check_anim_v2_sample` 逐 item 对账 Unity 采样的 `(zone,order,face)` 与 `end_state`。

## 关键设计规则

- tree 只决定舞台/可见性；状态按 `entry -> parent -> 轨道前一条 cue` 继承，不因换 tree 而切断。
- 当前 stage 不包含的 zone 不渲染，但组件仍保留在逻辑状态里，后续切回对应 tree 时恢复显示。
- cue 级 `stage` 由 `cue.stage -> tree.stage` 解析；换 stage/tree 不是状态重置。
- demo 分支通过 `demo: true` 显式标出，audit 会在 `state_graph` 中区分 `is_demo` 并对 canonical/demo 采用不同守恒口径。
- 起 cue / `world_cut` 的第一条 camera 事件必须锚定在 cue start（编译后 `at=0`）；校验器会检查。
- 素材路径必须直接写处理过的 `_cutout.png`；多色模板用 `face_image_by_palette` 显式映射。
- `demo: true` 允许“牌堆清空 / 假设买牌”等假设性增减；canonical 分支必须显式 `entry` 回真实来源，不能默认继承 demo 结局。
- 动画职责越少越好；文字、契约、events 必须一一对应。
- 任意跳转由编译期入口状态 + 运行时维护当前状态结合实现。

## 牌堆 order 契约（2026-10 定稿）

牌堆（`display.mode = "stack"` 的 zone）只有一套顺序，没有「逻辑顶」「视觉顶」之分：

- **order 0 = 牌堆底**：最先放上桌，不会被先抽走。
- **order 最大 = 牌堆顶**：下一张被抽走的牌。
- 抽顶 = `transfer` 取 **order 最大** 的那件；抽走 order39 后下一张就是 order38，**任何牌都不重排 order**。
- 放回/加牌到顶 = 新 order = 当前最大 order + 1；已有牌保持原 order。
- 视觉台阶由 **order 0..7** 这八个底部位错槽产生（容量 40、`max_visible = 8` 时）；order 8 及以上重合在顶面。
  即：`lift = min(order, max_visible - 1)`，底部 order 小的一端有台阶，顶部 order 大的一端是顶面。
- `stack` 事件的 `real_templates[0]` 仍表示「最先被抽的牌」，实现时给列表**反序**赋 order，使它拿到最大 order。
- `transfer` 的落点 `order` 写在事件顶层（市场补空位等）；它只对非堆叠目标生效，不是源选择器，不允许塞回 `target` 冒充源 order。
- `shuffle` 只抖动，不重排；不允许再出现 `order 0 = 顶`、`PickFront 取最小 order`、或为了抽牌而 `move_order` 压紧牌堆的路径。

## 工作方式（不要走回头路）

- 动画脚本是**手写的静态资产**，story / note / tree / 契约 / events / camera 全由人或 LLM 写入源 JSON；程序只做体检、过账、编译、对账、取景链检查。
- **动画问题归因脚本。** 画面里出现的任何视觉元素都必须能追溯到源 JSON 里的显式事件/原语；改动画默认只改 `full.anim.json` / stage 等源数据后重编译 `full.compiled.json`，不要为单条 cue 在 Unity C# 里加特判。C# 只负责解释通用原语；如果确认是通用原语实现缺口/缺陷，作为基础设施问题单独修，再回到脚本层完成动画修改。
- 合法性问句手写：一 cue 一事、只带最小状态；不要使用机器拼接的问句。
- `camera` 写“要入镜的 zone”（逗号分隔）；`camera_fill` 是这些 zone 占画面中央的比例，默认 0.8，特写 0.6–0.72；镜头继承状态来源 cue 的 `camera_out`，没有可用来源时用当前 resolved stage 的默认机位。
- 用户验收节奏：AI 写 → 用户看 → AI 改 → 改完即成为固定资产，只用于确定性播放。
- 每条 cue 的文字至少包含：念什么（story） + 画面要变成什么（enter/exit） + 为什么这么演（note） + 在哪棵树/怎么切树（tree） + 看哪几个 zone（camera）。
- 动画文字与结构必须一一对应；后续应增加“文字与结构一致性”lint，防止“文字说了、结构没做”或反过来。

## 当前遗留

- **下一步（最高优先）：Splendor full 重新过动画。** 发展卡扫描件/真卡模板已补齐，`check_card_identity_v2.py` 569 个中间状态 0 error；现在需要从头到尾重新看一遍 83 cue，重点看 `action.cards.*` / `action.nobles.*` / `action.reserve.*` 等市场与发展区 cue 的真卡画面、`setup.gems.005.1` 的宝石数量复位、终局示例的双发展区与贵族表现，以及旧审查记录中的观感问题。
- TTS 增量尚未真实跑过一次（改一条 cue 文本，验证只生成该 cue 的 mp3/subtitle，其他 cue 不动）。
- `cue_graph_v2.py` 的 insert/delete/split/merge 尚未接入 `compile_tutorial.py` 总控。
- 尚无编辑前后 compiled 自动回归断言。
- Quick 版尚未开始；正式视觉验收未闭环。
- 以下为 2026-09-23 旧审查记录，待 83 cue 重审时重新确认；当前 `check_anim_v2.py` 已是 83 cues / 0 warnings，旧记录的 2 条 stage 布局重叠 warning 不再出现：
  - 第二批取景待手写：买牌进发展区、发展区+贵族结算、拿三色宝石、市场一格。
  - `action.nobles.forced.001.1` 仍有“贵族特写 → 整桌 → 又回贵族特写”的跳切。
  - 两处原语缺口：错误示范的撤销/临时状态层；4 人局例子只有 A/B 玩家区。
  - 起始玩家标记尺寸待用户实测。
  - 主桌 extent 偏大导致整桌镜头偏小；多棵树落地后可再收紧。
- Android PTT / 回答 TTS / 继续播放代码已存在；打断问答回到动画播放器的端到端真机验收仍未完成，待复测。
