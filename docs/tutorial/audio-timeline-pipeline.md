# Tutorial 音频与时间轴制作流程

本文说明口播素材、固定时间轴、字幕、视觉轨道和课程 plan 的制作与发布。
数据字段见 [Cue Timeline 协议](cue-timeline-protocol.md)，播放行为见
[课程音频播放器](continuous-cue-audio.md)。

## 文件职责

以下路径以 `content/games/<game>/tutorial/` 为根目录。

| 文件 | 职责 |
| --- | --- |
| `tracks/<track>.track.json` | section 顺序和 track 元信息 |
| `{track}.cue-audio-overrides.json` | 可选的 cue 到 TTS 音频、字幕与源窗口映射；full 覆盖全部 cue |
| `sections/<id>/<track>.script.json` | 口播文字、cue 切分、beats 和 refs |
| `sections/<id>/<track>.anim.json` | 动画事件、素材引用、镜头和 QA |
| `build/<track>.script.json` | section 装配生成的文字与导航中间产物 |
| `<track>.runtime.json` | TTS/字幕窗口、导航元数据；切片导入与整轨兼容加载的输入 |
| `anim/v2/<track>.compiled.json` | 可定位到任意素材时间的编译动画 |
| `<track>.audio-slices.json` | 固定时间点、源区间、字幕 refs、per-cue 策略和视觉映射 |
| `<track>.timing-policy.json` | 固定文件路径、script 路径、默认时长模式等全局配置 |
| `<track>.cue-timeline.json` | Android 和 Unity 共用的课程播放计划 |

口播和动画在 section 源文件中编辑，共享动作、示例与表现放在 `library/`。
`build/` 由装配工具生成。固定时间轴以命名 ID/ref 组织秒数与映射；
生产脚本按字段通用解析。

```text
section 文字、动画与共享素材
  → TTS、runtime、compiled
  → audio-slices（固定时间点、源区间、字幕与视觉映射）
  → build_cue_timeline + timing-policy
  → cue-timeline（课程起点、时长、三类轨道）
  → content manifest
  → Android 导航与 Unity 课程播放器
```

## 音频素材

Splendor 的音频素材采用 seed2 音色 `zh_female_vv_uranus_bigtts`，
资源 ID 为 `seed-tts-2.0`。

| track | 音频组织 | cue 数 | 课程时长 |
| --- | --- | ---: | ---: |
| quick | cue-level TTS；逻辑 slice 引用素材区间 | 32 | 201.168 秒 |
| full | 默认独立 TTS；同句跨 cue 共享连续源 | 83 | 632.095 秒 |

full 的 `setup/title_2` 使用 `media/tts/full/setup.intro.mp3`，
`cards_group/title_3_2` 使用 `media/tts/full/action.cards.group.mp3`。
一个 cue 可引用多段音频；多个 cue 可共享同一素材的连续区间。
素材文件保持完整，plan 使用 `asset + source_start/source_end` 描述所需内容。

quick TTS 生成命令：

```bash
python animation/tts_doubao.py \
  --provider seed2 \
  --voice zh_female_vv_uranus_bigtts \
  --resource-id seed-tts-2.0 \
  --input content/games/splendor/tutorial/quick.cue.tts.lrc \
  --out-dir content/games/splendor/media/tts/quick.cues \
  --track quick --prune
```

section 内容编译入口为：

```bash
python animation/compile_tutorial.py --game splendor --track full
```

生成 TTS 时显式配置 `DOUBAO_TTS_PROVIDER=seed2`、
`DOUBAO_SPEAKER=zh_female_vv_uranus_bigtts`、
`DOUBAO_RESOURCE_ID=seed-tts-2.0`。编译工具也支持 `--dry-run` 与
`--skip-tts`，适合检查影响范围和复用已生成媒体。

## 源区间与切点

音频区间统一使用半开区间 `[start, end)`。TTS word timing 是估计值，
审核首尾字时应结合源音频听感与波形。

quick/full 默认每条 cue 使用独立素材窗口；如果同一句按 cue 切分，则允许
相邻 cue 共享同一素材的连续窗口，并通过 `continuous` 边界和 0 间隔播放。
制作流程如下：

1. runtime 声明素材、音频起点、时长和字幕窗口；full 切片导入器据此建立
   `audio-slices.json` 中的逻辑 slice。
2. 制作期测量 PCM 能量边界，将 speech start/end 写为固定时间点。
   `use_measured_speech_start`、`use_measured_speech_end` 和
   `use_measured_source_window` 控制导入时保留的测量结果。
3. plan 编译器将起点裁到 `speech_start - trim_start_guard_seconds`，
   终点裁到 `speech_end + trim_tail_guard_seconds`，两侧均受源窗口限制。
4. 编译器根据全局间隔目标和源音频已有尾部时长，计算需要追加的 silence。

