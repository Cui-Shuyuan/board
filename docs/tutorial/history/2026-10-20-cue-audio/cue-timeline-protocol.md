# Cue Timeline Protocol v1

状态：Phase 5 已完成。Splendor `quick`（32 cue）与 `full`（83 cue）都有
确定性、固定时间源的 `{track}.audio-slices.json` 和共享
`{track}.cue-timeline.json`。Android 与 Unity 的 plan 模式都只从共享 plan
读取 cue start/duration、课程总时长、章节映射和 seek 映射；Unity 的所有
cue 播放、seek、暂停/恢复与自动前进只由 `CueCoursePlaybackController` 的
课程 DSP 时钟驱动，cue-local 时间由课程位置减去 cue 起点得到。

当前课程级实现与回归测试见
[连续音频修复验收](continuous-cue-audio-fixes.md)。后文 Phase 1–5 的
`CuePlaybackController` / `UnityCueAudioExecutor` 描述保留为迁移历史；
当前生产入口统一使用 `CueCoursePlaybackController` / `UnityCourseAudioTransport`。

历史实现中的 `section_audio`、`audio_plan`、`pause_after_speech_seconds`、
`hold_after_audio`、标题特判、字幕尾词裁切和共享音源交接标记已从生产播放
路径删除。`runtime` / `compiled` 仍可作为构建输入和全轨兼容回退输入，但
不再作为 plan 模式下的运行时 cue 时间源。

## Phase 5：全量迁移与单轨执行

### 5.1 固定时间轴文件

每个 track 的唯一固定时间源：

```text
content/games/splendor/tutorial/quick.audio-slices.json
content/games/splendor/tutorial/full.audio-slices.json
```

schema 为 `tutorial-audio-slices/v1`、`schema_version=4`。固定文件拥有：

- timing point ID -> 秒数；
- cue ID -> logical audio slice ID；
- audio asset -> source range；
- subtitle -> timing point refs；
- cue/audio-group ID 映射；
- per-cue mode、trim、silence、hold 策略；
- 需要时的 `cue_visuals`：visual segment ID -> compiled animation 引用、
  cue-local start ref、source range refs 和 `after_end`。

`quick.timing-policy.json` / `full.timing-policy.json` 只保留全局字段：
`schema`、`schema_version`、`game_id`、`track`、`audio_source`、
`audio_slice_manifest`、`script_source`、`default_mode`。其中不得出现
per-cue ID 映射或秒数。

### 5.2 生成与校验

```bash
# section-level TTS runtime 变更后先同步固定切片
python3 animation/build_audio_slices.py --game splendor --track full
python3 animation/build_cue_timeline.py --game splendor --track quick
python3 animation/build_cue_timeline.py --game splendor --track full
```

生成器只按固定文件中的 ID/ref 通用查表，不包含 Splendor 具体 cue ID、
timing point ID 或课程时间戳。缺失 timing ref、重复 ID、重复 JSON key、
未知 cue/slice/animation ref、越界 trim、未知 visual segment、源范围越界
和循环引用都会显式失败。重复运行产物逐字节一致。

full 在 Phase 5 后为父章节新增了独立标题 cue：

- `setup/title_2`：`2，设置。`，独立音频 `setup.intro.mp3`；
- `cards_group/title_3_2`：`3.2，发展卡牌与贵族。`，独立音频
  `action.cards.group.mp3`。

两者均用 seed2 音色生成，不共享子章节音频。其余 full cue 继续复用已有
section TTS 媒体作为 audio slice source。若未来重新生成 full TTS，应保持
seed2 音色，并在重新生成后更新 `full.audio-slices.json` 的 timing points。

### 5.3 多 visual animation segment

多 visual clip 方案采用 **compiled cue id + source range**：

```json
{
  "cue_visuals": {
    "<cue_id>": [
      {
        "id": "<segment_id>",
        "animation": "<compiled_cue_id>",
        "cue_start_ref": "<cue_id>.visual.0.start",
        "source_start_ref": "<cue_id>.visual.0.source_start",
        "source_end_ref": "<cue_id>.visual.0.source_end",
        "after_end": "hold_last_frame"
      }
    ]
  }
}
```

`CueVisualTimelineResolver` 按 cue-local time 选择 segment，并输出
animation id 与 source time；`UnityCueVisualExecutor` 只执行这个结果：

- 活动 `animation`：加载 `animation` 指向的 compiled cue，并 seek 到
  `source_start + cue-local offset`；
- `hold`：冻结前一个 animation 的 `source_end`；
- animation 结束后的 gap：遵循该 animation 的 `after_end`；
- `after_end=clear`、无前序 animation 的 hold、或 cue 开头无视觉段：清空
  画面，不保留陈旧帧；
- 不同 compiled cue 之间切换时，各自使用 compiled `start_state`，这是明确
  的状态重置边界，不伪装成可继承状态；
- duration 为 0 的 compiled cue 使用 `type: "state"` 视觉段：加载该 cue
  的 compiled frame、seek 到 source time 0，并在 cue 时长内保持。这样
  纯音频 cue 里的瞬发 state/overlay 变更不会被漏掉，画面也不会停在旧状态。

视觉执行器不读写音频，也不决定 cue 完成。`animationEnabled=false` 只影响
画面提交，不改变 plan duration、音频执行或控制器完成时刻。

### 5.4 Android / Unity 一致性

- catalog 的 `cue_timeline_tracks` 同时启用 `quick` 和 `full`；
- Android 从 `{track}.cue-timeline.json` 读取 cue 顺序、start/duration、
  总时长、章节树、seek mapping 和 QA context；
- Unity 校验同一 plan set 的 cue 覆盖/顺序、总时长、visual/audio 段与音频
  文件存在性；
