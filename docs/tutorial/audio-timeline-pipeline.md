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
| quick | cue-level TTS；逻辑 slice 引用素材区间 | 32 | 204.322 秒 |
| full | section TTS 与独立父章节标题素材 | 83 | 638.603 秒 |

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
3. 正文 slice 的起点和终点先按同一前置量移动。采用
   `trim_to_speech_end` 的 slice 前移起点，尾部由
   `speech_end + trim_tail_guard_seconds` 决定。
4. 对原始窗口相接、上一段未显式裁尾的同源片段，统一计算共享边界。
   上一段 end 和下一段 start 相等，边界不早于上一段已知 speech end。
   两段时长按共享边界分配。
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
编译器检查有效 speech end 与非空源区间，再追加显式 silence 段。
`setup_players/title_2_4` 的源区间为 `[20.055, 21.385)`，
cue 时长为 1.83 秒。

quick 标题同样采用音频时长与 0.5 秒尾静音，当前
`trim_tail_guard_seconds` 使用默认值 0。正文 cue 也在 `cue_timing`
中统一追加 0.5 秒尾静音；如果正文 cue 的下一 cue 是章节标题，则追加
1.0 秒。具体值以各 track 的固定文件为准。

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
