# Cue Timeline Protocol v1

状态：Phase 1 已实现纯 Python 编译器、plan 校验器和旧数据适配层；尚未接入 Unity/Android 运行时，旧播放器行为未改变。

本文先固定 cue 的时间轴协议、状态机、职责边界、迁移顺序和验收方式。实现时可以替换内部类与文件名，但不应在实现过程中重新引入以下临时机制：

- 用字幕最后一个词的时间作为音频裁切边界；
- 标题 cue 特判；
- `pause_after_speech_seconds` + 跳到下一条 `audio_start`；
- 共享 `AudioSource` 交接标记；
- 用 `AudioSource.isPlaying == false` 直接判定自然完成；
- 固定提前量或延后量补偿 MP3 定位。

## 1. 调查证据

以下结论基于当前 `main` 上的 runtime、compiled 和播放器代码。

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

### Phase 0：协议与基线（本阶段）
- 提交本协议；
- 提交 source/compiled 示例数据；
- 建立调查证据和验收矩阵；
- 不改播放行为。

### Phase 1：纯编译器与适配层（已完成）
- 新增 `animation/cue_timeline.py` 纯 Python 模块；
- 将旧 runtime + compiled 通过适配器转换为统一 plan set；
- 编译器解析绝对时间、`cue_start`、`after`、`after_all` 锚点，计算时长并校验循环/越界/重叠；
- 增加 `animation/test_cue_timeline.py` 纯 Python/JSON 测试；
- 旧播放器继续运行，尚未读取新 plan。

### Phase 2：纯播放控制器
- 实现状态机、generation、权威时钟；
- 用 fake audio/visual executor 和可控时钟测试；
- 不接 Unity；
- 覆盖暂停、seek、加载失败、快速切换和完成只发一次。

### Phase 3：Unity executor 与 feature flag
- 实现音频/视觉/字幕执行器；
- 先只迁移：
  - 一个标题 cue；
  - 一个普通正文 cue；
  - 一个合成多段音画 cue；
- 新旧路径通过 feature flag 共存；
- 旧 `section_audio`、`audio_plan` 特判不再新增逻辑。

### Phase 4：Android 时间轴切换
- `TutorialTimeline` 读取 compiled cue plan 的课程时长；
- 章节跳转、进度条、QA context 使用同一时长；
- 直接 seek 与顺序播放结果一致。

### Phase 5：全量迁移与清理
- full/quick 全量转到新 plan；
- 移除 `pause_after_speech_seconds`、`hold_after_audio`、`section_audio` 和旧 audio plan 双路径；
- 移除播放器中的标题特判和共享音源交接标记。

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

## 11. 留给 Phase 2 的问题

- 真实音频执行方式：构建期切片、运行期预解码取采样，或整段解码后采样级调度；
- `CuePlaybackController` 状态机、generation/cancellation token 与完成事件只发一次；
- 暂停、seek、快速切 cue 时音频与视觉执行器如何重调度；
- 哪些类字段进入 Unity C# DTO，以及 plan set 的加载和 feature flag 形态；
- Android `TutorialTimeline` 何时切换为读取 plan set 的课程时间；
- 真实设备上用录音/波形验证音频首尾、静音、hold 和跨 cue 拼接；
- `tutorial-cue-source/v1` 是否应把字幕也提升为显式 track segment，当前 Phase 1
  编译器只接受 visual/audio track 及其已定义的 segment 类型。
