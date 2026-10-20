# 讲规动画素材化制作流程（新游戏接入指南）

本文是动画生产链在 section-first 之上的现行作者入口。它解决的核心问题是：
**同一套已确认的教学事实，可以被 full、quick 和未来其它版本共享，而不是各写一份状态、动作和镜头。**

适用范围：`content/games/<game>/tutorial/`、`animation/material_library.py`、
`animation/check_materials.py`、`animation/material_cli.py`、`animation/section_source.py`、
`animation/compile_animation_v2.py` 及其现有消费者。不要绕过这条链去手改 compiled。

音频/字幕时间轴（cue-level TTS、逻辑切片、字幕 track）的现行入口见
`docs/tutorial/audio-timeline-pipeline.md`。

## 0. 先确认事实，再写内容

1. 读当前游戏规则数据、组件资料和已有 QA；不要用 FAQ 中不确定的说法当规则。
2. 写清楚教学目标、教学对象、示例角色和示例局面。
3. 区分：
   - **规则真值**：比如“发展卡如何付费”，引用现有规则/组件资料。
   - **示例事实**：比如“这个例子买 onyx_3、支付两蓝一白”，放 `example` 素材。
   - **动作片段**：发生什么、读写哪些状态、入口出口是什么，放 `action` 素材。
   - **纯表现**：机位、构图、高亮、标注、文字、放大镜，放 `presentation` 素材或 cue 内联。
4. 场面随机结果必须使用确定预设或种子，禁止在播放时随机抽取。

## 1. 目录与职责

```text
content/games/<game>/tutorial/
  tracks/<track>.track.json              # 只负责 section 顺序和 track 元信息
  sections/<id>/section.json             # section 元信息 / entry / tree / tts_asset
  sections/<id>/<track>.script.json      # 口播文字、beats、refs
  sections/<id>/<track>.anim.json        # 教学顺序、内联事件、素材调用
  sections/<id>/qa.json                  # 可选外置手写 QA
  shared/stage/*.stage.json              # zone / template / 命名机位
  shared/<track>.base.anim.json          # track 的 schema / worlds / trees

  library/actions/*.action.json          # tutorial-material/v1，可复用状态动作
  library/examples/*.example.json        # tutorial-material/v1，具体局面事实与变体
  library/presentations/*.presentation.json # tutorial-material/v1，纯表现
```

- `library/` 是唯一权威定义；展开后的 build/compiled 复制不算源重复。
- 真正共享的内容只有一份；只出现一次的内容可以直接内联在 cue 里，不必为了形式资产化。
- 素材 ID 稳定、游戏命名空间，例如 `splendor.action.seed_initial_market`。

## 2. 先确定共享示例，再写各版旁白

对同一款游戏的多个版本：

1. 先选一个已经确认的示例局面，写成 `example`：
   - `facts.objects`：具体对象身份（逻辑 id、template、zone、face、order、parts）。
   - `facts.zones`：局面计数或语义选择器。
   - `variants`：确实必要的前情差异；每个 variant 有稳定 id、说明和差异步骤。
   - `steps`/嵌套 `use`：实现这些事实的状态动作。
2. 再围绕这同一个示例写各版旁白、镜头、节奏。
3. 调用处通过 `bind` 绑定角色/参数；通过 `timing` 绑定 local beat 或显式局部 `at`。
4. 版本差异要么是显式参数，要么是显式 variant；禁止用大段 override patch 改变共享示例的核心事实。
5. 如果只能复用表现、不能复用动作，就拆成 presentation；如果只能复用局面、动作不同，就在 track 中另写动作并仍引用同一 example 事实做校验。

## 3. Action 的入口、出口与校验

每个 action 写：

```json
{
  "schema": "tutorial-material/v1",
  "id": "game.action.verb_object",
  "kind": "action",
  "game": "game",
  "title": "一句话说明",
  "description": "规则依据、局部角色含义、入口/出口语义",
  "roles": [
    {"name": "target_zone", "type": "zone", "required": true, "accepts": ["zone_a"]}
  ],
  "params": [
    {"name": "count", "type": "integer", "required": false, "default": 1}
  ],
  "steps": [
    {"id": "do_it", "events": [
      {"op": "create", "count": "$count", "anchor": "@do_it",
       "target": {"space": "entity", "zone": "$target_zone", "template": "piece"}}
    ]}
  ],
  "contract": {
    "preconditions": {"zones": {"$target_zone": {"count": 0}}},
    "postconditions": {"zones": {"$target_zone": {"count": "$count"}}}
  },
  "rules": ["rulebook:section"]
}
```