对于共享素材的内容，导入器支持最多 0.15 秒前置上下文，并为相邻片段统一
源边界。原始窗口相接、上一段未显式裁尾时，两侧共享同一切点且保护上一段
已知 speech end；显式裁尾交界通过起点约束保证无重叠。相关关系记录在
`source_boundary_contracts`。

边界契约在固定文件读入和计划策略应用后分别校验：

| kind | 约束 |
| --- | --- |
| `continuous` | 两侧使用同一个整数采样边界 |
| `trimmed_tail` | 允许显式裁尾产生的空隙；区间无重叠 |
| `authored_gap` | 允许作者声明的源区间空隙；区间无重叠 |

`source_boundary_contracts` 为可选字段；full 导入器会生成它。
声明采样率决定固定文件的采样边界校验精度。

quick/full 全部 cue 启用 `trim_to_speech_start: true` 和
`trim_to_speech_end: true`，首尾 guard 均为 0.1 秒。标题使用 `mode: audio`，
正文使用 `mode: max`。普通 cue 的尾部间隔目标为 0.5 秒，section 最后一条
为 1.0 秒，具体计算见下文。

例如 `setup_players/title_2_4` 的计划源区间为 `[0.25, 2.256)`，
音频段长 2.006 秒，追加 silence 为 0.494 秒，cue 总时长为 2.5 秒。

### PCM 能量首尾锚点与边界校正

TTS word 边界可能明显偏离真实 PCM 语音首尾，full 的正文也有同样问题。
`animation/measure_audio_boundaries.py` 只负责制作期测量：它把音频解码
成 PCM，按 10ms 窗口找到 source range 内第一个持续有意义的能量窗口和
最后一个有意义的能量窗口，分别写成 `speech_start_ref` /
`speech_end_ref` 时间戳，并标记 `use_measured_speech_start/end`。

`--runtime-windows` 模式从 runtime 的 `audio_start/duration` 读取源窗口。
遇到共享素材的相邻 cue 时，工具可在 provider 边界附近 ±0.8 秒寻找足够长的
静音，以静音中点校正源窗口。结果写入 `start_ref` / `end_ref` 并标记
`use_measured_source_window`，供后续导入保留。能量分析由制作期工具执行；
plan 构建读取固定边界并检查媒体时长，播放器解码计划引用的音频。

```bash
# quick 使用 authored cue-level source windows
python3 animation/measure_audio_boundaries.py \
  --game splendor --track quick \
  --ffmpeg /path/to/ffmpeg \
  --write --guard-seconds 0.1
python3 animation/build_cue_timeline.py --game splendor --track quick

# full 使用 runtime 的各 cue audio_start/duration 窗口
python3 animation/measure_audio_boundaries.py \
  --game splendor --track full --runtime-windows \
  --ffmpeg /path/to/ffmpeg \
  --write --guard-seconds 0.1
python3 animation/build_audio_slices.py --game splendor --track full
python3 animation/build_cue_timeline.py --game splendor --track full
```

`--check` 可以对比已落盘时间戳是否与当前音频测量结果一致。`ffmpeg` 是
显式测量步骤的外部工具，不属于运行时依赖；也可以通过 `FFMPEG_EXE` 或
PATH 提供。具体值以各 track 的固定文件为准。

### 自动 cue / section 间隔

`{track}.timing-policy.json` 提供两个全局间隔字段：

```json
{
  "inter_cue_gap_seconds": 0.5,
  "section_end_gap_seconds": 1.0
}
```

`build_cue_timeline.py` 按 runtime cue 顺序和 `section` 边界自动决定：

- 普通 cue 尾部补足到实测 speech end 后 0.5 秒
- section 最后一条 cue 尾部补足到 1.0 秒，再进入下一 section
- 最终 section 的最后一条 cue 也按 1.0 秒处理

这两个值表示从实测 speech end 到音频轨道末尾的目标总间隔。
追加的静音长度为 `max(0, 目标间隔 - 源音频已有尾部时长)`。
`cue_timing` 可用 per-cue `post_speech_silence_seconds` 覆盖目标，或用
`trailing_silence_seconds` 指定固定追加量。`mode: max` 下视觉轨道也参与
cue 时长计算，较长视觉可能延长课程中的实际停留。

## 时长、字幕与视觉

每条 cue 的策略位于 `audio-slices.json` 的 `cue_timing`。
`timing-policy.json` 提供全局 `default_mode`。

| mode | cue 时长与轨道行为 |
| --- | --- |
| `max` | 使用各轨道的最晚结束时间；先结束的视觉按尾部策略保持或清空 |
| `audio` | 使用音频轨道时长，包含显式静音；视觉超出部分裁到 cue 末尾 |
| `visual` | 使用视觉时长；会截断音频时构建失败 |

课程中的 cue 起点按最终时长累加，课程总时长等于最后一个 cue 的终点。
字幕窗口来自 source-absolute refs，编译后转换为 cue-local
`cue_start + duration`，作为独立 subtitle track 展示。

