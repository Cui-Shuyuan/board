# Tutorial 音频/字幕时间轴制作管线（Phase 4）

本文记录当前 **quick track** 的 cue-level 音频、逻辑切片、字幕轨道和内容发布流程。
`full` 仍走 legacy section runtime，不在本文迁移范围内。

相关协议与实现：

- `docs/tutorial/cue-timeline-protocol.md`
- `animation/build_cue_timeline.py`
- `animation/tts_doubao.py`
- `animation/cue_timeline.py`
- `clients/unity/Assets/Scripts/Tutorial/Animation/`
- `clients/android/app/src/main/java/com/boardai/tutorial/uaal/timeline/`

## 1. 数据流总览

```text
quick.script.json
  └─ cue/beat 文本
      └─ quick.cue.tts.lrc
          └─ tts_doubao.py
              └─ media/tts/quick.cues/*.mp3 + *.subtitle.json
                  └─ quick.audio-slices.json
                      ├─ timing_points（所有秒数）
                      ├─ 逻辑音频 slice（start_ref/end_ref/speech_end_ref）
                      └─ subtitle windows（start_ref/end_ref/text）
                          └─ quick.timing-policy.json
                              └─ animation/build_cue_timeline.py
                                  └─ tutorial/quick.cue-timeline.json
                                      ├─ Android TutorialTimeline
                                      └─ Unity CuePlaybackController

content/catalog/splendor.json
  └─ cue_timeline_tracks: ["quick"]
      └─ Android 选择是否读取 plan，并把实际模式传给 Unity

tools/content/build_content_manifest.py
  └─ 把 plan 引用的 mp3/wav 和 plan 本身打入不可变 release
```

## 2. 源文件职责

```text
content/games/splendor/tutorial/build/quick.script.json
content/games/splendor/tutorial/quick.cue.tts.lrc
content/games/splendor/tutorial/quick.audio-slices.json
content/games/splendor/tutorial/quick.timing-policy.json
content/games/splendor/media/tts/quick.cues/
```

- `quick.script.json`：cue/beat 文本的唯一编辑源。
- `quick.cue.tts.lrc`：逐 beat 的 TTS 合成清单；用于生成独立音频。
- `quick.audio-slices.json`：**所有音频/字幕时间点的唯一权威文件**。
- `quick.timing-policy.json`：cue duration 模式、静音配置和 plan 生成入口配置。
- `media/tts/quick.cues/`：TTS 生成物，不进 Git，但内容发布必须包含 plan 引用到的文件。

不要在这些文件之外再散落 TTS 秒数。

## 3. TTS 生成

当前 Splendor quick 使用 seed2 音色：

```bash
python animation/tts_doubao.py \
  --provider seed2 \
  --voice zh_female_vv_uranus_bigtts \
  --resource-id seed-tts-2.0 \
  --input content/games/splendor/tutorial/quick.cue.tts.lrc \
  --out-dir content/games/splendor/media/tts/quick.cues \
  --track quick --prune
```

规则：

- 一个 cue 默认生成一个 MP3；同一 cue 内的连续句子/分句应合并成同一行 TTS，
  避免逐 beat 生成后在片段边界听到额外停顿。
- 一个 cue 仍可以包含多个 MP3，但只用于确有独立停顿/独立素材的编排。
- 同一句话或需要保持连续语气的多个串行 cue，可以在 timing policy 的
  `audio_groups` 中合并成一次 TTS 合成。
- 不为保持语气而把多个 cue 物理复制成多份音频；共享关系通过逻辑切片表达。
- 重新生成 TTS 后，必须同步更新 `quick.audio-slices.json` 的 timing points。

## 4. 逻辑音频切片与命名时间点

`quick.audio-slices.json` 当前 schema：