- 正常播放是整条 track 的 plan 模式，不按 cue 混合 plan/runtime 时长；
- 若整个 plan set 缺失或校验失败，Android 与 Unity 都切换到清晰的整轨
  兼容回退：Android 使用 runtime cue 时间，Unity 由
  `CueTimelineCompatibilityPlanFactory` 把同一 runtime cue 时间整体适配成
  plan 形状后仍由 `CueCoursePlaybackController` 执行。不存在第二套播放调度引擎；
- `LoadGameWithRoot` 携带 Android 实际选择的模式，避免两端各自猜默认值；
- Unity 状态回报实际 `cueTimelinePlanActive`，Android 发现“本地期望 plan、
  Unity 实际未进入 plan”时，必须把整条 track 重载为 runtime 兼容回退，不能
  让 Android plan 时间轴和 Unity compatibility 时间轴并行。

### 5.5 空分组章节的显示规则

当前 quick/full 的章节审计结果为 0 个空章节：每个 `group_path` 节点都至少
有一条直属 cue。full 的两个父章节也各有独立标题 cue：

- `2 设置` -> `setup/title_2` + `setup.intro.mp3`；
- `3.2 发展卡牌与贵族` -> `cards_group/title_3_2` +
  `action.cards.group.mp3`。

Android 仍保留通用兜底显示规则：若未来新增了只有子章节、没有直属 cue 的
分组节点，时间轴会把该分组标题合并进子标题，章节面板会提升其子节点，避免
渲染空目录。真实 `group_path` 仍用于 seek、当前章节高亮和 QA context。

### 5.6 旧路径清理结果

已删除或收敛：

- Unity `TutorialCuePlayer.SectionPlayback.cs` 旧 section/audio-plan 执行路径；
- `TutorialCuePlayer` 的逐 cue feature flag、Phase 3 allowlist 和合成样例；
- `pause_after_speech_seconds` / `hold_after_audio` 编译字段与运行时读取；
- `IsLegacyTitleCue` / `LastSpokenWordEnd` 驱动的暂停跳转；
- `preserveSectionAudioPreRollOnNextPlay` / `sectionAudioPreRolledCueIndex`
  预卷补丁；
- `playback_group` 共享音源交接字段；
- 旧 Unity `CueTimelineLegacyPlanFactory` / `CueTimelineFeatureGate`；
- Python legacy runtime adapter 和 `build_section_runtime.py` 旧构建入口。

Cue 完成只由 plan duration 与权威 cue clock 决定。音频执行器即使发出
segment 完成诊断，也不会让控制器提前完成或自动进入下一条。

### 5.7 测试入口

```bash
python3 animation/build_cue_timeline.py --game splendor --track quick
python3 animation/build_cue_timeline.py --game splendor --track full
python3 -m unittest animation.test_build_cue_timeline -v
python3 -m unittest animation.test_cue_timeline -v
python3 -m unittest discover -s animation -p 'test_*.py'
TMPDIR="$(pwd)/.tmp-dotnet" /mnt/d/dotnet/dotnet.exe run --project tests/AnimationModelTests
python3 tools/ops/check_unity_scripts.py --json
cd clients/android && gradlew.bat testDebugUnitTest
python3 tools/content/build_content_manifest.py --game splendor
```


## 1. 调查证据（历史背景）

> 以下内容记录迁移前的调查结论；其中“仍需录音确认”等表述是当时风险记录，
> 不是 Phase 4 当前验收要求。当前状态见 4.7 与 13.5。

以下结论基于当时 `main` 上的 runtime、compiled 和播放器代码。

### 1.1 已证实：runtime 与动画 compiled 各自计算 cue 时长

`animation/build_section_runtime.py` 的 cue 时长来自音频窗口：相邻 cue 的 `audio_start` 差，最后一条取 section 音频剩余长度。

`animation/compile_animation_v2.py` 的 cue 时长来自动画事件和 clip 的最晚结束时间：

```text
compiled cue.duration = max(event.at + event.lead + event.dur,
                            clip.at  + clip.lead  + clip.dur)
```

两者没有合并。Splendor quick 的实际样例：

| cue | runtime duration | compiled duration | 备注 |
| --- | ---: | ---: | --- |
| `quick_intro/title_1` | 2.295 | 3.080 | runtime 为 clip 0–1.295 + silence 1.0 |
| `quick_intro/001` | 9.392 | 10.296 | 正文 section audio |
| `quick_cards/001` | 8.285 | 9.264 | 正文 section audio |
| `quick_cards/002` | 13.050 | 13.056 | 几乎相等 |

这说明“音频结束 && 动画结束”不能只在 Unity 内实现；否则 Unity 的实际播放时长会超过 Android 使用的 runtime cue duration。

### 1.2 已证实：正文 cue 的尾部截断来自旧兼容暂停路径

当前编译产物给 quick 的正文 cue 也写入了：

```json
"pause_after_speech_seconds": 1.0
```

`TutorialCuePlayer.SectionPlayback.cs` 中旧路径会在最后一个字幕 word 结束附近：

1. `audioSource.Pause()`；
2. 在 Unity 里等待 `pause_after_speech_seconds`；
3. 将 `audioSource.time` 直接设为下一条 cue 的 `audio_start`；
4. `audioSource.UnPause()`。

这与字幕显示、音频输出缓冲和 MP3 解码边界叠加后，会截断最后几个字。`quick_cards/001` 的样例中：

- cue-local 最后字幕 word 结束：7.275；
- runtime cue duration：8.285；
- 下一条 `audio_start`：10.885。

旧路径在尾部语音附近暂停并跳到下一段音频起点，用户实际听到“永久”后直接进入下一句，属于播放调度/提前停止问题，不是 TTS 素材缺字。

### 1.3 强证据：首字丢失与运行时内容版本及精确 seek 有关

