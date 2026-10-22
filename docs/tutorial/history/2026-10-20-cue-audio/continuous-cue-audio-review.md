> 历史路径说明（2026-10-23）：本文保留当时本地 scratch 路径 `.tmp/...`，这些不是当前运行依赖。新实验请使用 `$BOARD_RUNTIME_ROOT/tmp/...`；2026-10-23 之前的原始 agent/eval 产物已压缩到 `D:\board\archive\agent-workspace-evidence-20261023-004025.tar.zst`，需要时解压后按原相对路径读取。

# 连续 cue 音频实现验收记录

日期：2026-10-20。范围：当前工作区新增课程音频规划器、时钟、控制器、Unity transport，以及 TutorialCuePlayer 接入。未修改生产代码。

**后续状态：** 本文是修改前审查快照。R1–R6 及主动操作、缓存、桥接进度、
边界生成的修复和新验证结果记录在
[连续音频修复验收](continuous-cue-audio-fixes.md)。下文历史复现工程使用旧接口；
当前回归工程为 `tests/CourseAudioTransportTests`。

## 结论与已通过部分

核心改造成立：统一 DSP epoch 驱动课程时间，cue localTime、字幕、视觉定位由该位置派生；顺序跨 cue 不再重新 Prepare/Stop/Play 音频。当前内容按声明的 24 kHz 单声道格式重新运行规划器，full 的 83 个音频段合为 32 个块，51 个连续交界被合并；quick 为 32 个块。用户已反馈顺播气口自然，本次没有独立录音验证这一主观结果。

重新执行 `dotnet run --project tests/AnimationModelTests`，结果为 `OK AnimationModelTests: all checks passed`。

现有测试只编译 Core，不包含 UnityCourseAudioTransport，其 fake transport 不模拟排程时间、双 source 占用、PCM 创建和失败回调的实际路径。因此模型测试通过不等于这些边界已验收。

另建隔离复现工程：

- [工程](/D:/workspace/board/.tmp/cue-audio-review/Review.csproj)
- [复现用例](/D:/workspace/board/.tmp/cue-audio-review/Program.cs)
- [Unity API 边界替身](/D:/workspace/board/.tmp/cue-audio-review/UnityStubs.cs)
- [运行结果](/D:/workspace/board/.tmp/cue-audio-review/results.txt)

运行命令：`dotnet run --project .tmp/cue-audio-review/Review.csproj`。

工程直接编译当前 UnityCourseAudioTransport 源码并引用真实 Core，只有 Unity API 用确定性的内存/时钟替身，不执行网络访问，不模拟音频混音器。它复现的是 C# 控制流和预约参数，不能替代设备输出或音质测试。工程位于已被 git 忽略的 `.tmp` 目录。

## 建议修补的问题

### R1 [P2] 跳转/恢复先定 DSP 起点，后创建 PCM，可能提交过期预约

位置：[UnityCourseAudioTransport.cs:396](/D:/workspace/board/clients/unity/Assets/Scripts/Tutorial/Animation/Unity/UnityCourseAudioTransport.cs:396)、[CueCoursePlaybackController.cs:271](/D:/workspace/board/clients/unity/Assets/Scripts/Tutorial/Animation/Core/CueCoursePlaybackController.cs:271)。

`SeekTo`/`Resume` 提前用当前 dspTime + 0.08 确定锚点，随后 `ScheduleAhead` 调用 GetOrCreateOutputClip。未缓存的目标会进行 GetData、整块后缀复制、包络和 AudioClip.Create/SetData。预约是否来得及的判断发生在创建之前，创建之后不复查。

隔离复现：当前 DSP 为 0.500，跳到课程 1.000；模拟输出 clip 创建耗时 200 ms。调用 PlayScheduled 时 DSP 已为 0.700，传入预约仍是 0.580，逻辑位置已经到 1.120。这个 200 ms 是注入的测试条件，不是本机测量值。

结果：提交过期预约，音频与课程时钟的共同起点不再有保障。移动设备上较长片段的首次恢复/seek、内存分配或 GC 停顿都可能触发；顺播缓存命中不一定暴露。

建议：目标准备完毕后才共同提交 course/DSP 锚点；将准备与预约提交分开，提交前校验剩余余量。若过期，重新建立共同锚点，不允许 executor 私自延后声音而控制器仍用旧锚点。余量应参考实际 DSP buffer 和设备准备耗时。

### R2 [P2] underrun 冻结在检测时刻，会跳过尚未播放的语音，且没有自动恢复路径

