# Tutorial 音频/字幕时间轴制作管线（Phase 5）

本文记录 Splendor `quick` 与 `full` 两条 track 的 cue-level 音频、逻辑切片、
字幕轨道、多 visual segment、固定时间轴和内容发布流程。

相关协议与实现：

- `docs/tutorial/cue-timeline-protocol.md`
- `animation/build_cue_timeline.py`
- `animation/cue_timeline.py`
- `clients/unity/Assets/Scripts/Tutorial/Animation/`
- `clients/android/app/src/main/java/com/boardai/tutorial/uaal/timeline/`

## 1. 数据流总览

```text
{track}.script.json
  └─ cue/beat 文本与顺序
      └─ {track}.audio-slices.json                 # 唯一固定时间轴
          ├─ timing_points（所有秒数）
          ├─ cues：cue ID -> logical audio slices
          ├─ refs：asset/source range/subtitle
          ├─ cue_timing：per-cue mode/silence/trim policy
          ├─ audio_groups：cue/audio-group ID 映射
          └─ cue_visuals：多 visual animation segment 映射
              └─ animation/build_cue_timeline.py
                  └─ {track}.timing-policy.json    # 仅全局配置
                      └─ tutorial/{track}.cue-timeline.json
                          ├─ Android TutorialTimeline
                          └─ Unity CuePlaybackController

content/catalog/splendor.json
  └─ cue_timeline_tracks: ["quick", "full"]

tools/content/build_content_manifest.py
  └─ 打包 plan、固定时间轴、全局 timing policy 和 plan 引用的全部音频
```

## 2. 源文件职责

```text
content/games/splendor/tutorial/build/quick.script.json
content/games/splendor/tutorial/build/full.script.json      # cue 文本/顺序元数据（构建生成）
content/games/splendor/tutorial/quick.audio-slices.json     # quick 唯一固定时间轴
content/games/splendor/tutorial/full.audio-slices.json      # full 唯一固定时间轴
content/games/splendor/tutorial/quick.timing-policy.json    # 仅全局配置
content/games/splendor/tutorial/full.timing-policy.json     # 仅全局配置
content/games/splendor/media/tts/quick.cues/                # quick TTS 生成物
content/games/splendor/media/tts/full/                      # full 现有 section TTS
```

- `build/{track}.script.json`：cue/beat 文本与导航元数据的编辑/装配源。
- `{track}.audio-slices.json`：**时间、ID、source range、字幕 refs、音频组、
  per-cue timing policy 和 visual segment 映射的唯一权威来源**。
- `{track}.timing-policy.json`：只保留全局流水线配置（`audio_source`、固定
  文件路径、script 路径、`default_mode`），不得出现 per-cue ID 或秒数。
- 固定文件之外不得再散落 cue ID -> slice ID 映射、TTS 秒数或 per-cue 静音
  配置。生产构建脚本只按 ID/ref 通用查表，不包含具体 Splendor cue ID 或
  timing point ID。

## 3. TTS 与 full 媒体

quick 使用 seed2 cue-level TTS：

```bash
python animation/tts_doubao.py \
  --provider seed2 \
  --voice zh_female_vv_uranus_bigtts \
  --resource-id seed-tts-2.0 \
  --input content/games/splendor/tutorial/quick.cue.tts.lrc \
  --out-dir content/games/splendor/media/tts/quick.cues \
  --track quick --prune
```

full 为两个父章节新增了独立标题 cue：

- `setup/title_2`，文本 `2，设置。`，独立音频
  `content/games/splendor/media/tts/full/setup.intro.mp3`；
- `cards_group/title_3_2`，文本 `3.2，发展卡牌与贵族。`，独立音频
  `content/games/splendor/media/tts/full/action.cards.group.mp3`。

两者均使用 seed2 音色 `zh_female_vv_uranus_bigtts` / `seed-tts-2.0`
单独生成，不复用子章节音频。其余 full cue 继续复用已有 section TTS 媒体，
并以固定 source range 表示音频窗口。

重新生成 full title cue 的入口：

```bash
DOUBAO_TTS_PROVIDER=seed2 \
DOUBAO_SPEAKER=zh_female_vv_uranus_bigtts \
DOUBAO_RESOURCE_ID=seed-tts-2.0 \
python animation/compile_tutorial.py --game splendor --track full
```

如果新增/修改 section TTS，必须同步更新 `full.audio-slices.json` 的
timing points、字幕 refs、audio group 和 plan，再重新构建 manifest。

规则：

- 一个 cue 可以引用一个或多个逻辑音频 slice；
- 不复制或改写原始 TTS 文件；plan 写 asset + source range；
- 共享音频关系只作为固定文件中的 `audio_groups` 审计/ID 映射，不再生成
  运行时 `playback_group` 交接标记；
- 运行时不再用 `AudioSource.isPlaying`、字幕最后 word 或标题特判决定 cue
  完成。

## 4. 固定时间轴文件 schema

当前 schema 为 `tutorial-audio-slices/v1`、`schema_version=4`：