```json
{
  "schema": "tutorial-audio-slices/v1",
  "schema_version": 2,
  "game_id": "splendor",
  "track": "quick",
  "sample_rate": 24000,
  "timing_points": {
    "quick_nobles__001.start": {"time": 0.0},
    "quick_nobles__001.end": {"time": 6.4475625},
    "quick_nobles__001.speech_end": {"time": 6.2950416},
    "quick_nobles__001.subtitle.0.start": {"time": 0.265},
    "quick_nobles__001.subtitle.0.end": {"time": 1.695}
  },
  "cues": {
    "quick_nobles/001": [
      {
        "id": "quick_nobles__001",
        "asset": "media/tts/quick.cues/quick_nobles__001_002.b1.mp3",
        "start_ref": "quick_nobles__001.start",
        "end_ref": "quick_nobles__001.end",
        "speech_end_ref": "quick_nobles__001.speech_end",
        "subtitles": [
          {
            "start_ref": "quick_nobles__001.subtitle.0.start",
            "end_ref": "quick_nobles__001.subtitle.0.end",
            "text": "贵族是额外得分。"
          }
        ]
      }
    ]
  }
}
```

### 命名规则

```text
{slice_id}.start
{slice_id}.end
{slice_id}.speech_end
{slice_id}.subtitle.{index}.start
{slice_id}.subtitle.{index}.end
```

- `id`：逻辑音频名，可被 cue 引用。
- `asset`：原始 TTS 文件，保持完整，不复制不修改。
- `start_ref/end_ref`：逻辑播放范围。
- `speech_end_ref`：最后一个 TTS word 的结束时间点。
- `subtitles[].start_ref/end_ref`：字幕窗口时间点。

构建脚本只读取 `*_ref` 名称；所有实际秒数只存在于 `timing_points`。

### 标题 cue 的 1 秒静音

标题 cue 使用两段逻辑：

1. timing policy 设置 `trim_to_speech_end: true`，构建器把逻辑播放终点
   clamp 到 `speech_end_ref`，从而截掉 TTS 文件自带的尾部静音；
2. timing policy 再追加完整 `trailing_silence_seconds: 1.0`。

`trim_to_speech_end` 是布尔策略，不需要在脚本中写秒数。标题 cue 配置示例：

```json
{
  "mode": "audio",
  "trim_to_speech_end": true,
  "trailing_silence_seconds": 1.0
}
```

例如：

```text
TTS 文件总长       = 1.656s
最后一个 word 结束 = 1.245s
slice end          = 1.245s
追加 silence       = 1.000s
cue duration       = 2.245s
```

## 5. cue duration 策略

`quick.timing-policy.json` 支持 per-cue mode：

```json
{
  "audio_source": "audio_slice_manifest",
  "audio_slice_manifest": "tutorial/quick.audio-slices.json",
  "script_source": "tutorial/build/quick.script.json",
  "default_mode": "max",
  "cues": {
    "quick_intro/title_1": {
      "mode": "audio",
      "trim_to_speech_end": true,
      "trailing_silence_seconds": 1.0
    }
  }
}
```

- `max`：等待所有 track 结束。音频先结束则动画继续；动画先结束则保持最后画面。
  正文 cue 默认使用该模式，避免动画被音频截断。
- `audio`：cue duration 使用完整 audio track；visual 超出部分裁到 cue duration。
  标题 cue 使用该模式。
- `visual`：visual 为准，但若会截断音频则构建失败。
- `runtime`：legacy runtime duration 为基准，并保证不截断音频。

cue 起点按最终 duration 顺序累加；一条 cue 全部轨道结束后自动进入下一条。

## 6. 字幕轨道

字幕在 plan 中是正式 track：

```json
{
  "id": "subtitle.main",
  "kind": "subtitle",
  "tail_policy": "clear",
  "segments": [
    {
      "id": "subtitle.0.0",
      "type": "subtitle",
      "cue_start": 0.305,
      "duration": 3.17,
      "text": "璀璨宝石的目标，是成为最负盛名的商人。"
    }
  ]
}
```

