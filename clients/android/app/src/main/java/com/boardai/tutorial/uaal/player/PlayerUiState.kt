package com.boardai.tutorial.uaal.player

import android.os.SystemClock
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.Stable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableFloatStateOf
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableLongStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.runtime.withFrameNanos
import com.boardai.tutorial.uaal.UnityStatus
import com.boardai.tutorial.uaal.timeline.TimelineTarget
import com.boardai.tutorial.uaal.timeline.TutorialTimeline
import kotlinx.coroutines.delay
import kotlin.math.abs

/**
 * Compose state owned by [TutorialPlayerOverlay].
 *
 * Keeping the mutable state in one holder makes the overlay composable small
 * enough to stay focused on rendering and gesture routing.
 */
@Stable
internal class PlayerOverlayUiState {
    var controlsVisible by mutableStateOf(false)
    var controlGeneration by mutableIntStateOf(0)
    var scrubbing by mutableStateOf(false)
    var scrubGlobal by mutableFloatStateOf(0f)
    var scrubTarget by mutableStateOf<TimelineTarget?>(null)
    var showChapters by mutableStateOf(false)
    var seekFlash by mutableStateOf<String?>(null)
    var localVolume by mutableFloatStateOf(1f)
    var volumeDragging by mutableStateOf(false)

    // Optimistic seek display.  When the user commits a scrub, keep showing the
    // requested position until Unity sends a status that reflects the new cue /
    // position.  This prevents the handle from flashing back to the old anchor.
    var pendingSeekTarget by mutableStateOf<TimelineTarget?>(null)
    var pendingSeekGlobal by mutableFloatStateOf(0f)
    var pendingSeekStartedAt by mutableLongStateOf(0L)

    // Native smoothing anchor.  Unity now posts only on state changes; Android
    // advances the displayed cue-local position between events by frame.
    var anchorPosition by mutableFloatStateOf(0f)
    var anchorCueIndex by mutableIntStateOf(-1)
    var receivedAtMs by mutableLongStateOf(SystemClock.elapsedRealtime())
    var frameNowMs by mutableLongStateOf(SystemClock.elapsedRealtime())

    fun revealControls() {
        controlsVisible = true
        controlGeneration++
    }

    fun rememberOptimisticSeek(
        target: TimelineTarget,
        timelineForMapped: TutorialTimeline?
    ) {
        pendingSeekTarget = target
        pendingSeekGlobal = timelineForMapped?.cueIndexToGlobal(
            target.targetCue.index,
            target.localSeconds
        ) ?: target.targetCue.start
        pendingSeekStartedAt = SystemClock.elapsedRealtime()
    }

    fun clearPendingSeek() {
        pendingSeekTarget = null
        pendingSeekGlobal = 0f
    }

    fun seekBy(
        seconds: Float,
        baseGlobal: Float,
        timeline: TutorialTimeline?,
        currentCueIndex: Int,
        startPaused: Boolean,
        onCommand: (method: String, value: String) -> Unit
    ) {
        if (timeline != null && timeline.cueCount > 0) {
            val desired = (baseGlobal + seconds)
                .coerceIn(0f, timeline.totalDuration)
            val target = timeline.globalToCue(desired)
            rememberOptimisticSeek(target, timeline)
            sendTimelineSeek(
                target = target,
                currentCueIndex = currentCueIndex,
                send = onCommand,
                startPaused = startPaused
            )
        } else {
            onCommand("SeekRelative", formatPayload(seconds))
        }
        seekFlash = if (seconds < 0f) "-15s" else "+15s"
    }

    fun jumpToCue(
        cueId: String,
        startPaused: Boolean,
        onCommand: (method: String, value: String) -> Unit
    ) {
        if (cueId.isBlank()) return
        val pausedFlag = if (startPaused) "1" else "0"
        onCommand("PlayCueAt", "$cueId|0|$pausedFlag")
        showChapters = false
    }

    fun finishScrubFromGesture(
        timeline: TutorialTimeline?,
        currentCueIndex: Int,
        startPaused: Boolean,
        totalDuration: Float,
        onCommand: (method: String, value: String) -> Unit
    ) {
        val target = if (timeline != null && timeline.cueCount > 0) {
            timeline.globalToCue(scrubGlobal)
        } else {
            scrubTarget
        }
        if (target != null) {
            rememberOptimisticSeek(target, timeline)
            sendTimelineSeek(
                target = target,
                currentCueIndex = currentCueIndex,
                send = onCommand,
                startPaused = startPaused
            )
        } else if (totalDuration > 0f) {
            onCommand("SeekTo", formatPayload(scrubGlobal))
        }
        scrubbing = false
        scrubTarget = null
        controlsVisible = true
        controlGeneration++
    }
}

@Composable
internal fun rememberPlayerOverlayUiState(): PlayerOverlayUiState =
    remember { PlayerOverlayUiState() }

