# 连续 cue 音频修复验收

日期：2026-10-20。针对 `continuous-cue-audio-review.md` 的 R1–R6、
主动暂停/跳转、缓存与“2.4”声母截断问题。

## 根因与数据修复

“四”的问题发生在生成源区间时：上一段为了 context lead 独立前移 end，
下一段标题因 trim 分支未前移 start，两个区间之间漏掉了源 PCM。
TTS word 时间戳又不保证处于真实声母之前。无缝播放器无法播放未被任何
区间包含的采样，因此只改播放器或给边缘淡入不能修复漏词。

保留标题同样使用最多 0.15 秒前置上下文的修复；增加统一相邻源边界和
`source_boundary_contracts`。原始窗口连续、未显式裁尾的交界共享一个
源采样边界，保护上一段已知 speech end。固定文件读入和计划编译完成后
均校验连续性/重叠。显式标题裁尾与作者留空单独标记。

full 仍为 607.408 秒；30 个播放块、53 个连续交界被合并。
`setup_players/title_2_4` 仍从 20.055 秒开始，标题 0.08 秒 guard、
1 秒尾静音保留。不会继续挪动已经修复的课程时间。

## 播放器修改

- **R1：** 初始 PCM 就绪后由 transport 返回实际 DSP epoch，再提交共同
  课程时钟。预约余量参考 DSP buffer；每次 PlayScheduled 前复查期限。
- **R2：** 缺块时冻结在最早未播片段起点，自动恢复最多三次；连续推进
  一秒后重置恢复计数。先检查声音覆盖，再发布 cue 完成或视觉进度。
- **R3：** 配对订阅音频配置事件，覆盖同采样率设备切换；保存最后可靠
  位置，清理源与动态输出 clip，再解码并建立新 epoch。准备中暂停/恢复
  保留用户意图。
- **R4：** 预热错误立即失败；同步回调导致失败或 generation 改变后，
  禁止提交 Playing，错误状态冻结时钟。
- **R5：** 密集短片段预合成为较长 PCM 组（包含原定静音），长期连续块
  保持完整。两条预约声道加两条短期退出声道，取消 20 毫秒延迟释放。
- **R6：** 课程覆盖按原计划秒数检查；源范围和输出边界独立转换为整数
  采样，避免 44.1 kHz 的小数帧产生虚假重叠。
- **主动操作：** 暂停/同轨跳转在音频线程做 4 毫秒余弦淡出；旧 voice
  确认静音后才回收。每个 AudioSource 独立子对象与滤波器，不影响新
  预约。普通连续播放滤波器原样通过。跳转目标沿用短淡入。
- **缓存：** 输出 clip 16 MiB、额外源 PCM 24 MiB 字节预算；正在播放、
  即将播放的 clip 不驱逐。Prepare 清除旧计划输出缓存，内容根目录或
  设备变化清除解码缓存；边界诊断用 cue+segment 身份。
- **原生进度：** DSP epoch 到达前 `IsPlaying` 为 false，避免 Android
  在准备余量内提前插值。字幕、动画和进度仍由同一课程位置派生。

字节预算是缓存目标，不是进程内存硬上限：正在使用/预热的 clip、单个
超预算素材及渲染临时数组可能超过预算；原始解码 AudioClip 仍按整轨
持有。当前 full 最长连续块 79.444 秒，24 kHz 单声道 float PCM 为
7.27 MiB。尚未引入流式分页，真机峰值内存和 GC 需要单独测量。

## 验证

已通过：

- Python 内容/切片/时间轴测试：69 项。
- .NET AnimationModelTests：all checks passed。
- .NET CourseAudioTransportTests：all checks passed；包括慢准备、缓冲、
  密集短剪接、非整帧切点、混合采样率/声道、跨计划缓存隔离及长块多次
  seek 的字节预算。
- Unity Android Library 导出与 IL2CPP ARM64 调试 APK 构建成功。
- Android `testDebugUnitTest --rerun-tasks`：14 个测试套件、129 项，
  0 failures / 0 errors / 0 skipped；42 个 Gradle task 实际执行，
  未将缓存结果算作本次独立运行。

安装包：`clients/android/app/build/outputs/apk/debug/app-debug.apk`，
143,953,016 字节，SHA-256：
`7d0929ddddc24d42bf4938d676722a41f1fa9b95b10018f10c3143a91147d02e`。
本次未安装到测试机，也未修改设备 active content。

构建日志：`.tmp/cue-audio-final-build.log`；
Unity 日志：`clients/unity/Builds/AndroidLibrary/export.log`；
Android 测试日志：`.tmp/cue-audio-android-tests.log`。

当前回归入口：

```powershell
python -m unittest animation.test_build_cue_timeline animation.test_build_content_manifest animation.test_cue_timeline
dotnet run --project tests/AnimationModelTests
dotnet run --project tests/CourseAudioTransportTests
```

Unity transport 测试直接编译生产源代码，但 Unity API 是确定性替身；
不模拟实际混音器或 MP3 解码器。它验证控制流、预约参数和 PCM，不能
代替听感与物理设备切换测试。

设备验收仍应覆盖：2.4/2.5 标题、普通连续气口、首次冷跳转、暂停恢复、
快速连续拖动、后台恢复、同采样率蓝牙/耳机切换、长时间播放及内存峰值。
