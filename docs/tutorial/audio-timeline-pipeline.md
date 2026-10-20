# Tutorial 音频与时间轴制作流程

本文说明口播素材、固定时间轴、字幕、视觉轨道和课程 plan 的制作与发布。
数据字段见 [Cue Timeline 协议](cue-timeline-protocol.md)，播放行为见
[课程音频播放器](continuous-cue-audio.md)。

## 文件职责

以下路径以 `content/games/<game>/tutorial/` 为根目录。

| 文件 | 职责 |
| --- | --- |
| `tracks/<track>.track.json` | section 顺序和 track 元信息 |
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
| quick | cue-level TTS；逻辑 slice 引用素材区间 | 32 | 206.224 秒 |
| full | section TTS 与独立父章节标题素材 | 83 | 618.399 秒 |

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

full 的切片导入器 `animation/build_audio_slices.py` 采用以下规则：

1. 根据 runtime 的 cue 顺序、音频起点、时长和字幕窗口建立逻辑 slice。
2. 有 word timing 且起点大于零的 slice，使用最多 0.15 秒前置上下文。
   前置量受源文件起点和当前窗口的可用尾部时长约束。
3. 正文 slice 的起点和终点先按同一前置量移动。所有 cue 都采用
   `trim_to_speech_end`：尾部裁到
   `speech_end + trim_tail_guard_seconds`，随后再由 plan 追加显式反应
   停顿。full 用 `trailing_silence_seconds` 追加固定静音；quick 用
   `post_speech_silence_seconds` 从审查后的 speech end 补足到目标值。
   这样 TTS 素材自身的自然尾音不会叠加到反应停顿上。
4. 对原始窗口相接、上一段未显式裁尾的同源片段，统一计算共享边界。
   上一段 end 和下一段 start 相等，边界不早于上一段已知 speech end。
   两段时长按共享边界分配。若右侧 cue 需要 0.15 秒首词前置上下文，
   但左侧 trim 后的终点已经进入这段上下文，右侧起点会夹到左侧终点，
   优先保证源区间不重叠。
5. 将连续、裁尾、作者留空关系写入 `source_boundary_contracts`。

边界契约在固定文件读入和计划策略应用后分别校验：

| kind | 约束 |
| --- | --- |
| `continuous` | 两侧使用同一个整数采样边界 |
| `trimmed_tail` | 允许显式裁尾产生的空隙；区间无重叠 |
| `authored_gap` | 允许作者声明的源区间空隙；区间无重叠 |

`source_boundary_contracts` 为可选字段；full 导入器会生成它。
声明采样率决定固定文件的采样边界校验精度。

full 标题使用 `mode: audio`、`trim_to_speech_end: true`、
`trim_tail_guard_seconds: 0.08` 和 `trailing_silence_seconds: 0.5`。
编译器检查有效 speech end 与非空源区间，先裁尾再追加显式 silence 段。
`setup_players/title_2_4` 的源区间为 `[20.055, 21.385)`，
cue 时长为 1.83 秒。

quick 标题与正文也启用 `trim_to_speech_end`，全部 cue 的 guard 为
0.1 秒；停顿目标为普通 cue 0.5 秒、下一 cue 是章节标题时 1.0 秒。
quick 的 cue-level ASR word end 会低估最后一个音节，不能直接当作裁尾点；
`quick.audio-slices.json` 的 `speech_end_ref` 是制作期从 PCM 能量尾部
生成并落盘的时间戳，策略再从该时间戳加 0.1 秒 guard 裁切，并用
`post_speech_silence_seconds` 补足目标停顿，保证不会裁掉句尾字。

### quick 能量尾部锚点

`animation/measure_audio_tail.py` 只负责制作期测量：它把音频解码成 PCM，
按 10ms 窗口找到 source range 内最后一个有意义能量窗口，把结果写成
`speech_end_ref` 时间戳，并同步最后一个匹配的字幕 end ref。播放器和
`build_cue_timeline.py` 只读这些时间戳，不在运行时或构建时解码音频。

```bash
python3 animation/measure_audio_tail.py \
  --game splendor --track quick \
  --ffmpeg /path/to/ffmpeg \
  --write --guard-seconds 0.1
python3 animation/build_cue_timeline.py --game splendor --track quick
```

`--check` 可以对比已落盘时间戳是否与当前音频测量结果一致。`ffmpeg` 是
显式测量步骤的外部工具，不属于运行时依赖；也可以通过 `FFMPEG_EXE` 或
PATH 提供。具体值以各 track 的固定文件为准。

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

## 生成与发布

full 的 section TTS/runtime 更新后，依次执行：

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