调查时测试机 active runtime 仍是旧版：

- `schema_version = 2`；
- `quick_intro/title_1` duration = 2.080；
- 没有 `audio_plan`。

更新到 schema 3 的 audio plan 后，`title_1` duration = 2.295，并观察到播放日志：

```text
preroll start ... source=1.900 audio_start=2.080
preroll handoff ... source=2.113 audio_start=2.080 isPlaying=True
```

交接时音频源已经越过 2.080 的“璀”起点，没有再发生二次 seek。

目前可以确定的是：

- 旧内容没有走 audio plan 分支；
- 播放器从连续 section 音频中切到正文时，曾经在正文首字起点做 `AudioSource.time` 定位；
- 修掉定位路径后没有再出现同一现象。

仍需在目标 Android 设备上通过实际录音/波形确认 MP3 解码帧定位是否是唯一的底层原因。本文不把“MP3 解码必然丢首帧”当作既定结论，只把它列为待输出验证的高概率假设。

### 1.4 已证实：Android 进度条只认 runtime cue 时长

`TutorialTimeline.load()` 从 runtime 读取每条 cue 的 `start` 和 `duration`，并直接生成：

- 课程总时长；
- cue/章节 seek；
- 进度条映射；
- QA context 的 cue 时间窗。

它不读取 compiled cue 的动画时长。当前播放器如果为了等动画而额外停留，Android 进度条不会知道这段停留，必然产生时间轴漂移。

### 1.5 已证实：播放器存在三条执行路径和多个时钟来源

当前 `TutorialCuePlayer` 至少有：

- 普通单音频 cue 路径；
- `section_audio` 路径；
- `audio_plan` 路径。

时间来源包括：

- `audioSource.time`；
- `audioPlanLocal`；
- `Time.deltaTime` 驱动的静音和动画 hold；
- `isPaused`、`animationHoldActive` 等全局 bool；
- `AudioSource.isPlaying` 循环退出条件。

这使暂停、seek、自动下一条和异步加载互相影响，且没有统一的状态机。

### 1.6 待验证：异步加载和旧回调覆盖新 cue

`PlayCue()` 通过 `StopCoroutine()` 取消旧协程，但没有显式 playback generation / cancellation token。

当前代码中的加载回调多数是协程局部变量，不能直接证明存在旧回调覆盖新 cue；但由于没有 generation 检查，也没有显式取消 UnityWebRequest 的统一机制，这仍是必须通过测试排除的风险。

## 2. 协议目标

### 2.1 必须区分三种时间

| 时间 | 含义 | 示例字段 |
| --- | --- | --- |
| 课程时间 | 整条 tutorial 的全局时间 | `course.start`, `course.duration` |
| cue 时间 | cue 内部局部时间，从 0 开始 | `cue_start` |
| 素材时间 | 原始音频/动画素材自身时间 | `source_start`, `source_end` |

所有时间使用秒；边界统一为半开区间 `[begin, end)`。编译源允许毫秒精度输入；音频执行层会将音源范围解析到采样边界，并在 compiled plan 中记录采样位置或确定性换算方式。

### 2.2 cue 是一个局部时间轴

cue 由若干 track 和 segment 组成，不再由一个全局 audio_start + duration 隐式定义。

首版支持：

- 一个视觉轨（可包含多段动画和 hold）；
- 一个音频轨（可包含多段 clip 和 silence）；
- 多条同类轨作为协议扩展，首版运行时可以只实现单轨；
- 没有音频的 cue；
- 没有动画的 cue；
- 纯停留 cue；
- cue 内并行：音频和视觉同时进行；
- cue 内顺序：同一轨内多段排列；
- 显式 cue duration 可以长于所有轨道内容。

### 2.3 cue 长度

cue duration 按以下顺序确定：

1. 若源数据显式声明更长 duration，使用显式值；
2. 否则使用所有 track segment 的最晚结束时间；
3. 若显式 duration 小于内容结束时间，编译报错；
4. 需要裁切时必须显式写 cut/clip，不允许播放器静默截断。

cue 内 segment 的 `cue_start` 不允许依赖 `cue_end`，避免：

```text
cue duration 依赖 segment 末尾
segment 末尾又依赖 cue duration
```

需要“所有片段结束后再等 1 秒”时，用 `after_all` 锚点引用片段，而不是引用 cue_end；或者直接写显式 cue duration。

## 3. 编译后协议草案

示例数据见：

- `tests/fixtures/cue_timeline/source_example.json`
- `tests/fixtures/cue_timeline/compiled_example.json`

### 3.1 cue 顶层

```json
{
  "id": "quick_cards/001",
  "entry": "quick_setup_players/003",
  "course": {
    "start": 88.499,
    "duration": 9.5
  },
  "tracks": [
    {
      "id": "audio.main",
      "kind": "audio",
      "tail_policy": "silence",
      "segments": []
    },
    {
      "id": "visual.main",
      "kind": "visual",
      "tail_policy": "hold_last_frame",
      "segments": []
    }
  ]
}
```

字段说明：

- `course.start`: 课程全局起点，由编译器累加得到；
- `course.duration`: cue 的有效总时长；
- `tracks`: 并行轨道列表；
- `tail_policy`: 轨道内容结束后、cue 仍未结束时的行为。

### 3.2 音频 segment

```json
{
  "id": "audio.a1",
  "type": "audio_clip",
  "asset": "media/tts/quick/quick.cards.mp3",
  "cue_start": 0.0,
  "source_start": 2.600,
  "source_end": 10.885,
  "source_start_sample": 62400,
  "source_end_sample": 261240
}
```

```json
{
  "id": "audio.gap1",
  "type": "silence",
  "cue_start": 8.285,
  "duration": 1.0
}
```

