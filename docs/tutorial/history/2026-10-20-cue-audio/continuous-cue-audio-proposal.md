# Cue 切换连续音频方案

日期：2026-10-20。状态：已按本文方案接入课程级 transport；.NET/内容/Android 单元测试已跑，尚未做 Unity 真机录音听感验证。

本文保留初始调查和方案；后续边界修复、当前实现及验证记录见
[连续音频修复验收](continuous-cue-audio-fixes.md)。下面的旧播放器路径和
51 个连续交界是改造前快照，当前 full 为 53 个合并交界。

目标：顺序播放时，cue 只划分教学内容，不打断连续音频；真实剪接与主动跳转采用采样级短过渡。保留现有 course 时间轴、字幕、动画、暂停和跳转语义。

## 已核实的实现

- `UnityCueAudioExecutor.ApplyCueTime()` 用单 AudioSource 的 Stop、换 clip、设置 timeSamples、Play 启动每个 segment。
- `GetOrCreateSegmentClip()` 从已解码 PCM 复制区间，尚无边界平滑。PCM 获取、复制和 AudioClip 创建第一次发生在 Play 阶段，而非全部在 Prepare 阶段完成。
- `CuePlaybackController.Complete()` 主动调用 audio.Stop；`TutorialCuePlayer.UpdateCueTimeline()` 收到完成请求后才 PlayCue 下一条。
- `PlayCue()` 无条件 CancelCueTimelineForSwitch，之后重新 Prepare；下一条若使用新 asset，还需要异步读取和解码。
- 控制器时钟已经是 AudioSettings.dspTime，但切换命令仍由主线程 Tick 发出。使用 DSP 时钟不等于提前安排了采样级交接。
- 控制器在调用 audio.Play 前记录起始时钟；首次 PCM 复制、clip 创建和实际 Play 的耗时可能让逻辑时间领先声音。具体误差需要录音和运行时测量，不能只从代码推断毫秒数。
- ApplyCueTime 用 source.isPlaying 参与是否重启的判断。后续应以明确的调度状态判断 segment 是否启动或完成，避免将已自然结束或尚未到预约时间的 source 误判为需要重播。当前是否出现重复启动尚未验证。

这能证实存在不连续播放路径，不能证明用户听到的声音就是方波，也不能完全排除源 TTS、解码、重采样或设备输出因素。硬切通常产生瞬态；周期性听感需要通过实际输出确认。

## 当前内容的边界审计

按当前工作区 quick/full.cue-timeline.json 的相邻 audio_clip 统计。两条轨道当前每 cue 各一段 audio_clip，方案仍须支持一个 cue 内多音频段。

| 情况 | quick（32 cue，31 个交界） | full（83 cue，82 个交界） |
| --- | ---: | ---: |
| 同 asset，源采样和课程时间都连续 | 0 | 51 |
| 同 asset，源采样连续但课程时间有间隔 | 1 | 2 |
| 同 asset，源范围之间有跳过 | 0 | 16 |
| 不同 asset | 30 | 13 |

没有发现相邻同 asset 的源范围重叠。课程间隔可能来自显式 silence 或音频结束后的 visual hold，不能一律当作播放故障删除。

代表样例：

- `setup_cards/001.1 → setup_cards/001.2`：同源且时间连续，应成为原样通过的验证用例。
- `quick_nobles/001 → quick_nobles/002`：源连续，课程间隔约 242.437 ms。
- `cards/discount.003.1 → cards/discount.003.2`：源连续，课程间隔 305 ms。
- `nobles/source.001 → nobles/choice.001.1`：源连续，课程间隔 2.34 s。
- `intro/title_1 → intro/001`：源范围从 1.305 s 跳到 1.850 s，同时课程安排 1 s 静音。属于真实编辑点，无法用“直接接回源文件”同时保持既有时间轴。

0.15 s context lead 在导入逻辑中会同时前移 start/end，受可用尾部时长约束，并非所有 cue 都简单增加 0.15 s。不要全局删除 lead，以免重新损伤首字。标题 guard 和静音也应与播放修复分开验证。

## 建议实现：课程级音频调度 + 连续区间合并

音频播放生命周期提升到整条 track。cue-local 控制器继续负责当前 cue 的表现和完成事件，课程 transport 负责唯一播放位置及跨 cue 音频安排。

```text
现有 cue plan set
  → 转换为课程绝对时间上的音频区间 / 静音区间
  → 合并真正连续的源区间，标记真实剪接点
  → 提前准备 PCM / 过渡窗口
  → 按同一 DSP 起点调度持续输出
        └→ 同一课程位置映射到 cue、字幕、动画
```

### 1. 连续区间保持原始采样