- 字幕窗口来自 `quick.audio-slices.json` 的 `subtitles`。
- 不再把整条 cue 文本显示到 cue 结束。
- Unity 字幕执行器优先读取 `kind=subtitle` track，缺失时才回退旧扁平
  `subtitles` 列表。
- Android loader 同时校验字幕 track 的结构和时间范围。

## 7. 生成 plan

```bash
python3 animation/build_cue_timeline.py --game splendor --track quick
```

输出：

```text
content/games/splendor/tutorial/quick.cue-timeline.json
```

plan 产物中：

- audio segment 写入 `audio_slice_id`，并展开数值 `asset/source_start/source_end`
  供运行时使用；
- subtitle track 写入 cue-local 字幕窗口；
- 所有 track 仍由同一个 `CuePlaybackController` 驱动。

生成器会再次执行 `validate_plan_set`，重复运行必须逐字节一致。

## 8. 内容发布

```bash
python3 tools/content/build_content_manifest.py --game splendor
```

发布脚本会把以下内容纳入 release、manifest、SHA-256 完整性校验：

- `tutorial/quick.cue-timeline.json`
- plan 引用到的所有 `media/tts/quick.cues/*.mp3` / 其他音频
- runtime、compiled、图片等既有运行时依赖

`quick.audio-slices.json` 和 timing policy 是构建期源文件，不直接进运行时 release。

## 9. Feature flag 与客户端接线

唯一 track 开关来源：

```json
"cue_timeline_tracks": ["quick"]
```

- Android 解析 catalog，并决定是否启用 plan。
- Android plan 校验通过后，把实际模式放进 `LoadGameWithRoot` payload。
- Unity 读取同一开关结果，并加载同一份 `quick.cue-timeline.json`。
- 缺失/非法 plan 时整条 track 回退 legacy runtime，不能逐 cue 混合。

## 10. 验证清单

```bash
python3 -m unittest animation.test_cue_timeline -v
python3 -m unittest animation.test_build_cue_timeline -v
python3 -m unittest discover -s animation -p 'test_*.py'
TMPDIR="$(pwd)/.tmp-dotnet" /mnt/d/dotnet/dotnet.exe run --project tests/AnimationModelTests
python3 tools/ops/check_unity_scripts.py --json
python3 tools/ops/check_no_secrets.py
git diff --check
```

Android：

```bash
cd clients/android
gradlew.bat testDebugUnitTest
```

设备日志关注：

```text
[CueTimelineV1][Plan] loaded track=quick ...
[CueTimelineV1][Audio] play ... segment=audio.slice.0 ...
[CueTimelineV1][Subtitle] text=...
```

## 11. 常见修改流程

### 改文案

1. 改 `quick.script.json`。
2. 同步 `quick.cue.tts.lrc`。同一个 cue 内的连续分句合并成一行；不要默认拆成多个 beat 音频。
3. 重新生成对应 TTS。
4. 更新 `quick.audio-slices.json` 的 timing points 与字幕窗口。
5. 构建 plan、发布内容、跑自动测试。
6. 真机听测与字幕验收。

如果发现某个 cue 内部句与句之间有非预期停顿，先检查它是否被拆成了多个 TTS beat 文件，
而不是先去调播放器或加 silence。

### 调整 cue 时长

只改 `quick.timing-policy.json` 的模式或 silence；不要手改 plan。

### 调整共享语气组

修改 `audio_groups` 和 TTS LRC，再更新 slice manifest；不要让运行时推断“哪些 cue 属于同一段语气”。

### 调整字幕

只改 `quick.audio-slices.json` 中对应 slice 的 subtitle refs/text；构建器会生成 subtitle track。

## 12. 当前限制

- 只有 quick 已迁移到 cue-level TTS + logical slice + subtitle track。
- full 仍使用 legacy section runtime。
- TTS 时间点目前是人工审核后写入 slice manifest；修改 TTS 后必须同步复核。
- 真机音频/字幕主观验收仍需用户执行，自动化测试不能替代听测。
