# Tutorial 音频/字幕时间轴制作管线（Phase 4）

本文记录当前 **quick track** 的 cue-level 音频、逻辑切片、字幕轨道、固定时间轴
和内容发布流程。`full` 仍走 legacy section runtime，不在 Phase 4 迁移范围内。

相关协议与实现：

- `docs/tutorial/cue-timeline-protocol.md`
- `animation/build_cue_timeline.py`
- `animation/cue_timeline.py`
- `animation/tts_doubao.py`
- `clients/unity/Assets/Scripts/Tutorial/Animation/`
- `clients/android/app/src/main/java/com/boardai/tutorial/uaal/timeline/`

## 1. 数据流总览

```text
quick.script.json
  └─ cue/beat 文本
      └─ quick.cue.tts.lrc
          └─ tts_doubao.py
              └─ media/tts/quick.cues/*.mp3 + *.subtitle.json
                  └─ quick.audio-slices.json               # 唯一固定时间轴
                      ├─ timing_points（所有秒数）
                      ├─ cues：cue ID -> logical audio slices
                      ├─ refs：asset/source range/subtitle
                      ├─ cue_timing：per-cue mode/silence/trim policy
                      └─ audio_groups：cue/audio group ID mapping
                          └─ animation/build_cue_timeline.py
                              └─ quick.timing-policy.json    # 仅全局流水线配置
                                  └─ tutorial/quick.cue-timeline.json
                                      ├─ Android TutorialTimeline
                                      └─ Unity CuePlaybackController

content/catalog/splendor.json
  └─ cue_timeline_tracks: ["quick"]
      └─ Android 选择是否读取 plan，并把实际模式传给 Unity

tools/content/build_content_manifest.py
  └─ 打包 plan、固定时间轴、全局 timing policy 和 plan 引用的音频
```

## 2. 源文件职责

```text
content/games/splendor/tutorial/build/quick.script.json    # cue 文本/顺序元数据
content/games/splendor/tutorial/quick.cue.tts.lrc          # TTS 合成输入
content/games/splendor/tutorial/quick.audio-slices.json    # 唯一固定时间轴
content/games/splendor/tutorial/quick.timing-policy.json   # 仅全局配置
content/games/splendor/media/tts/quick.cues/               # TTS 生成物
```

- `quick.script.json`：cue/beat 文本与导航元数据的编辑源。
- `quick.cue.tts.lrc`：逐 cue/beat 的 TTS 合成清单。
- `quick.audio-slices.json`：**时间、ID、source range、字幕 refs、音频组和
  per-cue timing policy 的唯一权威来源**。
- `quick.timing-policy.json`：只保留全局流水线配置（`audio_source`、固定文件
  路径、script 路径、`default_mode`），不得出现 per-cue ID 映射或秒数。
- `media/tts/quick.cues/`：TTS 生成物；内容发布必须包含 plan 引用到的文件。

固定文件之外不得再散落 TTS 秒数、cue ID 到 slice ID 的映射或 per-cue 静音配置。
生产构建脚本只按 ID/ref 通用查表，不包含具体 Splendor cue ID、timing point ID
或按 cue 名分支的代码。

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
- 一个 cue 仍可包含多个 MP3，但只用于确有独立停顿/独立素材的编排。
- 需要保持连续语气的串行 cue 可以共享一个 MP3；共享关系写入固定文件的
  `audio_groups`，不要通过复制音频文件表达。
- 重新生成 TTS 后，必须同步更新 `quick.audio-slices.json` 的 timing points。
- 生成的 TTS 文件不能被构建器复制或改写；plan 只引用 asset + source range。

## 4. 固定时间轴文件

`quick.audio-slices.json` 当前 schema：

