# Cue Timeline 数据协议

Android 导航、Unity 音频、字幕与视觉使用同一份课程计划。
本文描述固定时间轴与编译 plan 的字段和约束。

- [制作流程](audio-timeline-pipeline.md)
- [课程播放器](continuous-cue-audio.md)
- 校验实现：`animation/build_cue_timeline.py`、`animation/cue_timeline.py`

## 时间坐标

| 坐标 | 含义 | 字段 |
| --- | --- | --- |
| course | 整条课程的绝对位置 | `course.start`、`course.duration` |
| cue-local | 当前 cue 内的位置 | `cue_start`、段 `duration` |
| source | 音频或动画素材内的位置 | `source_start`、`source_end` |

数值单位为秒，要求有限、非负；有效区间为 `[start, end)`。
源区间必须有正长度。音频、动画、字幕在各自轨道中按 cue-local 位置编排。

```text
segment_course_start = cue.course.start + segment.cue_start
cue_local_time       = course_time - cue.course.start
source_time          = segment.source_start + (cue_local_time - segment.cue_start)
```

音频执行层以实际解码采样率换算整数帧。`source_start_sample`、
`source_end_sample` 是构建时的采样位置提示，格式差异会产生诊断。

## 固定时间轴

文件：`tutorial/<track>.audio-slices.json`。

| 字段 | 内容 |
| --- | --- |
| `schema` | `tutorial-audio-slices/v1` |
| `schema_version` | `4` |
| `game_id`、`track` | 内容身份 |
| `sample_rate` | 正整数，固定文件采样位置与边界契约的计算基准 |
| `timing_points` | 命名时间点到 `{time, description?}` 的映射 |
| `cues` | cue ID 到逻辑音频 slice 列表的映射 |
| `cue_timing` | per-cue 时长模式、裁尾、guard 和可选静音覆盖 |
| `audio_groups` | 共享素材的 cue/slice ID 映射与审计信息 |
| `cue_visuals` | 可选的多段视觉映射 |
| `source_boundary_contracts` | 可选的同源 slice 边界契约 |

`timing-policy.json` 还提供自动间隔策略：
`inter_cue_gap_seconds` 用于普通 cue 尾部，`section_end_gap_seconds` 用于
每个 section 的最后一条 cue。plan 构建按 section 边界自动应用，不需要逐条
写 `trailing_silence_seconds` 或 `post_speech_silence_seconds`。

音频 slice：

```json
{
  "id": "lesson__a",
  "asset": "media/tts/lesson.mp3",
  "start_ref": "lesson__a.start",
  "end_ref": "lesson__a.end",
  "speech_start_ref": "lesson__a.speech_start",
  "speech_end_ref": "lesson__a.speech_end",
  "subtitles": [
    {
      "start_ref": "lesson__a.subtitle.0.start",
      "end_ref": "lesson__a.subtitle.0.end",
      "text": "示例字幕。"
    }
  ]
}
```

slice ID 唯一；refs 必须存在；`asset` 相对于游戏内容根目录。
字幕 refs 使用素材坐标。启用 `trim_to_speech_start` / `trim_to_speech_end`
时，对应 speech start/end 必须有效并落在源区间内；plan 会在 speech start
前和 speech end 后各保留配置的 guard。裁切后的音频与
`trailing_silence_seconds` 或 `post_speech_silence_seconds` 一起确定音频轨道
时长。`speech_start_ref` / `speech_end_ref` 及对应的 `use_measured_*` 是可选扩展，
用于保留制作期 PCM 能量测量结果。full 连续 section 音频还可用
`use_measured_source_window` 保留经过能量静音中点校正的 source window；
此时 `start_ref` / `end_ref` 是固定的测量边界，不再由 provider word timing
直接决定。

连续边界契约示例：

```json
{
  "left_slice": "lesson__a",
  "right_slice": "lesson__b",
  "kind": "continuous"
}
```

两侧必须是不同的已知 slice，且引用同一素材。契约对不能重复。
`continuous` 要求 `round(left.end × sample_rate)` 等于
`round(right.start × sample_rate)`；`trimmed_tail`、`authored_gap`
允许空隙。所有契约都要求整数采样区间无重叠。