约定：

- `cue_start` 是 cue 局部时间；
- `source_start/source_end` 是素材自身时间；
- clip 的 cue 内持续时长 = `source_end - source_start`；
- silence 的持续时长 = `duration`；
- 同一音频轨内 segment 不得重叠；
- 编译后可附带采样位置，执行器优先使用采样位置，避免解码器 seek 误差。

### 3.3 视觉 segment

```json
{
  "id": "visual.v1",
  "type": "animation",
  "animation": "quick_cards/001#main",
  "cue_start": 0.0,
  "source_start": 0.0,
  "source_end": 9.264,
  "after_end": "hold_last_frame"
}
```

```json
{
  "id": "visual.hold1",
  "type": "hold",
  "cue_start": 9.264,
  "duration": 1.0,
  "pose": "last_frame"
}
```

约定：

- `animation` 引用 compiled animation clip，而不是源脚本字符串；
- animation 的 cue 内持续时长 = `source_end - source_start`；
- `after_end` 可取 `hold_last_frame` 或 `clear`；
- `hold` segment 明确冻结最后一帧；
- 视觉轨为空或没有 active segment 时，由 `tail_policy` 决定保持还是清空；
- 视觉动画不能暂停、跳转或停止音频执行器。

### 3.4 字幕

字幕仍可作为 cue 内的独立时间数据保留：

```json
{
  "id": "subtitle.s1",
  "type": "subtitle",
  "cue_start": 4.58,
  "duration": 3.7,
  "text": "左上角是分数，右上角是永久折扣。",
  "words": []
}
```

字幕的 word timing 只用于：

- 显示；
- 语义锚点；
- 编译期诊断/验证。

字幕时间不得作为音频停止、裁切或 cue 完成的权威边界。

### 3.5 源创作层锚点

首版只支持编译期可解析的锚点：

```json
{ "anchor": { "type": "cue_start", "offset": 0.0 } }
{ "anchor": { "type": "after", "segment": "audio.a1", "delay": 1.0 } }
{ "anchor": { "type": "after_all", "segments": ["visual.v1", "audio.a1"], "delay": 1.0 } }
```

编译规则：

- 所有引用必须存在；
- 锚点图必须无环；
- 不支持运行时才能确定的交互等待；
- 不允许静默回退到 0；
- 解析后的 compiled plan 只包含确定的 `cue_start` 数字。

## 4. Cue 状态机

运行时状态：

```text
Idle
  -> Preparing
  -> Ready
  -> Playing
  -> Paused
  -> Seeking
  -> Completed
  -> Cancelled
  -> Failed
```

合法转换：

| From | To | 条件 |
| --- | --- | --- |
| Idle | Preparing | 请求加载 cue |
| Preparing | Ready | 所有必需资源加载成功 |
| Preparing | Failed | 资源缺失/解码失败/计划非法 |
| Ready | Playing | 播放请求，资源已就绪 |
| Playing | Paused | 用户暂停 |
| Paused | Playing | 用户恢复 |
| Playing/Paused/Completed | Seeking | 用户 seek；可原地重调度 |
| Seeking | Playing/Paused | seek 完成，保持原播放意图 |
| Playing/Seeking | Completed | 权威时钟到达 cue duration 且只触发一次 |
| Any non-terminal | Cancelled | 切 cue、卸载、重载 |
| Any | Failed | 不可恢复的执行错误 |

硬性规则：

- 加载失败不是自然完成；
- 用户暂停时不得推进 cue 时钟；
- `AudioSource.isPlaying == false` 只能作为诊断信号，不能作为完成事件；
- 完成通知必须带 playback generation，且只发一次；
- 旧 generation 的加载完成、协程回调、音频完成事件全部丢弃；
- seek 后已调度的音频必须取消或重排；
- 禁用动画渲染不得改变 cue 时长、音频执行或完成时间。

## 5. 权威时钟与执行器职责

### 5.1 权威时钟

推荐以音频 DSP 时间为主时钟：

```text
cueTime = (dspTime - cueStartDspTime) - accumulatedPauseDsp
```

要求：

- 音频 clip 用 `PlayScheduled` 或等价方式调度到确定 DSP 时间；
- 静音、hold、字幕和动画时间也从同一个 cue clock 派生；
- 暂停时记录 DSP 时间，恢复时重设调度基准；
- cue 完成由计划中的 `cue.duration` 触发，不依赖 `AudioSource.isPlaying` 抖动；
- 音频真实输出与计划不一致时进入错误/恢复路径，不静默截断。

### 5.2 职责

- `CuePlanCompiler`：解析源锚点，计算 cue/segment 时间，校验引用和时长，输出 compiled plan。
- `CuePlaybackController`：状态机、权威时钟、generation、完成事件。
- `AudioTrackExecutor`：加载/调度/取消音频 segment；不知道标题、QA、字幕业务。
- `VisualTrackExecutor`：加载动画 clip、按 cue time 求帧、处理 hold；不控制音频。
- `SubtitleExecutor`：按 cue time 选择字幕；不控制音频。
- Android `TutorialTimeline`：只读取编译后课程时间；不推导音频文件位置。

### 5.3 预加载

- 可以预加载下一条 cue 的资源；
- 预加载不得出声；
- 预加载不得接管当前 `AudioSource` 或视觉执行器；
- 相邻 cue 可共享只读缓存，但播放实例必须独立。

## 6. 音频边界方案

需要比较并验证：

1. 构建期切分为独立音频片段；
2. 运行期预解码后按采样取片段；
3. 运行期解码整段并使用采样级调度。

首版建议：

- 构建期按 compiled audio segment 生成精确片段，片段文件从 0 开始播放；
- 原始素材只作为源，不再在运行时对 MP3 做 cue 内任意位置 seek；
- 每段记录源采样起止，构建时校验片段完整包含应播语音；
- 资源格式优先选择可确定性解码/预览的格式；内存与下载体积通过抽样测量后决定。

