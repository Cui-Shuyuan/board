# Course audio transport regression checks

From the repository root:

```powershell
dotnet run --project tests/CourseAudioTransportTests
dotnet run --project tests/AnimationModelTests
```

This console harness compiles the production Unity transport and release filter,
and references the production Core types through AnimationModelTests. Unity APIs
are deterministic in-memory doubles: DSP time and AudioClip creation delay can
be advanced deliberately; all source clips are injected, and network use fails.

Checks cover slow seek epoch commit, prewarm failure, earliest unheard position
after a scheduling stall, automatic recovery, dense edits, fractional-frame
boundaries, bit-identical continuous PCM, device cache invalidation, audio-thread
release across buffers, stereo gains, rapid seeks, and the actual quick/full plans.
It also checks mixed-rate mono/stereo rendering, cross-plan cache identity, and
byte-budget eviction after repeated seeks within an 80-second source.
The model suite also checks failure callbacks and bounded recovery retries.

These checks do not implement a Unity mixer, decoder, OS audio buffers, or device
events. Unity export and Android builds verify integration; listening/recording
on the target device is still required to verify audible output and device changes.

See the [course audio runtime](../../docs/tutorial/continuous-cue-audio.md) and
[device/build validation record](../../docs/tutorial/validation/2026-10-20-cue-audio.md)
for the implementation contract and recorded acceptance results.