全局配置文件 `tutorial/<track>.timing-policy.json` 使用
`tutorial-cue-timing-policy/v1`、`schema_version: 1`，包含
`audio_source: "audio_slice_manifest"`、`audio_slice_manifest`、
`script_source` 和 `default_mode`。per-cue 策略由固定时间轴的
`cue_timing` 提供，模式为 `max`、`audio` 或 `visual`。

## 编译计划

文件：`tutorial/<track>.cue-timeline.json`。

```json
{
  "schema": "tutorial-cue-plan-set/v1",
  "schema_version": 1,
  "game_id": "example",
  "track": "full",
  "course": {"start": 0.0, "duration": 2.0},
  "cues": [
    {
      "schema": "tutorial-cue-plan/v1",
      "cue_id": "lesson/a",
      "entry": null,
      "course": {"start": 0.0, "duration": 2.0},
      "tracks": [
        {
          "id": "audio.main",
          "kind": "audio",
          "tail_policy": "silence",
          "segments": [
            {
              "id": "audio.slice.0",
              "audio_slice_id": "lesson__a",
              "type": "audio_clip",
              "asset": "media/tts/lesson.mp3",
              "cue_start": 0.0,
              "source_start": 10.0,
              "source_end": 12.0,
              "source_start_sample": 240000,
              "source_end_sample": 288000
            }
          ]
        }
      ],
      "subtitles": [],
      "text": "示例口播。",
      "group_path": ["章节"],
      "refs": []
    }
  ],
  "diagnostics": []
}
```

`cue_id` 在课程内唯一。cue 按课程起点排序，范围无重叠；段位于所在 cue
范围内；最后一个 cue 的终点与课程总时长一致。生成器按 cue 最终时长
累加起点。`entry`、`text`、`group_path`、`refs` 提供状态来源和导航信息。

### 轨道与段

每条轨道包含 `id`、`kind`、`tail_policy`、`segments`。
当前生产播放器按 kind 使用音频、视觉和字幕轨道，同轨段顺序排列且无重叠。
音频与视觉可并行。

| kind | type | 关键字段 | 时长 |
| --- | --- | --- | --- |
| audio | `audio_clip` | `asset`、`source_start/end` | 源区间长度 |
| audio | `silence` | `duration` | 显式时长 |
| visual | `animation` | `animation`、`source_start/end`、`after_end` | 源区间长度 |
| visual | `state` | `animation`、`duration`、`after_end` | 显式时长，显示素材时间零的画面 |
| visual | `hold` | `duration` | 显式时长，保持前序画面 |
| subtitle | `subtitle` | `text`、`duration` | 显式时长 |

每段包含 `id` 和 `cue_start`；段 ID 在所属 cue 内唯一。
`audio_slice_id` 记录逻辑 slice 来源，用于编译后源边界契约校验。
`animation` 引用 compiled cue ID。

尾部策略：

| track | `tail_policy` |
| --- | --- |
| audio | `silence`、`stop` |
| visual | `hold_last_frame`、`clear` |
| subtitle | `clear` |

视觉段 `after_end` 使用 `hold_last_frame` 或 `clear`。
无前序画面的 hold 和 `clear` 对应视觉空白。
顶层 `subtitles` 是字幕窗口的镜像表示；正式字幕内容由 subtitle track 承载。

## 时长与同步契约

- cue 时长来自编译计划；字幕窗口承担展示职责。
- source 连续性与 course 连续性分别检查；课程上的显式静音和视觉停留保持原时长。
- 课程音频块可以覆盖多个 cue，cue 边界作为字幕、视觉和导航标记。
- 准备完成后，transport 返回实际 DSP 起点；课程时钟与声音使用共同锚点。
- 跳转、设备重置等操作创建新的 transport generation；异步回调带同一标识。
- cue 完成由课程位置越过 cue 终点触发。

## 校验与扩展入口

`build_cue_timeline.py` 负责固定文件解析、视觉映射、时长策略、源边界契约与
生产 plan 生成。`cue_timeline.py` 提供纯 Python 编译与校验函数，并支持
`tutorial-cue-source/v1` 的源描述与锚点解析；对应示例位于
`tests/fixtures/cue_timeline/source_example.json` 和 `compiled_example.json`。

Unity 的 Core 类型位于
`clients/unity/Assets/Scripts/Tutorial/Animation/Core/`，Android 的读取与
导航映射位于 `clients/android/app/src/main/java/com/boardai/tutorial/uaal/timeline/`。
字段调整应同步更新两端解析器、Python 校验器和相关回归测试。