禁止：

- 用固定 50ms/100ms 预卷作为长期方案；
- 用最后一个字幕词自动裁剪；
- 为了追上时间轴静默丢弃音频尾音。

## 7. 迁移与分阶段改动

> 本节保留迁移历史。Phase 0–3 的“未接入/仅 Unity 验证”等描述是当时状态，
> 已被 Phase 4 的 quick track 接线取代；当前验收边界以 4.7 为准。

### Phase 0：协议与基线（历史）
- 提交本协议；
- 提交 source/compiled 示例数据；
- 建立调查证据和验收矩阵；
- 不改播放行为。

### Phase 1：纯编译器与适配层（历史，已完成）
- 新增 `animation/cue_timeline.py` 纯 Python 模块；
- 将旧 runtime + compiled 通过适配器转换为统一 plan set；
- 编译器解析绝对时间、`cue_start`、`after`、`after_all` 锚点，计算时长并校验循环/越界/重叠；
- 增加 `animation/test_cue_timeline.py` 纯 Python/JSON 测试；
- 旧播放器继续运行，尚未读取新 plan。

### Phase 2：纯播放控制器（历史，已完成）
- 在 Unity-independent Core 中实现状态机、generation、权威 cue-local 时钟；
- 定义音频、视觉、字幕执行器接口与 fake executor 测试；
- 不接 Unity；
- 覆盖暂停、seek、加载失败、快速切换、取消和完成只发一次；
- 旧 `TutorialCuePlayer` / `TutorialAnimPlayer` 仍不读取新控制器。

### Phase 3：Unity executor 与 feature flag（历史；Android 与 track 模式已在 Phase 4 接入）
- 实现音频/视觉/字幕执行器；
- 先只迁移：
  - 一个标题 cue；
  - 一个普通正文 cue；
  - 一个合成多段音画 cue；
- 新旧路径通过 feature flag 共存；
- 旧 `section_audio`、`audio_plan` 特判不再新增逻辑。
- 当时目标设备真实音频输出验证未完成；Phase 4 用户主观验收已完成，但未保留录音/波形证据。

### Phase 4：quick track 共享 plan 与 Android 时间轴切换（已完成）
- `quick.audio-slices.json` 作为唯一固定时间轴；
- `TutorialTimeline` 在 plan 模式读取共享 plan 的 course start/duration；
- 章节跳转、进度条、seek 与 QA context 使用同一时间轴；
- Unity 与 Android 读取同一 plan set，任一校验失败整条 track 回退 legacy；
- 只覆盖 quick，未覆盖 full。

### Phase 5：全量迁移与清理（已完成）
- full track 迁移到共享固定时间轴与 plan；
- 移除旧 pause/hold/section-audio/audio-plan 双路径；
- 清除旧 per-cue 特殊时长路径；
- 多 visual animation segment 使用 compiled cue id + source range，并覆盖
  编译器、纯 resolver、Unity executor/compile 测试；
- 清理标题特判和共享音源交接标记。

## 8. 验收矩阵

| 编号 | 场景 | 验证层 |
| --- | --- | --- |
| A1 | A 播完停留 1 秒，B 首字完整 | 设备真实音频 |
| A2 | A 播完停留 1 秒，A 尾字完整 | 设备真实音频 |
| A3 | 音频短于动画 | 编译器 + fake clock + 设备 |
| A4 | 动画短于音频 | 编译器 + fake clock + 设备 |
| A5 | 两组音画之间插入停留 | 编译器 + fake clock + 设备 |
| A6 | 动画中间定格，声音继续 | 编译器 + fake clock + 设备 |
| A7 | 纯音频 / 纯动画 / 纯停留 | 编译器 + fake clock |
| A8 | 语音、静音、定格、cue 边界处暂停/恢复 | fake clock + 设备 |
| A9 | 快速连续跳转，旧加载不覆盖新 cue | fake clock + 设备 |
| A10 | 顺序播放 vs 直接 seek 到同一时间点 | fake clock + 设备 |
| A11 | 连续多次切换无重复播放、提前出声、累计漂移 | fake clock + 设备长序列 |

关键不变量：

- 给 A 增加 1 秒停留，只改变 B 的课程开始时间；
- B 的素材范围、cue 内编排、应播语音不变；
- 若 B 显式依赖 A 的状态，状态依赖单独验证，不和时间轴偏移混在一起。

验证分层：

1. 协议/编译器测试；
2. 使用可控时钟和 fake executor 的播放器测试；
3. 目标 Android 设备真实音频检查。

日志正确不等于音频正确。丢字必须检查实际输出。

## 9. 待确认项

- 首版是否允许同一 kind 的多条并行 track；
- 音频精确执行选择构建期切片还是运行期预解码；
- 动画 clip 的引用粒度：完整 compiled cue、compiled cue 的子区间，还是独立 animation clip；
- 源数据中是否强制显式写 audio source range，还是允许编译器从旧 runtime 适配并给出警告；
- 显式 cue duration 缺失时是否允许自动取最大值，还是要求作者始终显式声明；
  Phase 1 编译器当前按协议自动取所有 segment 结束时刻的最大值。

## 10. Phase 1 实现入口与边界

实现文件：

- `animation/cue_timeline.py`
- `animation/test_cue_timeline.py`

公共 API：

```python
from animation.cue_timeline import (
    CueTimelineError,
    compile_cue_source,       # tutorial-cue-source/v1 -> tutorial-cue-plan/v1
    validate_cue_plan,        # 单个 tutorial-cue-plan/v1 校验
    validate_plan_set,        # tutorial-cue-plan-set/v1 校验
    adapt_legacy_runtime,     # legacy runtime + 可选 compiled -> plan set
)
```