规则：

- action 事件只使用状态原语（`create/destroy/transfer/stack/shuffle/move_order/set_order/set_face` 及 `take/move/pay/flip/draw`），不写 `camera/show/highlight/label`；表现放 presentation 或 cue 内联。
- `roles` / `params` 必须声明类型、必填和默认值；未知绑定、缺失必填、类型不匹配会在解析期报错。
- 入口条件写进 `preconditions`，结果断言写进 `postconditions`；它们是断言，不是第二套状态真值。
- 合法性尽量引用现有规则；游戏专属判断放该游戏的 checker，不要假装通用层能验证所有规则。
- 大动作可以组合小动作：在 `steps` 里用 `"use"` 引用别的 action，允许参数 `${...}` 传递；依赖循环会被拒绝。
- `events` 的 `anchor` 一律写 `@marker`，不能固化为 cue id 或绝对秒数。
- 连续承接时，动作只能改自己声明的角色/区域，不得静默补牌、补钱或换对象；不满足前置条件应失败。
- 独立示例必须显式 `entry`/`parent` 或标记 isolated，出口要么继续这个例子，要么显式回命名阶段。
- 错误示范保留 `negative: true`，且声明预期违反的规则；不要让错误分支成为正常流程的隐式父状态。
- 省略视觉过程可以，但必须显式声明，并在动作后置条件里保留完整逻辑结果。

## 4. Presentation 与机位

- presentation 只使用 `show/hide/highlight/point/shape/fade/scale/wait/camera/label/magnifier/overlay_show/overlay_hide`，不得产生状态变化。
- 表现绑定角色、区域和语义部位，例如费用区、资源区；屏幕临时对象 id 可以用 `@name`，每次调用会自动获得独立命名空间，避免多次实例化碰撞。
- stage 的命名 shot 仍只是机位；不要把“整段演示”硬塞进一个 shot。
- 同一动作可以有多个 presentation；同一 presentation 也可以配不同局面，不要强行一对一绑定。

## 5. 时间模型

- 共享素材只写语义节点/阶段标记，例如 `@place_worker`、`@pay_done`、`@arrive`。
- 各版 cue 在 `use.timing` 中把标记绑定到：
  - `{"beat": "b1", "edge": "start"}` / `{"anchor": "b1.start"}`：由现有 TTS beat + word timing 解析；
  - `{"at": 1.2, "offset": 0.1}`：测试夹具或确需显式局部时间时使用，不能反过来污染共享素材。
- 区分可伸缩的停顿/高亮与需要最小时长的移动/翻转/切镜头/阅读时间。
- quick 不是 full 的全局倍速：宁可减少可选展示或显式改表现，也不要暗中改音频速度或自动截断动作。
- 时间不足时默认报错并说明缺少多少、来自哪个素材调用；作者显式选择紧凑表现或延长停留。
- 所有 cue 最终时长必须由 `{track}.audio-slices.json` 的固定时间轴和 plan duration 决定；不能另起一套动画时钟。
- 连续播放、跳 cue、回退、重播、问答打断恢复必须得到同一逻辑状态和画面；素材不能依赖“必须先播放过一次”。

### 5.1 音频、逻辑切片与字幕轨道

quick 使用 cue-level TTS，full 使用 section TTS 和独立标题素材。
音频/字幕的制作流程：

1. `tutorial/quick.cue.tts.lrc` 按 beat 写 TTS 合成行。多条 cue 可引用同一素材
   的连续源区间，共享映射记录在 `audio-slices.json` 的 `audio_groups`。
2. 重新生成 TTS 后，把 word timing 审核写入
   `tutorial/quick.audio-slices.json` 的 `timing_points`。
3. 每条 cue/逻辑音频只引用命名时间点：
   `start_ref` / `end_ref` / `speech_start_ref` / `speech_end_ref` / `subtitle.*_ref`。
   不要在构建脚本或 timing policy 中写 TTS 秒数。