```json
{
  "schema": "tutorial-audio-slices/v1",
  "schema_version": 3,
  "game_id": "splendor",
  "track": "quick",
  "sample_rate": 24000,
  "cue_timing": {
    "<cue_id>": {
      "mode": "audio",
      "trim_to_speech_end": true,
      "trailing_silence_seconds": 1.0
    }
  },
  "audio_groups": {
    "<group_id>": {
      "cue_ids": ["<cue_id_a>", "<cue_id_b>"],
      "slice_ids": ["<slice_id_a>", "<slice_id_b>"]
    }
  },
  "timing_points": {
    "<slice_id>.start": {"time": 0.0},
    "<slice_id>.end": {"time": 1.656},
    "<slice_id>.speech_end": {"time": 1.245},
    "<slice_id>.subtitle.0.start": {"time": 0.285},
    "<slice_id>.subtitle.0.end": {"time": 1.245}
  },
  "cues": {
    "<cue_id_a>": [
      {
        "id": "<slice_id_a>",
        "asset": "media/tts/quick.cues/<generated>.mp3",
        "start_ref": "<slice_id_a>.start",
        "end_ref": "<slice_id_a>.end",
        "speech_end_ref": "<slice_id_a>.speech_end",
        "subtitles": [
          {
            "start_ref": "<slice_id_a>.subtitle.0.start",
            "end_ref": "<slice_id_a>.subtitle.0.end",
            "text": "..."
          }
        ]
      }
    ]
  }
}
```

### 命名与引用规则

```text
{slice_id}.start
{slice_id}.end
{slice_id}.speech_end
{slice_id}.subtitle.{index}.start
{slice_id}.subtitle.{index}.end
```

- `id`：逻辑音频 ID，必须全局唯一。
- `asset`：原始 TTS 文件，保持完整，不复制不修改。
- `start_ref/end_ref`：source range 的 timing point refs。
- `speech_end_ref`：最后一个 TTS word 结束的 timing point。
- `subtitles[].start_ref/end_ref`：字幕窗口 timing point refs。
- `cue_timing`：cue ID -> 时间策略。
- `audio_groups`：共享音频组的 cue/slice ID 映射。

所有时间点时刻只存在于 `timing_points`；固定时长 silence 等 per-cue 数值策略
只存在于 `cue_timing`。构建器要求 `start_ref/end_ref` 存在，
引用必须能解析；缺失 ID、重复 ID、非法 ref、越界或循环引用必须显式失败。
切片中不允许写 inline `start/end/speech_end` 秒数。

### 标题 cue 的 trim + 固定尾静音

标题 cue 使用固定文件中的显式策略：

```json
{
  "cue_timing": {
    "<title_cue_id>": {
      "mode": "audio",
      "trim_to_speech_end": true,
      "trailing_silence_seconds": 1.0
    }
  }
}
```

构建器在编译期执行：

1. `trim_to_speech_end=true` 时要求 slice 有 `speech_end_ref`；
2. 解析出的 `speech_end` 必须是有限数；
3. 校验 `start <= speech_end <= end`；
4. trim 后 source range 必须非空且非负；
5. 追加 `trailing_silence_seconds` 的 `silence` segment；
6. `mode=audio` 的 cue duration 精确等于 trim 后音频长度 + silence。

示例关系：

```text
TTS 文件逻辑范围 = [0, 1.656)
最后一个 word 结束 = 1.245
trim 后音频长度 = 1.245
追加 silence = 1.000
cue duration = 2.245
```

这是编译期固定时间轴策略，不是运行时按字幕最后 word 裁切。缺少
`speech_end_ref`、越界、零/负时长都必须失败，不能静默退回不裁切。

## 5. cue duration 策略

per-cue mode 位于固定文件的 `cue_timing`，`quick.timing-policy.json` 只提供全局
`default_mode`：

```json
{
  "audio_source": "audio_slice_manifest",
  "audio_slice_manifest": "tutorial/quick.audio-slices.json",
  "script_source": "tutorial/build/quick.script.json",
  "default_mode": "max"
}
```

- `max`：等待所有 track 结束。音频先结束则动画继续；动画先结束则保持最后画面。
  正文 cue 默认使用该模式，避免动画被音频截断。
- `audio`：cue duration 使用完整 audio track（含显式 silence）；visual 超出部分
  裁到 cue duration 并保持最后画面。标题 cue 使用该模式。
- `visual`：visual 为准；若会截断音频则构建失败。
- `runtime`：legacy runtime duration 为基准，并保证不截断音频。