`compile_cue_source(source, course_start=..., sample_rate=...)` 只做纯 JSON
转换，不读写文件。显式 `sample_rate`（或 source 顶层 `sample_rate`）存在时，
`audio_clip` 会附带 `source_start_sample` / `source_end_sample`；采样位置按最
近采样点换算。缺少显式时长时，编译时长取所有 segment 结束时刻的最大值。

`adapt_legacy_runtime(runtime, compiled=None)` 返回
`tutorial-cue-plan-set/v1`：

- `audio_plan` 的 clip/silence 顺序转换为 audio track；
- `section_audio` 和普通单音频 cue 转为 `[audio_start, audio_start + duration]` 的 audio clip；
- 有 compiled cue 时生成 visual animation segment，源区间为
  `[0, compiled cue.duration]`；
- cue duration 取旧 runtime duration、音频内容时长、compiled animation duration
  的最大值；
- 全局 cue start 按 effective duration 重新累加，只保留旧 cue 之间原本存在的显式 gap；
- 缺失 compiled cue 时输出 `missing_compiled_animation` diagnostic 且不生成 visual track；
- compiled cue 时长为 0 时输出 `empty_compiled_animation` diagnostic，不伪造零长度动画 segment；
- 输入 runtime/compiled 不被修改。

Phase 1 不接入任何播放器，也不改变构建产物：

- Unity `TutorialCuePlayer` / `TutorialAnimPlayer` 仍读取旧 runtime/compiled；
- Android `TutorialTimeline` 仍只读旧 runtime cue duration；
- 未实现音频切片、真实音频执行、播放器状态机、generation 或权威时钟。

测试命令：

```bash
python3 -m unittest animation.test_cue_timeline -v
python3 -m unittest discover -s animation -p 'test_*.py'
```

## 11. Phase 2 实现入口与边界（历史）

状态：**Phase 2 纯控制器已完成。本节的“Unity/Android 尚未使用”是当时状态；
Phase 4 已让 Unity track 模式与 Android TutorialTimeline 使用共享 plan。**

实现文件：

- `clients/unity/Assets/Scripts/Tutorial/Animation/Core/CueTimelineTypes.cs`
- `clients/unity/Assets/Scripts/Tutorial/Animation/Core/CuePlaybackController.cs`
- `tests/AnimationModelTests/Program.cs`（fake executor、可控时钟与行为测试）

这两个 Core 文件不 `using UnityEngine`，也不引用 `AudioSource`、`MonoBehaviour`、
`Coroutine` 或 DSP API。

### 11.1 纯 Core API

- `CuePlaybackController`：唯一 cue-local 时钟、播放状态机、generation 和取消；
- `CuePlaybackStateMachine.CanTransition(from, to)`：显式合法转换表；
- `CueTimelinePlan` / `CueTimelineCourse` / `CueTimelineTrack` /
  `CueTimelineSegment`：与 `tutorial-cue-plan/v1` 的绝对时间字段对应的纯 DTO；
- `CueTimelinePlanValidator`：纯控制器消费前的结构校验（不解析创作锚点）；
- `ICueClock` / `ManualCueClock`：可注入的权威时间源，测试可控；
- `IAudioCueExecutor`：`Prepare` / `Play` / `Pause` / `Resume` / `Seek` /
  `Stop` / `Cancel`；
- `IVisualCueExecutor`：`Prepare` / `Seek` / `Pause` / `Resume` / `Stop` /
  `Cancel`，不控制音频；
- `ICueSubtitleExecutor.GetState(plan, cueTime)`：只按 cue-local t 返回字幕状态；
- `ICueExecutorCallback`：`OnPrepared` / `OnCompleted` / `OnFailed`，全部携带
  generation。

控制器公开的关键观测点：

- `State`、`Generation`、`Plan`、`CueTime`；
- `CurrentSubtitle`；
- `LastCompletion`、`CompletionCount`；
- `LastFailure`、`AudioCompletionSignalCount`；
- `StateChanged`、`Completed`、`Failed` 事件。

### 11.2 状态与完成语义

合法转换以 `CuePlaybackStateMachine` 为准：`Idle -> Preparing -> Ready ->
Playing <-> Paused`，`Playing/Paused -> Seeking -> Playing/Paused`，
`Playing/Seeking -> Completed`，任意非终态可取消，执行错误进入 `Failed`。
`Completed` 后可以显式 seek，或准备下一条 cue；新 cue 从 `Preparing` 重新开始。

- 只有两个必需 executor 都 `OnPrepared` 后才进入 `Ready`；未就绪时 `Play()`
  返回 false，不改变状态。
- 每次 `Prepare`、`Play`、`Resume`、`SeekTo` 或 cue switch 都推进
  playback generation；旧 generation 的 prepare/completion/error 回调直接丢弃。
- `CueTime` 只在 `Playing` 时由注入时钟推进；`Paused` 冻结，seek 直接设置
  cue-local 目标时间。
- 字幕、视觉 seek 和时间推进全部使用同一个 `CueTime`。
- cue 完成只由 `plan.course.duration` 与权威时钟决定。音频 executor 的
  `OnCompleted` 或 `isPlaying == false` 只作为诊断，不会提前完成 cue。
- 完成事件每个 playback generation 只发送一次并带 generation；executor
  加载失败、用户暂停、取消、音频失败或视觉失败都不会被当作自然完成。
- seek 先取消旧 generation 的音频调度，再按新 generation seek；播放中的
  seek 会重新播放，暂停中的 seek 保持暂停，恢复时不会重复 `Play`。
- 音频或视觉 executor 报告错误时控制器进入 `Failed` 并停止执行器；视觉失败
  同样明确失败，不允许静默继续音频或切 cue。

