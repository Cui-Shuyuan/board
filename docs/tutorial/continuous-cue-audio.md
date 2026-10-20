# 课程音频播放器

Unity 使用一条课程 DSP 时钟驱动声音、cue、字幕与视觉。Android 从共享 plan
读取导航坐标，并根据 Unity 状态展示播放位置。

数据定义见 [Cue Timeline 协议](cue-timeline-protocol.md)，内容构建见
[音频与时间轴制作流程](audio-timeline-pipeline.md)。

## 组件职责

| 组件 | 职责 |
| --- | --- |
| `CueAudioTimelinePlanner` | 将 cue-local 音频展开到课程时间，分类边界并合并连续源区间 |
| `CueAudioRenderGroup` | 组织密集短片段，渲染含静音与边缘包络的 PCM |
| `CueCoursePlaybackClock` | 保存课程/DSP 锚点，计算或冻结课程位置 |
| `CueCoursePlaybackController` | 播放状态、generation、cue 事件、跳转和恢复 |
| `UnityCourseAudioTransport` | 素材解码、缓存、AudioSource 预约与资源生命周期 |
| `CueAudioReleaseEnvelope` / `CueAudioReleaseRamp` | 音频线程上的主动停止淡出 |
| `TutorialCuePlayer` | 播放入口、Android 状态、设备事件和视觉/字幕接入 |

Core 文件位于 `clients/unity/Assets/Scripts/Tutorial/Animation/Core/`；
Unity 执行层位于相邻的 `Unity/` 目录。

## 连续播放

规划器将所有 cue 的音频段映射到课程绝对时间。相邻段满足以下条件时，
组成一个连续播放块：

1. 素材相同；
2. 解码采样率、声道与播放速率相同；
3. 前段源结束帧等于后段源开始帧；
4. 两段课程边界在采样坐标上相接。

连续块保留原始 PCM 和内部 cue 标记。到达 cue 边界时，控制器发布 cue
完成/切换事件，字幕与视觉切换到相应局部时间，声音沿当前块继续输出。
实际源剪接、素材切换和静音交界使用默认 4 毫秒的短边缘包络。

课程覆盖按计划秒数检查；源帧和输出帧按各自采样率计算绝对边界。
实际解码格式决定播放帧坐标。计划中的采样位置提示用于诊断。

短片段按组渲染：当前组跨度小于 0.25 秒且下一块间隔小于 0.25 秒时，
可将下一块加入同组。组内静音渲染为零采样，完整连续长块保持完整。
混合组采用组内最高采样率和最多声道数，支持单声道/立体声；
低采样率素材使用线性插值，单声道复制到左右声道。

## 准备与 DSP 调度

准备阶段加载并校验课程引用的音频，建立计划并预热临近输出。
调度时先准备起播 PCM，再选择 DSP 起点并返回控制器：

```text
准备素材与目标 PCM
  → transport 确定 DSP 起点并预约
  → controller 提交课程位置/DSP 锚点
  → DSP 起点到达
  → 课程时间推进
```

启动余量至少为 0.08 秒，同时参考 DSP buffer 大小、buffer 数量和
输出采样率。默认向前预约窗口为 1.5 秒。每次 `PlayScheduled` 前检查
预约是否仍有足够余量。

transport 使用四个独立子对象上的 AudioSource：最多两条用于当前预约，
其余容量用于容纳短期淡出的声音。滤波器只处理所属 source。
DSP 起点到达前，课程位置冻结，`HasStarted` / 对外 `IsPlaying` 为 false；
Android 在此期间保持当前位置。

## 暂停、跳转与播放策略

- **暂停：** 冻结课程位置；可听声音在音频线程完成 4 毫秒余弦淡出；
  尚未开始的预约立即取消。
- **恢复：** 从冻结位置准备目标 PCM，建立新的共同 DSP 起点。
- **同轨跳转：** 使用已准备的课程计划，创建 transport generation；
  目标 PCM 使用短淡入，离开的声音完成淡出后回收。
- **快速连续跳转：** 取消过时的未来预约，保留仍在完成淡出的 voice。
- **顺序跨 cue：** 音频按课程预约继续输出，cue-local 时间由课程位置派生。
- **关闭自动前进或启用 cue 末尾暂停：** 输出范围限制到目标 cue 终点。
  末尾状态等待显式 Next/Seek。

淡出处理跨音频回调缓冲区保持进度，并对同一帧的所有声道使用相同增益。
正常连续播放时滤波器原样传递采样。

## 缓冲、失败与设备恢复

漏掉预约时，transport 报告最早尚未播放的片段起点。控制器先冻结到该位置，
再更新字幕与视觉；等待至少 0.1 秒后尝试恢复。同一恢复过程最多自动重试
三次，连续推进一秒后清零重试计数。

预热失败、PCM 范围异常和调度异常会进入 Failed 并冻结时钟。
提交调度后再次校验 generation 与状态，异步结果只对所属 generation 生效。

`AudioSettings.OnAudioConfigurationChanged` 与采样率检测共同触发设备恢复。
控制器保留最后可靠课程位置，transport 清理解码和动态输出资源，再准备并
建立 DSP 锚点。准备中的暂停意图一并保留；终态失效资源在下次播放时重建。
应用后台恢复使用同一套资源与时钟恢复流程。

## 缓存与内容身份

| 缓存 | 预算 | 生命周期 |
| --- | ---: | --- |
| 输出 AudioClip | 16 MiB | 按使用顺序淘汰；保护播放中和预热中的 clip |
| 额外源 PCM 数组 | 24 MiB | 超预算时淘汰较早缓存；单个大素材可保留 |
| 原始解码 AudioClip | 整轨持有 | 内容根目录变化、设备失效或 Dispose 时释放 |

Prepare 清空上一个计划的输出缓存。输出 key 包含组索引与帧区间，
其作用域是一次准备的不可变计划。边界诊断使用 cue 与 segment 组合身份。

预算是缓存目标；播放中/预热中的 clip、单个大素材和临时渲染数组可能使
内存超过预算。full 最长连续块为 19.649 秒，24 kHz 单声道 float PCM
约 1.80 MiB。真机峰值内存与 GC 需通过运行测量获得。

## 内容与验证

按当前 Splendor 的 24 kHz 单声道计划审计：

| track | cue 数 | 播放块数 | 合并连续交界 | 课程时长 |
| --- | ---: | ---: | ---: | ---: |
| quick | 32 | 32 | 0 | 199.206 秒 |
| full | 83 | 83 | 0 | 642.136 秒 |

`tests/AnimationModelTests` 验证纯控制器与计划逻辑；
`tests/CourseAudioTransportTests` 编译生产 transport，并用确定性 Unity API
替身验证预约、PCM、淡出和缓存。构建与设备结果见
[2026-10-20 验收记录](validation/2026-10-20-cue-audio.md)。