cue 起点按最终 duration 顺序累加；一条 cue 全部轨道结束后自动进入下一条。
plan set 的 `course.duration` 必须等于最后一条 cue 的 `start + duration`。

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
      "text": "..."
    }
  ]
}
```

- 字幕窗口来自固定文件的 subtitle timing point refs。
- 字幕只作展示，不参与音频裁切、不控制 cue completion。
- 构建器把源时间换算成 cue-local `cue_start/duration`，不再把整条 cue 文本
  塞成一个超长字幕。
- Unity 字幕执行器优先读取 `kind=subtitle` track，缺失时才回退旧扁平
  `subtitles` 列表；Android loader 同时校验字幕 track 结构和时间范围。

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
- 共享音频 slice 写入 `playback_group`；
- 所有 track 仍由同一个 `CuePlaybackController` 驱动；
- 构建阶段校验所有引用音频文件真实存在。

生成器会再次执行 `validate_plan_set`。固定文件改动后重新生成即可更新 plan；
不需要修改生产脚本。连续运行两次必须逐字节一致。

## 8. 内容发布

```bash
python3 tools/content/build_content_manifest.py --game splendor
```

发布脚本会把以下内容纳入 release、manifest、SHA-256 完整性校验：

- `tutorial/quick.cue-timeline.json`
- `tutorial/quick.audio-slices.json`（固定时间轴，便于审计生成输入）
- `tutorial/quick.timing-policy.json`（全局配置，如存在）
- plan 引用到的所有 `media/tts/quick.cues/*.mp3` / 其他音频
- runtime、compiled、图片等既有运行时依赖

`quick.cue.tts.lrc` 是 authoring/TTS 合成源，不进入运行时 release。Plan 中缺失
音频引用、或 manifest 收集时发现引用文件不存在，构建会失败而不是发布坏包。

## 9. Feature flag 与客户端接线

唯一 track 开关来源：

```json
"cue_timeline_tracks": ["quick"]
```

- 当前只允许 `quick`；`full` 不在数组中，继续 legacy。
- Android 解析 catalog，并决定是否启用 plan。
- Android plan 校验通过后，把实际模式放进 `LoadGameWithRoot` payload。
- Android loader 校验 schema、cue 覆盖/顺序、时间范围、总时长和音频文件存在性。
- Unity 读取同一开关结果，加载同一份 `quick.cue-timeline.json`，并校验相同的
  cue 覆盖/顺序、总时长和音频文件存在性。
- 缺失/非法 plan、缺失音频时 Android 与 Unity 都回退整条 track legacy，
  不允许逐 cue 混合。
- track 模式优先于 Phase 3 allowlist；进入 track 模式后 allowlist 不生效。

## 10. 验证清单

```bash
python3 animation/build_cue_timeline.py --game splendor --track quick
python3 animation/build_cue_timeline.py --game splendor --track quick
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

用户已完成 quick 主观动画/TTS 验收；Phase 4 未保留录音/波形证据，不宣称更高
等级验证。该主观验收不阻塞 Phase 4。

## 11. 常见修改流程

### 改文案

1. 改 `quick.script.json`。
2. 同步 `quick.cue.tts.lrc`。同一 cue 内的连续分句合并成一行；不要默认拆成
   多个 beat 音频。
3. 重新生成对应 TTS。
4. 更新 `quick.audio-slices.json` 的 timing points、subtitle refs 和必要映射。
5. 构建 plan、发布内容、跑自动测试。
6. 如需重新主观验收，再单独安排；不要把它描述为自动测试的替代品。

### 调整 cue 时长

只改 `quick.audio-slices.json` 的 `cue_timing` 或 timing point 秒数；不要手改
generated plan，也不要把 per-cue 配置放回 `quick.timing-policy.json`。

### 调整共享语气组

修改固定文件的 `audio_groups` 和 TTS 输入，再更新 slice manifest；不要让运行时
推断哪些 cue 属于同一段语气。

### 调整字幕

只改 `quick.audio-slices.json` 中对应 slice 的 subtitle refs/text；构建器会生成
subtitle track。字幕 refs 越界会显式失败。

## 12. 当前限制与 Phase 5 项目

- 只有 quick 已迁移到 cue-level TTS + fixed logical slice + subtitle track。
- full 仍使用 legacy section runtime，留待 Phase 5。
- TTS 时间点目前是人工审核后写入固定时间轴；修改 TTS 后必须同步复核。
- 多 visual animation clip 仍是 blocker：当前 plan 只支持一个 cue 粒度的
  animation segment 加可选 tail hold，不能声称已支持多 clip。
- 旧 `section_audio` / `audio_plan` / `pause_after_speech_seconds` /
  `hold_after_audio` 路径保留，Phase 5 才清理。
- Phase 4 只主张用户主观验收通过，不主张录音/波形级验证。