### 11.3 测试入口

```bash
dotnet run --project tests/AnimationModelTests
```

测试全部使用合成 plan、fake audio/visual executor 和可控 `ManualCueClock`，
不针对任何具体内容 cue。覆盖：

- 未就绪不能播放、加载失败、暂停冻结、seek/恢复调用序列；
- 语音、静音、hold、cue 边界 seek；
- 顺序播放和直接 seek 到同一 cue-local t 的状态一致性；
- `isPlaying=false`/音频完成回调不导致提前完成；
- cue duration 完成只发一次且携带 generation；
- 快速切 cue 和取消后旧回调全部丢弃；
- 音频失败与视觉失败的显式失败行为；
- 多 cue 连续播放无重复完成或累计时钟漂移。

### 11.4 Phase 3 仍需完成（历史）

以下清单记录 Phase 2 当时尚未完成、后来由 Phase 3/4 补上的工作：

- Unity 侧真实 `AudioSource` / `AudioClip` executor，包括加载、分段调度、
  暂停、恢复、seek 和 stop；
- 基于 `AudioSettings.dspTime` 或等价 DSP 时钟的 `ICueClock` 实现；
- 真实资源准备、预加载和底层取消（UnityWebRequest / 音频解码 / 动画 clip）；
- 目标设备上的真实音频输出验证（首尾字、静音、hold、跨 cue 拼接）；
- Android `TutorialTimeline` / 播放器接入 compiled plan 的课程时间；
- feature flag、新旧播放路径共存以及最终迁移清理。

## 12. 协议问题状态（历史待确认项）

- 音频执行方式已由现有 Unity PCM executor 方案承接；Phase 4 不改变执行方案；
- plan set DTO、加载、track 开关和 Android/Unity 接线已在 Phase 4 落地；
- Android `TutorialTimeline` 已在 plan 模式读取共享 plan 的课程时间；
- 用户已完成 quick 主观验收；录音/波形级验证未保留，不是 Phase 4 阻塞项；
- 字幕已成为显式 track segment，由 plan 执行器消费。


## 13. Phase 3 实现范围与边界（历史）

状态：**当时 Unity 代码已接线，feature flag 默认关闭，Android 尚未接入。**
Phase 4 已接通 quick track 的 Android/Unity 共享 plan，并以用户主观验收作为
本阶段验收；本节保留为历史实现记录。

### 13.1 Feature flag 与迁移样例

`TutorialCuePlayer` 新增：

```csharp
public bool useCueTimelineV1 = false;
public List<string> cueTimelineCueIds = new List<string> {
    "quick_intro/title_1",
    "quick_intro/001",
    "quick_take_gems/003",
};
public List<string> cueTimelineCompositeCueIds = new List<string> {
    "quick_take_gems/003",
};
public float cueTimelineCompositeHoldSeconds = 0.75f; // 显式 authored hold
```

选择顺序：

1. `useCueTimelineV1 == false`：完全不进入新路径，所有 cue 继续走原有
   `section_audio` / `audio_plan` / 单音频路径；
2. flag 为 true 但 cue id 不在 `cueTimelineCueIds`：继续旧路径；
3. flag 为 true、allowlist 命中，但 plan 生成或 `CueTimelinePlanValidator`
   校验失败：记录 `[CueTimelineV1] plan generation failed ... fallback=legacy`，
   继续旧路径；
4. allowlist 命中且 plan 合法：进入 `CuePlaybackController`。

进入新路径后，资源加载失败进入 `Failed`，不会自动下一条，也不会回退到旧
播放器；用户显式切换 cue 或重载内容后才会离开失败状态。

### 13.2 Unity 执行器职责

- `UnityCueAudioExecutor`
  - 使用独立 `AudioSource`，不复用旧播放器的播放状态；
  - 加载 compiled plan 的 `audio_clip`/`silence` segments；
  - 每次异步加载、成功、失败都携带 generation；
  - `Cancel` 停止协程、`Abort`/`Dispose` in-flight `UnityWebRequest`；
  - 音频段选择由 controller 的 `CueTime` 经 `SyncToCueTime` 驱动；
  - 只把音频结束回调作为诊断信号，不完成 cue。
- `UnityCueVisualExecutor`
  - 复用 `TutorialAnimPlayer.LoadCue/Seek`；
  - 把 cue-local time 映射成 animation `source_start + local`；
  - `hold` 固定到最后一个 animation `source_end`；
  - 不控制音频、不推进 cue 时钟、不完成 cue。
- `CueTimelineSubtitleExecutor`
  - 只按 plan 中的 cue-local subtitle window 返回字幕；
  - 不控制音频、不裁切音频、不触发完成。
- `UnityDspCueClock`
  - 向 controller 提供单调 `AudioSettings.dspTime`；
  - cue-local 权威时间仍只由 `CuePlaybackController` 维护。
- `CuePlaybackController.PlayAt(startCueTime, startPaused)`
  - `PlayCueAt` 的起点和原生暂停态直接传给 controller；
  - paused start 只对 audio executor 调 `Seek`，不调 `Play`，避免原生拖动时
    出现一帧出声。

### 13.3 音频边界方案比较与当前选择

| 方案 | 优点 | 风险/限制 | Phase 3 结论 |
| --- | --- | --- | --- |
| `AudioSource.time` 定位 | 简单，现有代码已用 | float 秒、解码器/帧边界行为不透明，无法证明首尾字 | 不作为新路径主方案 |
| `AudioSource.timeSamples` | 整数采样定位，可用 clip.frequency 做确定性换算 | MP3 在 Unity/Android 解码后的 seek 仍会受 decoder priming/帧边界影响，可能吞首字 | 弃用为 segment 起点方案 |
| 构建期切独立音频片段 | 片段从 0 播放，最容易做到可复现边界 | 需要新增切片产物、资源清单、版本与体积策略 | 长期推荐候选 |
| 运行期预解码 PCM / `AudioClip.Create` | 采样级完全可控，segment 从 sample 0 播放，不使用运行时 compressed seek | 内存、解码耗时、资源生命周期需要约束 | **当前实现主方案** |