同时满足以下条件才合并为一次播放：相同 asset、相同采样格式、上一段 source_end 等于下一段 source_start、课程位置也相接、播放速率一致。比较采用统一定义的整数采样帧坐标，避免浮点累计误差。

一个连续区间可以包含多个 cue。跨 cue 时音频不 Stop、不 Seek、不重新 Play，也不额外 fade；只有字幕和动画切换。这样 full 的 51 处连续交界可以在数字 PCM 层面保持原样。

不把 course 上明确存在的 silence/hold 合并掉。超长连续区间若需要分页，必须保证分页边界的采样和重采样状态连续；第一版优先使用有界的章节连续区间，避免引入自定义音频回调复杂性。

### 2. 真实剪接点使用短过渡

| 边界 | 处理 |
| --- | --- |
| 连续源、连续时间 | 原样通过，不淡入淡出 |
| 音频进入或离开计划静音 | 在原时间窗口内做短淡出/淡入 |
| 换 asset 或跳过源范围 | 有合法上下文时做短交叉淡化；否则短淡出/淡入 |
| 主动跳 cue / seek | 目标预备好后在未来 DSP 时刻提交跳转，旧声音退出、目标声音进入 |

初始试听参数：边缘淡入淡出 3–5 ms，交叉淡化窗口 5–10 ms。参数是起点，不是对所有语音的最优值；避免较长淡化吞掉辅音、气口或瞬态。

过渡包络在 PCM 采样上生成，不用 Update 或协程逐帧调 volume。左右声道共享同一增益包络。交叉淡化需要边界之外的合法素材余量（handles）；不得无条件读取相邻句子的语音。无余量时不伪造 overlap，采用边缘淡化。

相关信号默认采用互补增益窗（两路权重之和为 1），避免重复叠加放大；不同背景的等功率窗可作为经试听与峰值检测后的选择，不能一律套用。

过渡必须明确映射到现有 course 窗口，不能通过每次缩短 10 ms 来实现，否则整轨字幕逐渐漂移。最初实现可预渲染小块“剪接窗口”，明确该窗口替换哪一段输出时间。对任意跳转，目标 cue 的逻辑起点与目标声音进入时刻一致；短旧尾音仅作为过渡混合。

### 3. 提前准备并按 DSP 时间调度

采用两个 AudioSource 交替调度不重叠的输出块；将需要重叠的剪接窗口预混到 PCM 输出块中，避免假设两条 source 足以处理任意多段重叠。

- Prepare 完成的含义包含解码、GetData、PCM 区间/过渡窗口生成和 SetData；切换瞬间不做这些操作。
- PlayScheduled 使用同一个课程 DSP epoch。下一块的起点由绝对输出帧位置计算，不取“当前 dspTime + 一点延时”。
- 根据实际 DSP buffer 配置设置启动余量，后台按前瞻窗口预加载。Unity 文档建议为调度留出准备时间；具体窗口需在 Android 真机上测量。
- 长段用两个 source 交替；极短的音频/静音组合先合成足够长的输出块，避免主线程每帧只能排一个块而来不及调度。
- 同源连续部分应在同一个 PCM 播放区间内跨过 cue。若素材采样率/声道不同，在准备阶段统一输出格式，并保持连续源的重采样状态，避免以独立片段重采样引入新接缝。
- 缓存使用根目录/内容版本、asset、源范围、格式和过渡参数作为键，并限制容量；长课不要同时保留整轨 PCM、多份 slice 和多个完整 mix。
- 若准备未赶上预约时间，不能悄悄晚播并继续走字幕。记录 underrun，进入明确的缓冲状态并重建播放锚点。

Unity 官方说明 PlayScheduled 用 DSP 绝对时间安排播放，可脱离帧率进行音频拼接：[AudioSource.PlayScheduled](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/AudioSource.PlayScheduled.html)。

### 4. cue 完成与停止音频分开

需要一起调整这些路径，不能只替换 executor 里的 Play：

- `CuePlaybackController.Complete()`：顺序播放跨 cue 时只发出完成事件；是否停音由课程 transport 的播放策略决定。保留单 cue 播放/末尾暂停的停止语义。
- `TutorialCuePlayer.UpdateCueTimeline()` / `PlayCue()`：自动前进走持续 transport 的 cue 切换路径；用户跳转走显式 discontinuity 路径，不共用无条件 Cancel 的路径。
- 新增纯 C# 的课程音频区间规划器和 transport 时间映射，使用现有 plan set 的 course.start/duration，不再生成第二套 cue 时长。
- cue 的 generation 继续保护视觉准备；音频另有 transport generation，顺序换 cue 不作废预约。seek、换轨、取消、失败、销毁才使旧音频预约失效。
- 下一个 cue 的 visual 也需提前准备。主线程迟到时按当前课程位置进入正确 localTime，不能从零重播造成音画落后。
- 暂停取消未来预约并冻结统一课程位置；恢复以同一位置建立新 DSP epoch，字幕不能先走。暂停状态 seek 不应发出声音。
- pauseAtCueEnd / autoAdvance 关闭时，在边界预先安排停止，不先播下一句再等完成回调刹车。运行中改变策略时同步取消未开始预约。
- 应用后台/音频设备配置改变后重新建立时钟与输出格式，防止沿用失效预约。