位置：[UnityCourseAudioTransport.cs:196](/D:/workspace/board/clients/unity/Assets/Scripts/Tutorial/Animation/Unity/UnityCourseAudioTransport.cs:196)、[CueCoursePlaybackController.cs:705](/D:/workspace/board/clients/unity/Assets/Scripts/Tutorial/Animation/Core/CueCoursePlaybackController.cs:705)、[TutorialCuePlayer.CueTimeline.cs:584](/D:/workspace/board/clients/unity/Assets/Scripts/Tutorial/TutorialCuePlayer.CueTimeline.cs:584)。

缺少当前块预约时，transport 回调传入的是当前 courseTime，而不是该块尚未播放的起点。控制器又取 `Math.Max(courseTime, courseClock.CourseTime)`。因此等待期间没播出的部分已被当作播放进度消耗。集成层只记录日志；Buffering 下 Tick 直接返回，没有预备完成后自动恢复的路径。

隔离复现：两个不同文件，各长 2 秒。首次预约后主线程一直到课程 2.500 才 Tick，第二块应从 2.000 开始但未预约。实际冻结在 2.500；继续 Tick 到 DSP 8 秒仍停在 Buffering。用户手动恢复将从 2.500 播，丢掉第二块开头半秒。

建议：记录最早未输出的课程区间，从该位置进入缓冲并重建音画状态。补齐预备成功后的受控恢复；对失败设置有限重试或明确错误状态。不能简单自动 Resume 当前检测时刻，否则只是把丢词隐藏掉。

### R3 [P2] 音频设备变化回调没有订阅，动态 PCM 重建也不完整

位置：[TutorialCuePlayer.CueTimeline.cs:455](/D:/workspace/board/clients/unity/Assets/Scripts/Tutorial/TutorialCuePlayer.CueTimeline.cs:455)。

全项目 Scripts 搜索只找到 `OnAudioConfigurationChanged` 的函数定义，没有 `AudioSettings.OnAudioConfigurationChanged += ...`。它是事件而非仅靠同名 MonoBehaviour 方法就会调用的消息。Update 只比较 outputSampleRate，因此采样率不变的耳机/输出设备切换不能被这一轮询覆盖。