4. `audio-slices.json` 的 `cue_timing` 用 per-cue `mode` 控制 cue duration；
   `timing-policy.json` 提供全局 `default_mode`：
   - `max`：所有轨道都结束后再切下一 cue，动画不会被音频截断；
   - `audio`：音频轨道为准，用于标题 cue；
   - `visual`：画面为准，裁剪音频则构建失败。
5. 所有 cue 都启用 `trim_to_speech_start/end: true`：起点裁到实测
   speech start 前 0.1 秒，终点裁到实测 speech end 后 0.1 秒，然后追加
   显式反应停顿。章节标题和普通 cue 的目标停顿为 0.5 秒；普通 cue 的
   下一 cue 是章节标题时为 1.0 秒。full 用
   `trailing_silence_seconds` 追加固定静音；quick 用
   `post_speech_silence_seconds` 从实测 speech end 补足目标停顿。
   full/quick 的 `speech_start_ref` / `speech_end_ref` 都是 PCM 能量
   审查后的时间戳。full 连续 section 音频还要用能量静音中点校正相邻
   cue 的 provider 边界；校正后的 source window 通过
   `use_measured_source_window` 保留。能量测量只在制作期由
   `animation/measure_audio_boundaries.py` 写入固定时间戳，播放和 plan
   构建不运行能量分析；full 的 measured 时间戳通过
   `use_measured_speech_start/end` 在重生成 `audio-slices` 时保留。
   full 导入器为标题与正文保留最多 0.15 秒前置上下文；如果左侧 cue
   trim 后的终点距右侧首词不足 0.15 秒，右侧起点会夹到左侧终点，避免
   音频重叠。`source_boundary_contracts` 记录并校验连续、显式裁尾和
   作者留空关系。
6. 字幕是独立 `kind: "subtitle"` track，窗口由 `quick.audio-slices.json` 的
   subtitle refs 生成。播放器从课程 DSP 位置派生 cue-local 时间，同步字幕、
   视觉和连续音频。
7. 如果连续 section TTS 中某个标题和正文之间的换气不能被硬切，可用
   `{track}.cue-audio-overrides.json` 把相关 cue 指向单独生成的 MP3 和
   字幕文件；每条 cue 使用自己的 `audio_start=0`，同时仍按 PCM 能量首尾
   时间戳裁切。
8. 构建入口：

```bash
python3 animation/build_cue_timeline.py --game splendor --track quick
python3 tools/content/build_content_manifest.py --game splendor
```

完整字段与命令见 [音频与时间轴制作流程](audio-timeline-pipeline.md)，
运行时行为见 [课程音频播放器](continuous-cue-audio.md)。

## 6. 作者标绳：每一步都跑确定性检查

```bash
# 1. 素材库加载 / 引用 / 循环 / 类型 / 参数校验
python3 animation/material_cli.py validate --game <game>

# 2. section 源解析 + 素材展开
python3 animation/section_source.py --game <game> --track <track> --check

# 3. 素材动作前后置与 example 事实对比 compiled state
python3 animation/check_materials.py --game <game> --track <track>

# 4. 正式编译/校验/规则 ledger
python3 animation/compile_animation_v2.py --game <game> --track <track>
python3 animation/compile_animation_v2.py --game <game> --track <track> --check
python3 animation/check_anim_v2.py --game <game> --track <track>
python3 animation/validate_anim_rules_v2.py --game <game> --track <track>

# 5. Splendor 专属的守恒/身份/审计
python3 animation/audit_anim_v2.py --game <game> --track <track>
python3 animation/check_card_identity_v2.py --game <game> --track <track>

# 严格音频窗口：素材超出 cue plan duration 会进入诊断
python3 animation/check_materials.py --game <game> --strict-timing

# 6. 看一个 cue 的展开结果和来源映射
python3 animation/material_cli.py expand-cue --game <game> --track <track> --cue <qualified_cue> --anchors
```

规则 QA 与视觉验收：

- 每个真正改规则状态的 cue，仍按现行规范手写最小 Board API 问题；素材结构变化不能代替 QA 更新。
- 共享定义和调用上下文两层都要审：共享动作合法，不代表所有参数/入口都合法。
- compiled/静态检查通过只说明确定性和声明一致；机位、可读性、遮挡与节奏仍需 Unity 采样/截图/真人确认。
- 新 cue 或实质教学内容变更先做对比审核；纯结构迁移和独立测试继续推进。

## 7. 修改共享素材后的工作流

