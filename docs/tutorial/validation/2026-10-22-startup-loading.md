# Animation startup loading investigation

The current full plan contains 83 cues referencing 79 distinct audio assets.
`TutorialCuePlayer` uses `UnityCourseAudioTransport` for whole-course playback;
the older `UnityCueAudioExecutor` is not the active transport for this path.

Before this fix, `PrepareRoutine` issued one `GetAudioClip` request and yielded
until it finished before issuing the next. `OnPrepared` was called only after
all assets loaded, passed range checks, and initial PCM was prewarmed. File IO,
decode latency and coroutine resumption delays therefore accumulated across
the entire course. Sprite preloading runs separately and is not a readiness gate.

Connected-device Unity logs read on 2026-10-22 show:

- 22:54:28.950: full track plan mode requested.
- 22:54:29.323: log stack includes `LoadRuntime` / `ReloadGame`.
- 22:54:32.502: audio prepared, 79 assets, 163 entries, 79 blocks.
- 22:54:32.503: first audio group scheduled.

This is 3.552 seconds from track-mode request to audio readiness. The old logs
do not split IO, decoding, graphics contention and planning costs, so this is
an observed startup interval, not an isolated decoder measurement.

## Change

Load at most four audio files concurrently, refill available slots immediately,
and reuse decoded source clips. Preserve whole-course validation, actual decoded
sample formats, PCM prewarm and the shared DSP clock. Failure and cancellation
abort/dispose all outstanding requests. Emit load, planning/prewarm and total
preparation milliseconds for subsequent device comparison.

This removes sequential request waiting; it does not remove the all-assets
readiness gate. A future lazy-loading design would need verified format metadata
and explicit buffering on cold seeks before that gate could safely be relaxed.

## Verification

- `dotnet run --no-restore --project tests/CourseAudioTransportTests`: passed.
- `dotnet run --no-restore --project tests/AnimationModelTests`: passed.
- New deterministic checks cover four concurrent requests, out-of-order
  completion/refill, all-assets readiness, warm cache reuse, cancellation and
  load-failure cleanup, alongside existing synchronization and seek checks.

## Device deployment

Following the requested deployment, the Unity Android library export and Gradle
`assembleDebug` both succeeded. The updated APK was installed with `adb install -r`
on Redmi K30 Pro (`1138ee9b`), preserving existing application data.

- Package: `com.boardai.tutorial.uaal`, version `0.2.0` / code `2`.
- Device installation timestamp: `2026-10-22 23:15:43`.
- APK size: 143,932,612 bytes.
- SHA-256: `3fd6bda19141ff89bba348354b1a3b4b2dc49fc12947d14cfd51236fc9eeb0a4`.
- Build log: `.tmp/startup-parallel-build.log`.
- Activity cold launch: `Status: ok`, 1,368 ms reported by Android.

Activity launch time is not tutorial/audio preparation time. No measured post-fix
tutorial startup improvement is claimed yet. Compare repeated cold full/quick
opens using the new `prepared` timing fields; also exercise immediate back/reopen
and late seeks.