`cue_visuals` 可指定多个 `animation` 或 `state` 段。
动画段引用 compiled cue 及其源时间范围；`state` 在素材时间零应用画面，
适用于瞬发状态；编译 plan 中的 `hold` 段冻结前序画面。`after_end` 支持
`hold_last_frame` 与 `clear`。省略视觉映射时，编译器采用对应 compiled cue
的默认映射。

不同 compiled cue 各自使用编译好的 `start_state`。同一动画素材的多个源区间
可以连续定位。视觉执行器使用课程位置派生的 cue-local 时间进行 Seek。

### 独立 cue 音频覆盖与连续共享源

quick/full 默认每条 cue 使用独立 TTS 音频，素材包含本条口播的起音、收音与
换气。仅当同一句被拆到相邻 cue 时，才允许共享一条完整 TTS 源：两条 cue
分别声明同一 `audio` 的连续 `[audio_start, audio_start+duration)` 窗口，
字幕切分也按该源窗口 offset；构建后该边界必须为 `continuous`，且
`build_cue_timeline.py` 不为它补默认 cue 间隔。quick 按 cue-level 生成；
full 使用 `{track}.cue-audio-overrides.json` 声明音频、字幕和可选源窗口。
格式如下：

```json
{
  "track": "full",
  "cues": {
    "intro/title_1": {
      "audio": "media/tts/full/intro.title.mp3",
      "subtitle_file": "media/tts/full/intro.title.subtitle.json"
    },
    "intro/001": {
      "audio": "media/tts/full/intro.body.mp3",
      "subtitle_file": "media/tts/full/intro.body.subtitle.json"
    },
    "take_gems/public.001.1": {
      "audio": "media/tts/full/cues/take_gems__public.001.b1.mp3",
      "audio_start": 0.0,
      "duration": 5.625,
      "subtitle_file": "media/tts/full/cues/take_gems__public.001.1.b1.subtitle.json"
    },
    "take_gems/public.001.2": {
      "audio": "media/tts/full/cues/take_gems__public.001.b1.mp3",
      "audio_start": 5.625,
      "duration": 2.415,
      "subtitle_file": "media/tts/full/cues/take_gems__public.001.2.b1.subtitle.json"
    }
  }
}
```

runtime 生成器默认写入独立 `audio_start=0` 的源窗口；声明
`audio_start`/`duration` 时写入指定源窗口。同一句的第二个 cue 从上一 cue
的 `audio_end` 开始，`build_audio_slices.py` 生成 `continuous` 契约，
`build_cue_timeline.py` 据此跳过默认间隔。后续
audio-slices/plan 按实测首尾时间戳裁剪。当前 Splendor full 中
`take_gems/limit.001.1` 与 `limit.001.2`、`take_gems/public.001.1` 与
`public.001.2` 两组同句 cue 共享一条完整 TTS 源的连续窗口（边界为
`continuous`，不补 cue 间隔）；其余音频资产由单条 cue 引用。

## 生成与发布

full 的 TTS/runtime 更新后，依次执行：

```bash
python animation/build_audio_slices.py --game splendor --track full
python animation/build_cue_timeline.py --game splendor --track full
```

`build_audio_slices.py` 会根据导入输入重建固定文件；人工编辑的时间点应先确认
是否需要随新素材重新导入。quick 的固定时间轴由作者审核维护：

```bash
python animation/build_cue_timeline.py --game splendor --track quick
```

计划生成器检查引用、数值、段范围、源边界契约和课程覆盖。
相同输入应生成逐字节一致的产物。

内容发布入口：

```bash
python tools/content/build_content_manifest.py --game splendor
```

manifest 包含固定时间轴、全局策略、共享 plan、plan 引用音频、runtime、
compiled、图片，以及 Android QA 使用的 `tutorial/build/<track>.anim.json`，
并记录 SHA-256。缺失依赖会使构建失败。

## 加载与兼容模式

catalog 的 `cue_timeline_tracks: ["quick", "full"]` 指定使用共享 plan 的 track。
Android 校验计划，向 Unity 的 `LoadGameWithRoot` 下发实际模式；Unity 校验
cue 顺序、时长、轨道与媒体，并通过 `cueTimelinePlanActive` 回报状态。

计划缺失、非法或两端模式不一致时，Android 将整条会话加载为 runtime
兼容模式。Unity 适配器将 runtime 转为课程 plan，交给
`CueCoursePlaybackController` 执行。会话内两端使用一致的课程坐标。

## 校验入口

在仓库根目录执行：

```powershell
python -m unittest animation.test_build_cue_timeline animation.test_build_content_manifest animation.test_cue_timeline
dotnet run --project tests/AnimationModelTests
dotnet run --project tests/CourseAudioTransportTests
```

Android 单元测试入口为 `clients/android/gradlew.bat testDebugUnitTest`；
Unity 导出和 APK 构建入口为 `clients/android/build-uaal.bat`。
工具链路径配置见 [Android 工程说明](../../clients/android/README.md)。

设备与测试结果见 [2026-10-20 验收记录](validation/2026-10-20-cue-audio.md)。