```bash
# 全量检查素材库
python3 animation/material_cli.py validate --game <game>

# 找出所有直接/间接调用者（会穿透 example -> action 依赖）
python3 animation/material_cli.py impact --game <game> --material <stable_id>

# 校验受影响 track 的 compiled 是否过期；过期会报 STALE
python3 animation/material_cli.py impact --game <game> --material <stable_id> --check

# 只重编受影响 track，不跑 TTS；脚本文字未变时 TTS 不应重新合成
python3 animation/material_cli.py impact --game <game> --material <stable_id> --rebuild
python3 animation/material_cli.py stale --game <game>
```

- 修改纯动画/共享状态素材：不要重合成未变音频；`compile_tutorial.py` 的文本差量会跳过 TTS。
- 修改文本或有效 TTS 参数：必须重新生成 timing，不能复用旧字级时间。
- 修改素材后，相关调用者的旧 QA/视觉证据失效；需要重编并更新 QA 与验收记录。
- 失败不要留下“最新成功产物”的假象；编译失败时修源，不要手改 compiled。

## 8. 最小可复制模板

可直接参考并复制：

- `tests/fixtures/worker_toy/tutorial/`：非卡牌虚构夹具，含两个 track 复用同一 example；
- `content/games/splendor/tutorial/library/`：真实 Splendor 共享素材。

一个最小 track 调用骨架：

```json
[
  {
    "id": "new_example",
    "entry": "initial",
    "tree": "main",
    "example": {"ref": "game.example.name", "variant": "default"},
    "use": [
      {
        "ref": "game.example.name",
        "variant": "default",
        "bind": {"actor": "player_a", "target_zone": "slot_a"},
        "timing": {
          "place": {"anchor": "b1.start"},
          "arrive": {"anchor": "b1.end", "offset": 0.1}
        },
        "insert_at": 1
      }
    ],
    "events": [
      {"op": "camera", "anchor": "cue.start", "shot": "shot_table", "dur": 0.0}
    ]
  }
]
```

运行 `section_source.py` 后会看到展开事件、生成锚点和来源链；运行 `compile_animation_v2.py` 后得到 compiled。夹具测试 `animation/test_worker_toy_fixture.py` 证明这条链不依赖 Splendor 和卡牌。

## 9. 给经济模型/编排 LLM 的短提示词

把下面这段和实际输入一起使用。它的目标是让模型**使用已确认事实和既有素材接口**，缺能力时报告，而不是发明新事实。

```text
你是本仓库讲规动画制作助手。只使用我提供的输入，不得自行发明游戏事实或素材。

输入：
1. game / track / 教学目标 / 受众
2. 已确认的 example 事实（对象身份、zone、face、order、资源数、variant）
3. 可用的 action / presentation 接口列表（id、roles、params、steps 标记）
4. 本版旁白与 TTS 锚点（beat id、文本、起止秒范围）
5. 本版时长预算和必须保留的最小画面时间
6. 不可改变的对象、状态、规则结论

输出：
1. 每个 cue 使用哪个 example / action / presentation，绑定哪些角色和参数
2. 时间绑定：每个 @marker 绑到哪个 beat/局部时间，是否使用可选步骤
3. 需要内联的一次性 events；只允许内联便利内容，不重复共享状态序列
4. 不确定或缺失的素材能力、时间不足、需要新增的 QA 问题清单

规则：
- 不修改 example 的核心事实；需要不同终态时显式选择另一个 example 或 variant。
- 找不到合适素材时明确报告缺少什么，不要用内联事件假装成共享动作。
- 时长不足时报告缺口和来源，不自动压缩动作、不悄悄改音频速度。
- 输出结构必须通过 tutorial-material/v1 和 cue/use schema。
```

## 10. 完成定义

一款游戏动画“接好”至少满足：

1. 规则和组件事实来自现有权威数据；
2. full/quick/后续版本引用同一批 confirmed examples，版本差异显式；
3. 所有当前 track 走 `section_source -> compile_tutorial -> compiled` 同一条链；
4. `material_cli validate`、`check_materials`、`compile_animation_v2 --check` 和该游戏规则 checker 通过；
5. QA、Unity 采样/截图和真人视觉验收按现行规范完成；
6. 修改共享素材时能通过 `impact` 找出全部调用者并重编；
7. 制作日志记录本次调整，未执行项明确标注。