internal data class PlayerDisplayState(
    val paused: Boolean,
    val unityReady: Boolean,
    val volume: Float,
    val totalDuration: Float,
    val currentCueIndex: Int,
    val shownGlobal: Float,
    val progressTotal: Float,
    val chapterPathText: String?
)

/**
 * Derives the values needed by the UI and keeps the optimistic-seek and frame
 * interpolation anchors in sync with incoming Unity events.
 */
@Composable
internal fun rememberPlayerDisplayState(
    uiState: PlayerOverlayUiState,
    status: UnityStatus?,
    timeline: TutorialTimeline?
): PlayerDisplayState {
    val paused = status?.isPaused == true
    val unityReady = status?.unityReady == true
    val volume = if (uiState.volumeDragging) {
        uiState.localVolume.coerceIn(0f, 1f)
    } else {
        (status?.volume ?: uiState.localVolume).coerceIn(0f, 1f)
    }
    val totalDuration = timeline?.totalDuration ?: status?.duration ?: 0f
    val currentCueIndex = status?.cueIndex ?: -1

    LaunchedEffect(status?.volume, uiState.volumeDragging) {
        if (!uiState.volumeDragging) {
            status?.volume?.let { uiState.localVolume = it.coerceIn(0f, 1f) }
        }
    }

    LaunchedEffect(status) {
        val current = status ?: return@LaunchedEffect
        uiState.anchorPosition = current.position
        uiState.anchorCueIndex = current.cueIndex
        uiState.receivedAtMs = SystemClock.elapsedRealtime()
        uiState.frameNowMs = uiState.receivedAtMs

        val pending = uiState.pendingSeekTarget
        if (pending != null) {
            val sameCue = current.cueIndex == pending.targetCue.index
            val closeEnough = abs(current.position - pending.localSeconds) <= 1.5f
            val elapsed = SystemClock.elapsedRealtime() - uiState.pendingSeekStartedAt
            val timedOut = elapsed > 1500L
            // Ignore a status that was already in flight before the scrub.
            if ((sameCue && closeEnough && elapsed > 250L) || timedOut) {
                uiState.clearPendingSeek()
            }
        }
    }

    val isActuallyPlaying = status?.isPlaying == true && !paused
    LaunchedEffect(isActuallyPlaying, status?.position, status?.isPaused) {
        if (!isActuallyPlaying) return@LaunchedEffect
        while (true) {
            withFrameNanos { }
            uiState.frameNowMs = SystemClock.elapsedRealtime()
        }
    }

    val cueDuration = timeline?.cueAt(uiState.anchorCueIndex)?.duration
        ?: status?.duration
        ?: 0f
    val interpolatedLocal = if (isActuallyPlaying && cueDuration > 0f) {
        (uiState.anchorPosition + (uiState.frameNowMs - uiState.receivedAtMs) / 1000f)
            .coerceIn(0f, cueDuration)
    } else {
        uiState.anchorPosition.coerceAtLeast(0f)
    }
    val displayGlobal = if (timeline != null && uiState.anchorCueIndex in 0 until timeline.cueCount) {
        timeline.cueIndexToGlobal(uiState.anchorCueIndex, interpolatedLocal)
    } else {
        interpolatedLocal.coerceIn(0f, totalDuration.coerceAtLeast(0f))
    }
    val shownGlobal = when {
        uiState.scrubbing -> uiState.scrubGlobal
        uiState.pendingSeekTarget != null -> uiState.pendingSeekGlobal
        else -> displayGlobal
    }

    val chapterCue = when {
        uiState.scrubbing -> uiState.scrubTarget?.targetCue
        uiState.pendingSeekTarget != null -> uiState.pendingSeekTarget?.targetCue
        else -> timeline?.cueAt(currentCueIndex)
    }
    val chapterPathText = chapterCue
        ?.groupPath
        ?.joinToString(" > ")
        ?.takeIf { it.isNotBlank() }

    return PlayerDisplayState(
        paused = paused,
        unityReady = unityReady,
        volume = volume,
        totalDuration = totalDuration,
        currentCueIndex = currentCueIndex,
        shownGlobal = shownGlobal,
        progressTotal = totalDuration.coerceAtLeast(1f),
        chapterPathText = chapterPathText
    )
}

@Composable
internal fun PlayerAutoHideEffect(
    uiState: PlayerOverlayUiState,
    paused: Boolean,
    qaOpen: Boolean
) {
    LaunchedEffect(
        uiState.controlsVisible,
        uiState.scrubbing,
        uiState.showChapters,
        uiState.controlGeneration,
        paused,
        qaOpen
    ) {
        if (!qaOpen &&
            uiState.controlsVisible &&
            !uiState.scrubbing &&
            !uiState.showChapters
        ) {
            delay(3000)
            uiState.controlsVisible = false
        }
    }
}

@Composable
internal fun PlayerSeekFlashEffect(uiState: PlayerOverlayUiState) {
    LaunchedEffect(uiState.seekFlash) {
        val flash = uiState.seekFlash ?: return@LaunchedEffect
        delay(700)
        if (uiState.seekFlash == flash) uiState.seekFlash = null
    }
}