```json
{
  "schema": "tutorial-audio-slices/v1",
  "schema_version": 4,
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
    "<cue_id>": [
      {
        "id": "<slice_id>",
        "asset": "media/tts/quick.cues/<generated>.mp3",
        "start_ref": "<slice_id>.start",
        "end_ref": "<slice_id>.end",
        "speech_end_ref": "<slice_id>.speech_end",
        "subtitles": [
          {
            "start_ref": "<slice_id>.subtitle.0.start",
            "end_ref": "<slice_id>.subtitle.0.end",
            "text": "..."
          }
        ]
      }
    ]
  },
  "cue_visuals": {
    "<cue_id>": [
      {
        "id": "<visual_segment_id>",
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

`cue_visuals` 可省略；省略时兼容旧的单 compiled-cue 默认视觉段。需要多个
animation segment 时必须显式写入固定文件。

### 命名与引用规则

```text
{slice_id}.start
{slice_id}.end
{slice_id}.speech_end
{slice_id}.subtitle.{index}.start
{slice_id}.subtitle.{index}.end
{cue_id}.visual.{index}.start
{cue_id}.visual.{index}.source_start
{cue_id}.visual.{index}.source_end
```

- `id`：逻辑音频 slice / visual segment ID，必须唯一；
- `asset`：原始音频文件，保持完整，不复制不修改；
- `start_ref/end_ref`：source range timing point refs；
- `speech_end_ref`：最后一个 TTS word 结束的 timing point；
- `subtitles[].start_ref/end_ref`：字幕窗口 timing point refs；
- `cue_timing`：cue ID -> 时间策略；
- `audio_groups`：共享音频的 cue/slice ID 映射审计；
- `cue_visuals`：visual segment -> compiled animation + source range。

所有时间点时刻只存在于 `timing_points`；固定时长 silence 等 per-cue 数值
策略只存在于 `cue_timing`。缺失 ID、重复 ID、非法 ref、越界、未知 animation
和循环引用必须显式失败。

### trim + 固定尾静音

`trim_to_speech_end=true` 是编译期显式 policy：

1. slice 必须有 `speech_end_ref`；
2. timing point 必须是有限数；
3. `start <= speech_end <= end`；
4. trim 后 source range 非空且非负；
5. 追加 `trailing_silence_seconds` 的 silence segment；
6. `mode=audio` 的 cue duration 精确等于 trim 后音频长度 + silence。

这是固定数据中的编译策略，不是运行时按字幕最后 word 裁切。full/quick 的
标题 cue 不再有代码级特判；任何 cue 都可显式选择该 policy。

## 5. cue duration 策略

per-cue mode 位于固定文件的 `cue_timing`，`{track}.timing-policy.json`
只提供全局 `default_mode`：

```json
{
  "audio_source": "audio_slice_manifest",
  "audio_slice_manifest": "tutorial/full.audio-slices.json",
  "script_source": "tutorial/build/full.script.json",
  "default_mode": "max"
}
```

- `max`：等待所有 track 结束。音频先结束则动画继续；动画先结束则保持最后
  画面等待音频。
- `audio`：cue duration 使用完整 audio track（含显式 silence）；visual 超出
  部分裁到 cue duration 并保持最后画面。
- `visual`：visual 为准；若会截断音频则构建失败。

cue 起点按最终 duration 顺序累加；plan set 的 `course.duration` 必须等于
最后一条 cue 的 `start + duration`。旧 runtime duration 不再是 plan 模式的
时间来源。

## 6. 字幕与视觉轨道

字幕在 plan 中是正式 track，只作展示，不参与音频裁切或 cue completion。

视觉 track 支持零到多个 animation segment：

- 每个 segment 有 id、compiled animation 引用、cue-local start、source
  range、`after_end`；
- 活动 animation 按 cue-local time 映射到 `source_start + offset`；
- `hold` 冻结前一个 animation 的 `source_end`；
- `after_end=clear`、无前序 animation 的 hold、以及 cue 开头无视觉段都表示
  视觉空白，会清空画面；
- 不同 compiled cue 间切换时各自使用 compiled `start_state`，状态不自动
  继承；同一 compiled cue 的多个 source range 可以连续 seek。

## 7. 生成 plan

```bash
python3 animation/build_cue_timeline.py --game splendor --track quick
python3 animation/build_cue_timeline.py --game splendor --track full
```

产物：

```text
content/games/splendor/tutorial/quick.cue-timeline.json   # 32 cues
content/games/splendor/tutorial/full.cue-timeline.json    # 83 cues
```

生成器会再次执行 `validate_plan_set`。固定文件改动后重新生成即可更新 plan；
不需要修改生产脚本。连续运行两次必须逐字节一致。

## 8. 内容发布

```bash
python3 tools/content/build_content_manifest.py --game splendor
```

发布脚本把以下内容纳入 release、manifest、SHA-256 完整性校验：

- `tutorial/{quick,full}.cue-timeline.json`
- `tutorial/{quick,full}.audio-slices.json`
- `tutorial/{quick,full}.timing-policy.json`
- plan 引用的所有音频文件
- runtime、compiled、图片等既有运行时依赖

plan 中缺失音频引用、或 manifest 收集时发现引用文件不存在，构建会失败而
不是发布坏包。

## 9. Track 开关与回退

唯一 track 开关来源：

```json
"cue_timeline_tracks": ["quick", "full"]
```

- Android 解析 catalog，并决定是否请求 plan 模式；
- Android 校验整条 plan 后，把实际模式放进 `LoadGameWithRoot` payload；
- Unity 读取同一开关结果，加载同一份 plan，并校验 cue 覆盖/顺序、总时长、
  visual/audio 段和音频文件存在性；
- 正常路径下 quick/full 都使用 plan，不存在逐 cue 混合；
- 若整条 plan 缺失/非法，Android 与 Unity 都切换到整轨兼容回退：
  Android 使用 runtime cue start/duration；Unity 由加载适配器把同一组
  runtime cue 时间整体转换为 plan 形状，再交给 `CuePlaybackController`。
  这不是第二套播放调度引擎，也不会一端 plan、一端另一个播放引擎。

## 10. 验证清单

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

自动测试覆盖固定时间轴校验、full 83 cue、确定性、多 visual segment
resolver、控制器暂停/seek/generation/失败/完成、Android plan loader 与两端
总时长一致性。自动测试不能替代真机视觉/听感验收。