当前 Unity 音频执行器：

- 依据 controller 的 cue-local 时间选择 audio segment；
- 首次使用某个 asset 时用 `AudioClip.GetData` 预解码 PCM，并按 segment
  `source_start/source_end` 切出独立内存 clip；播放时每个 segment 从 sample 0
  开始，避免 MP3 decoder seek 吞首字；
- 仅在同一 PCM segment 内做样本级 seek，不添加固定 pre-roll/post-roll；
- 不读取字幕最后一个 word 作为音频裁剪边界；
- 在 prepare 阶段拒绝 `source_end > clip.length + 0.02` 的 clip，避免静默截断；
- 每次切换 segment 都从 controller 当帧 cue time 重新定位，而不是依赖第二条
  独立播放时钟。

已做的静态/纯测试证据：

- plan factory 保留 section audio 的 `source_end = audio_start + runtime_duration`；
- 24 kHz 下采样换算误差上界为 0.5 sample，即约 0.0208 ms；
- title/body/composite 三种 plan 的 controller 调用序列由
  `tests/AnimationModelTests/Program.cs` 覆盖；
- synthetic silence、seek、hold 和音频/视觉不同时结束由 fake clock 覆盖；
- 音频 executor 的 `OnCompleted` 在 controller 中只增加诊断计数，不会完成 cue。

**未验证：**

- Android 真机上 MP3 decode seek 是否在 `timeSamples` 目标处精确输出；
- `AudioSource.timeSamples` 在目标 Unity/Android 音频后端上的实际首帧行为；
- 静音段到下一 clip、pause/seek/resume、跨 cue 边界的真实波形；
- 扬声器/耳机输出是否出现重复、提前或吞字。

因此不能把“静态编译通过”或 fake clock 测试当作丢字问题最终修复。feature flag
保持默认关闭。

### 13.4 Android 真机听测/录音步骤（历史，不是 Phase 4 阻塞项）

> 本节只保留 Phase 3 的验证记录。用户已完成 Phase 4 quick 主观验收，当前
> 不再要求录音/波形证据，也不要求用户按本节重新执行设备步骤。

准备：

```bash
adb logcat -c
adb logcat -s Unity > cue-timeline-device.log
```

在 Unity Inspector 中只对测试内容打开：

- `useCueTimelineV1 = true`；
- allowlist 含 `quick_intro/title_1`、`quick_intro/001`、
  `quick_take_gems/003`；
- 其余 cue 保持不在 allowlist。

测试 A：标题 cue 与下一 cue 首字

1. 从 `quick_intro/title_1` 开头播放并录音；
2. 确认标题语音完整、1 秒 silence 存在；
3. 确认下一 cue `quick_intro/001` 首字“璀”完整；
4. 检查日志中 title 的 `[CueTimelineV1][Audio] play`、silence 切换点、
   `[CueTimelineV1][Complete]` 以及下一 cue 的 `prepare/play` generation。

测试 B：普通正文尾部

1. 单独从 `quick_intro/001` 开头播放并录音；
2. 重点听最后一句尾部“获胜。”是否完整；
3. 不应出现旧路径的 `pause_after_speech_seconds` 提前暂停或跳到下一段；
4. 确认完成后才出现 `[CueTimelineV1][Complete]`，且音频 executor 的
   `OnCompleted` 日志不能先于 controller completion。

测试 C：合成多段音画 cue

1. 播放 `quick_take_gems/003`；
2. 检查两段 audio clip + 中间 silence 的拼接；
3. 检查 visual animation 结束后进入 `hold`，而 audio 已先结束；
4. 在 clip 边界、silence、hold、cue 末尾分别 pause/resume/seek；
5. 确认没有重复出声、提前出声或静音段漏出相邻语音。

测试 D：快速切换

1. 在 prepare/加载过程中快速连续 `PlayCueAt` 到不同 allowlist cue；
2. 日志中旧 generation 的 prepare/success/failure 不得覆盖当前 cue；
3. 确认 Cancel 后没有旧 `UnityWebRequest` 回调继续播放。

日志关键词：

```text
[CueTimelineV1] plan ready
[CueTimelineV1][Audio] play
[CueTimelineV1][Audio] seek-position
[CueTimelineV1][Audio] pause
[CueTimelineV1][Audio] load failed
[CueTimelineV1][State]
[CueTimelineV1][Seek]
[CueTimelineV1][Complete]
[CueTimelineV1][Failed]
```

历史阶段曾要求保留实际录音/波形。Phase 4 的最终声明只按用户已完成的主观
验收记录，不把日志正确等同于采样级验证。

### 13.5 历史状态与 Phase 5 结果

Phase 5 已完成：

- Android `TutorialTimeline` 在 plan 模式读取 `{track}.cue-timeline.json` 的
  course start/duration、cue 顺序和总时长；进度条、章节跳转、QA context 与
  Unity plan 模式共用同一份 quick/full plan；
- full 83 cue 已整轨迁移，catalog 同时启用 quick/full plan；
- 旧 `section_audio` / `audio_plan` / `pause_after_speech_seconds` /
  `hold_after_audio` 生产播放路径已删除；
- 多 visual animation segment 已实现为 compiled cue id + source range，并有
  合成 resolver 测试与 Unity compile 验证；
- 用户仍需对 quick/full 做真机主观验收；自动测试不能替代视觉/听感验收。