即使通过采样率变化触发了 Rebase，当前只是重新 Schedule 并复用输出缓存，没有完整处理音频系统重置后动态 AudioClip 的有效性。Unity 官方说明脚本创建/修改的 clip 及播放状态在音频配置重置后需要重建：[Audio Settings](https://docs.unity3d.com/6000.0/Documentation/Manual/class-AudioSettings.html)。

建议：在生命周期内配对订阅/取消订阅；保留最后可靠的课程位置，取消旧预约，重建失效动态 PCM clip，再统一恢复。不要将新 DSP 时钟值与旧 epoch 相减来推断恢复位置。真机覆盖蓝牙连接/断开、有线耳机和后台恢复，包含输出采样率不变的情况。

此项由调用关系与 API 文档核实，未做物理设备切换测试。

### R4 [P2] 同步失败回调后，控制器把 Failed 覆盖为 Playing

位置：[CueCoursePlaybackController.cs:571](/D:/workspace/board/clients/unity/Assets/Scripts/Tutorial/Animation/Core/CueCoursePlaybackController.cs:571)，相同模式也存在于 SeekTo、Resume 和 RescheduleFromCurrentPosition。

`audio.Schedule()` 可以同步触发 OnFailed，例如 PCM 范围超界或 clip 创建失败。控制器 Fail 已执行，但 Schedule 返回后 ApplyStart 仍 AnchorAt 并 SetState(Playing)，没有检查状态或 generation 是否已改变。

此外 PrewarmCourseClips 遇到 error 只 break，PrepareRoutine 随后仍宣告准备成功，使可在准备时发现的问题延迟到 Schedule。

隔离复现：计划请求 1.000 秒，解码 clip 只有 0.990 秒，落在当前 0.02 秒范围容差内。Prewarm 的 PCM 越界错误未阻止 OnPrepared；Schedule 发出一次 Failed 后，最终 `Prepare` 返回 true、状态 Playing、实际预约数为 0；推进时钟后 CourseTime 仍前进到 0.500。

建议：准备错误立即上报并停止准备；每次外部 transport 调用返回后校验 generation/状态，失败或取消后不得继续提交时钟、Playing 状态和 CueChanged。Fail 本身也应冻结课程时钟，避免错误状态的进度继续前进。

### R5 [P2] 两个 source 加 20 ms 延迟释放，无法连续调度密集短片段

位置：[UnityCourseAudioTransport.cs:405](/D:/workspace/board/clients/unity/Assets/Scripts/Tutorial/Animation/Unity/UnityCourseAudioTransport.cs:405)、[UnityCourseAudioTransport.cs:592](/D:/workspace/board/clients/unity/Assets/Scripts/Tutorial/Animation/Unity/UnityCourseAudioTransport.cs:592)。

每个 block 独占一个 source，两个预约后没有更多 slot；已结束 slot 还要等待 20 ms 才可重用。实现中没有将密集短片段预混成更长输出块。

隔离复现：三个不同素材，时长 10 ms、10 ms、1 s。以 60 fps 驱动，在第三块开始后进入 Buffering；总共只发出了前两个预约。单纯增大 lookahead 不能解决 slot 不足。

建议：将短 audio/silence 序列预渲染为足够长的输出块，或实现能覆盖前瞻窗口的 source 池及明确释放策略。不要仅删除 20 ms 等待就认为支持任意短片段——主线程仍可能来不及接续预约。

当前 quick/full 的常规块长度未触发本例，但方案要求的一 cue 多音频段和短片段支持并未完整实现。

### R6 [P2] 按源帧重算时长，再与未量化 course.start 比较，会误报重叠

位置：[CueAudioTimelinePlanner.cs:404](/D:/workspace/board/clients/unity/Assets/Scripts/Tutorial/Animation/Core/CueAudioTimelinePlanner.cs:404)、[CueAudioTimelinePlanner.cs:447](/D:/workspace/board/clients/unity/Assets/Scripts/Tutorial/Animation/Core/CueAudioTimelinePlanner.cs:447)。

前段 CourseEnd 使用四舍五入后的源帧数重算，后段 CourseStart 仍是 plan 原秒数；检查容差只有 1 微秒。小于一采样帧的量化差就可能被当作真实重叠。反方向的差值也可能被 InsertGaps 当作 gap，阻断本应连续的合并。

隔离复现：44.1 kHz 同一源连续区间 [0,0.015)、[0.015,0.030)，课程也在 0.015 相接。第一段 662 帧对应 0.015011 秒，规划器抛出 `audio entries overlap`。内容的秒级区间原本完全连续。

建议：区分原计划覆盖关系与采样量化，使用统一的绝对输出帧边界进行相邻判定；同时维持源连续区间的帧一致性。不能只将容差放宽到几十毫秒，吞掉真实编辑。新增 44.1 kHz 和非整帧时间点测试。

## 可以后续优化的细节

- **暂停/主动跳转仍是硬切旧声音。** Pause 和 Schedule 都直接 StopCourseSources；目标有短淡入，但旧声音没有采样级淡出。正常自动跨 cue 已改善，主动操作的轻微点击仍有优化空间。需要规划短停止过渡并同步逻辑位置，而不是在主线程逐帧改 volume。
- **按字节管理缓存。** 当前缓存限制 24 个输出 clip，而 full 最长合并块约 79.444 秒，24 kHz 单声道 float PCM 约 7.27 MiB。该块内不同 seek/resume 起点各复制一个大后缀，可能缓存多份近似相同的长音频；源 AudioClip 还整轨保留。建议改为字节预算和有界区间缓存，记录真机峰值内存/GC 后决定是否进一步分页。
- **Android 的预约等待期。** 控制器在未来 DSP 起点之前已报告 Playing，Unity 的课程时钟会冻结等待，但原生进度条收到 isPlaying 后立即开始本地插值；直到后续状态刷新，进度条可能先走约启动余量。宜提供等待/起播事件或在真实起播时刷新位置。这不等于音频和 Unity cue 存在两套时钟，是桥接层的展示误差。
- **缓存身份和诊断身份。** outputClipCache key 只有 blockIndex、帧范围和 fade，没有 asset/内容版本；当前标准 ReloadGame 会 Dispose，降低了跨内容复用的风险，但直接复用 transport.Prepare 不具备正确隔离。边界诊断按 segment.id 查找，而 `audio.slice.0` 在多个 cue 中复用，可能指向错误块。后续应使用内容身份及 cue+segment 复合标识。
- **文档同步。** 部分协议、Content.cs 注释仍描述旧 CuePlaybackController 是唯一播放器，建议改为课程 transport 的实际职责。

## 建议修复次序与测试边界

先处理 R1、R2、R4：确保慢设备、缓冲和失败时不会出现漏词或音画分离；并修 R3，使耳机与后台恢复路径有效。R5/R6 在扩展短片段和其他采样率前补齐。最后做内存和主动切换的体验优化。

本次未重新启动 Unity 编译或打 APK，也没有重新录制真机音频。已有 Android 构建日志显示先前构建成功，但不将历史日志作为本次独立验证。上面的复现输出只证明逻辑/排程问题；修复后仍需真机覆盖首次播放、缓存播放、暂停恢复、快速拖动、长卡顿和设备切换。