## 落地与验收

1. 建立基线：选上述同源连续、标题到正文、不同 asset、源连续但课程插入静音四类边界。比较源 PCM、按 plan 离线拼出的参考 PCM、实际 Unity 输出。三者区分素材/编辑问题和运行时调度问题。
2. 先实现连续区间规划与课程级调度，预备所有待播放 PCM；验证无过渡情况下的数字连续性，再加入真实剪接的短包络。
3. 接入自动前进、pauseAtCueEnd、暂停恢复、跨 cue seek 和快速连续跳转。保留现有字幕/视觉/Android 进度协议。
4. 在当前 .NET 测试工程增加纯规划/时间映射测试：连续区间合并、显式 silence 不丢失、整数帧边界、暂停恢复、旧 generation 取消、提前停止与多段 cue。
5. 音频测试：同源连续拼接 PCM 与源参考逐采样相同；用连续正弦和脉冲夹具检查丢样、重复和意外零填充；包络不改计划帧长且不产生越界/非有限值。不能仅以“切点振幅差小”判定成功，正常音频本来就有样本变化。
6. Unity/Android 端录音 A/B：首次播放和缓存后播放、低帧率/人为主线程卡顿、标题/正文、短多段 clip、暂停恢复、快速跳转。记录预定 DSP 时刻、准备完成时刻、输出帧范围和 underrun。输出录音先校准设备延迟与重采样，再分析边界。

验收目标：相同连续源不会因 cue 划分改变 PCM；非连续剪接无额外启动空洞，低电平呼吸没有明显门控；计划静音仍准确保留；首字尾字完整；课程总时长与字幕不累积漂移；取消后旧声音不会再次响起。

5–10 ms fade 单独使用只能作为诊断对照，无法填补调度空洞，还会在本来连续的呼吸声中制造音量凹口。单独增加 PlayScheduled 也无法解决 Complete/Cancel 在上层停止音频的问题。

任意跳到另一句话，可以做到平滑、无爆音，但不能保证语义和呼吸相位仍像原录音；源素材本身不自然的部分需要另行修整。当前方案针对的是 cue 切换引入的额外破坏。

## 本次实现落地（代码路径）

- 新增 `CueAudioTimelinePlanner`：把 plan set 展开到课程绝对时间，按“同 asset/格式 + 整数源帧相接 + 课程帧相接”合并连续区间；显式 silence、hold、课程 gap、跳过源范围和换 asset 都保留为真实剪接。
- 新增 `CueCoursePlaybackClock` / `CueCoursePlaybackController`：课程 DSP epoch 是唯一时间权威，cue-local time、字幕、视觉和 Android global progress 均从 `courseTime` 派生；顺序跨 cue 只切换 cue generation，不取消音频预约。
- 新增 `UnityCourseAudioTransport`：双 AudioSource 按绝对 DSP 时间 `PlayScheduled`；连续区间只播一个输出块，跨 cue 不 Stop/Play/Seek。真实剪接使用采样级边缘淡入淡出，不改变计划帧数。输出 PCM 分块、源 PCM 小缓存和输出 AudioClip 缓存均有上限。
- 平台字段处理：播放位置以 Unity 解码后的 `AudioClip.frequency/channels` 换算整数帧；plan 里的 `source_*_sample` 只做诊断，不假定其采样率与解码频率一致。缺少解码格式时准备失败，不猜测。
- 暂停/恢复重新建立共同 DSP 锚点；暂停状态 seek 只调度静音位置；`autoAdvance`/`pauseAtCueEnd` 关闭时向 transport 传课程边界，输出块在边界前截断并做边缘淡出；underrun 进入显式 Buffering，课程时钟冻结。
- 旧 generation 的异步准备/underrun 回调由 controller 与 transport 双重校验；用户跳转、换轨、取消和销毁会作废旧预约，普通顺序跨 cue 不使 transport generation 失效。
- 尚未验证：Unity Editor/Android 真机编译、实际输出录音、不同设备 DSP buffer 下的延迟与听感、耳机/扬声器切换、后台恢复现场。需要按本文“落地与验收”执行录音 A/B。
