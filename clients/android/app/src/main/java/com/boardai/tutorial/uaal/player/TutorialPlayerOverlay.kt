package com.boardai.tutorial.uaal.player

import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.core.tween
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.slideInVertically
import androidx.compose.animation.slideOutVertically
import androidx.compose.foundation.gestures.detectHorizontalDragGestures
import androidx.compose.foundation.gestures.detectTapGestures
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.ExperimentalLayoutApi
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.systemBarsIgnoringVisibility
import androidx.compose.foundation.layout.windowInsetsPadding
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.rememberUpdatedState
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.boardai.tutorial.uaal.UnityStatus
import com.boardai.tutorial.uaal.catalog.GameCatalogEntry
import com.boardai.tutorial.uaal.content.ContentUpdateUiState
import com.boardai.tutorial.uaal.qa.QaPanel
import com.boardai.tutorial.uaal.qa.QaRepository
import com.boardai.tutorial.uaal.timeline.TutorialTimeline

private const val QA_TRANSITION_MILLIS = 280

/**
 * Full-screen native control layer.
 *
 * The composable is transparent apart from the actual chrome.  It keeps a
 * root tap/double-tap detector so the picture itself toggles controls and the
 * left/right half performs double-tap relative seek without binding horizontal
 * drag gestures to seek.
 *
 * QA mode is a sibling layer above the controls:
 *   controls down + fade out, QA panel down + fade in (from the top).
 */
@OptIn(ExperimentalLayoutApi::class)
@Composable
fun TutorialPlayerOverlay(
    status: UnityStatus?,
    timeline: TutorialTimeline?,
    contentState: ContentUpdateUiState,
    game: GameCatalogEntry?,
    qaOpen: Boolean,
    qaRepository: QaRepository,
    onOpenQa: (cueId: String, positionInCue: Float, wasPlaying: Boolean) -> Unit,
    onCloseQa: () -> Unit,
    onCommand: (method: String, value: String) -> Unit,
    onCheckContentUpdate: () -> Unit,
    gesturePolicy: PlayerGesturePolicy = PlayerGesturePolicy()
) {
    val uiState = rememberPlayerOverlayUiState()
    val display = rememberPlayerDisplayState(uiState, status, timeline)

    val resumeCue = timeline?.cueAt(display.currentCueIndex)
    val resumeCueId = resumeCue?.id?.takeIf { it.isNotBlank() } ?: status?.cueId.orEmpty()
    val resumePositionInCue = if (resumeCue != null) {
        (display.shownGlobal - resumeCue.start).coerceIn(0f, resumeCue.duration)
    } else {
        status?.position?.takeIf { it.isFinite() && it >= 0f } ?: 0f
    }
    val wasPlayingBeforeQuestion = status?.isPlaying == true && status?.isPaused != true

    PlayerAutoHideEffect(uiState, display.paused, qaOpen)
    PlayerSeekFlashEffect(uiState)

    LaunchedEffect(status?.unityReady) {
        if (status?.unityReady == true && !uiState.controlsVisible) {
            uiState.controlsVisible = true
            uiState.controlGeneration++
        }
    }

    LaunchedEffect(qaOpen) {
        if (qaOpen) {
            uiState.showChapters = false
            uiState.showContentPanel = false
            uiState.scrubbing = false
        }
    }

    fun revealControls() {
        uiState.revealControls()
    }

    fun togglePlayPause() {
        revealControls()
        if (display.paused) onCommand("Resume", "") else onCommand("Pause", "")
    }

    fun seekBy(seconds: Float, reveal: Boolean) {
        uiState.seekBy(
            seconds = seconds,
            baseGlobal = if (uiState.scrubbing) uiState.scrubGlobal else display.shownGlobal,
            timeline = timeline,
            currentCueIndex = display.currentCueIndex,
            startPaused = display.paused,
            onCommand = onCommand
        )
        if (reveal) revealControls()
    }

    fun jumpToCue(cueId: String) {
        if (cueId.isBlank()) return
        uiState.jumpToCue(cueId, display.paused, onCommand)
        revealControls()
    }

    val statusState = rememberUpdatedState(status)
    val timelineState = rememberUpdatedState(timeline)
    val shownGlobalState = rememberUpdatedState(display.shownGlobal)
    val totalDurationState = rememberUpdatedState(display.totalDuration)
    val onCommandState = rememberUpdatedState(onCommand)
    val gesturePolicyState = rememberUpdatedState(gesturePolicy)

    fun finishScrubFromGesture() {
        uiState.finishScrubFromGesture(
            timeline = timelineState.value,
            currentCueIndex = statusState.value?.cueIndex ?: -1,
            startPaused = statusState.value?.isPaused == true,
            totalDuration = totalDurationState.value,
            onCommand = onCommandState.value
        )
    }

    Box(
        modifier = Modifier
            .fillMaxSize()
            .windowInsetsPadding(WindowInsets.systemBarsIgnoringVisibility)
            .pointerInput(qaOpen) {
                if (!qaOpen) {
                    var dragActive = false
                    var dragBaseGlobal = 0f
                    var dragAccumulatedX = 0f

                    detectHorizontalDragGestures(
                        onDragStart = { offset ->
                            val currentTimeline = timelineState.value
                            val total = totalDurationState.value
                            // Only the progress bar and the controls below it are
                            // protected from screen-swipe seeking.  Everything above
                            // (including the top row and the central play button)
                            // remains available for horizontal picture swipes.
                            val inBottomChrome = uiState.controlsVisible &&
                                offset.y >= size.height - 142.dp.toPx()
                            if (gesturePolicyState.value.allowFullScreenHorizontalSeek &&
                                !inBottomChrome &&
                                currentTimeline != null &&
                                currentTimeline.cueCount > 0 &&
                                total > 0f
                            ) {
                                dragActive = true
                                dragBaseGlobal = shownGlobalState.value
                                dragAccumulatedX = 0f
                                uiState.scrubbing = true
                                uiState.scrubGlobal = dragBaseGlobal
                                uiState.scrubTarget = currentTimeline.globalToCue(dragBaseGlobal)
                                uiState.controlsVisible = true
                                uiState.controlGeneration++
                            } else {
                                dragActive = false
                            }
                        },
                        onHorizontalDrag = { change, dragAmount ->
                            if (dragActive) {
                                val currentTimeline = timelineState.value
                                val total = totalDurationState.value
                                if (currentTimeline != null &&
                                    total > 0f &&
                                    size.width > 0
                                ) {
                                    dragAccumulatedX += dragAmount
                                    // Full screen width from left to right == +20% total duration.
                                    val secondsPerPixel = (0.20f * total) / size.width
                                    val desired = (
                                        dragBaseGlobal +
                                            dragAccumulatedX * secondsPerPixel
                                        ).coerceIn(0f, total)
                                    uiState.scrubGlobal = desired
                                    uiState.scrubTarget = currentTimeline.globalToCue(desired)
                                    change.consume()
                                }
                            }
                        },
                        onDragEnd = {
                            if (dragActive) finishScrubFromGesture()
                            dragActive = false
                        },
                        onDragCancel = {
                            if (dragActive) finishScrubFromGesture()
                            dragActive = false
                        }
                    )
                }
            }
            .pointerInput(qaOpen) {
                if (!qaOpen) {
                    detectTapGestures(
                        onTap = {
                            if (uiState.controlsVisible) {
                                uiState.controlsVisible = false
                            } else {
                                uiState.controlsVisible = true
                                uiState.controlGeneration++
                            }
                        },
                        onDoubleTap = {
                            // Double-tap anywhere is play/pause and intentionally
                            // leaves the control chrome exactly as it is.
                            val currentlyPaused = statusState.value?.isPaused == true
                            onCommandState.value(
                                if (currentlyPaused) "Resume" else "Pause",
                                ""
                            )
                        }
                    )
                }
            }
    ) {
        AnimatedVisibility(
            visible = (uiState.controlsVisible || uiState.scrubbing) && !qaOpen,
            enter = slideInVertically(
                animationSpec = tween(durationMillis = QA_TRANSITION_MILLIS),
                initialOffsetY = { fullHeight -> fullHeight }
            ) + fadeIn(animationSpec = tween(durationMillis = QA_TRANSITION_MILLIS)),
            exit = slideOutVertically(
                animationSpec = tween(durationMillis = QA_TRANSITION_MILLIS),
                targetOffsetY = { fullHeight -> fullHeight }
            ) + fadeOut(animationSpec = tween(durationMillis = QA_TRANSITION_MILLIS)),
            modifier = Modifier.fillMaxSize()
        ) {
            Box(modifier = Modifier.fillMaxSize()) {
                PlayerTopBar(
                    chapterPathText = if (display.unityReady) display.chapterPathText else null,
                    onOpenContent = {
                        revealControls()
                        uiState.showContentPanel = true
                    },
                    modifier = Modifier.align(Alignment.TopCenter)
                )

                PlayerCenterPlayPause(
                    paused = display.paused,
                    enabled = display.unityReady,
                    modifier = Modifier.align(Alignment.Center),
                    onClick = { togglePlayPause() }
                )

                PlayerTransportBar(
                    status = status,
                    timeline = timeline,
                    contentState = contentState,
                    volume = display.volume,
                    paused = display.paused,
                    scrubbing = uiState.scrubbing,
                    scrubGlobal = uiState.scrubGlobal,
                    scrubTarget = uiState.scrubTarget,
                    shownGlobal = display.shownGlobal,
                    totalDuration = display.totalDuration,
                    progressTotal = display.progressTotal,
                    onVolumeChange = { value ->
                        uiState.volumeDragging = true
                        uiState.localVolume = value.coerceIn(0f, 1f)
                        onCommand("SetVolume", formatPayload(uiState.localVolume))
                        revealControls()
                    },
                    onVolumeChangeFinished = {
                        uiState.volumeDragging = false
                        onCommand("SetVolume", formatPayload(uiState.localVolume))
                        revealControls()
                    },
                    onOpenChapters = {
                        revealControls()
                        uiState.showChapters = true
                    },
                    onOpenQa = {
                        uiState.showChapters = false
                        uiState.showContentPanel = false
                        uiState.scrubbing = false
                        onOpenQa(resumeCueId, resumePositionInCue, wasPlayingBeforeQuestion)
                    },
                    onSeekRelative = { delta -> seekBy(delta, reveal = true) },
                    onPrevious = {
                        revealControls()
                        onCommand("PreviousCue", "")
                    },
                    onNext = {
                        revealControls()
                        onCommand("NextCue", "")
                    },
                    onTogglePlayPause = { togglePlayPause() },
                    onScrubStart = { value ->
                        uiState.scrubbing = true
                        uiState.scrubGlobal = value
                        uiState.scrubTarget = timeline?.globalToCue(value)
                        uiState.controlGeneration++
                    },
                    onScrubChange = { value ->
                        uiState.scrubGlobal = value
                        uiState.scrubTarget = timeline?.globalToCue(value)
                    },
                    onScrubFinished = { finishScrubFromGesture() },
                    modifier = Modifier.align(Alignment.BottomCenter)
                )
            }
        }

        uiState.seekFlash
            ?.takeIf { !qaOpen }
            ?.let { flash ->
                Box(
                    modifier = Modifier
                        .align(Alignment.Center)
                        .clip(RoundedCornerShape(14.dp))
                        .background(Color.Black.copy(alpha = 0.72f))
                        .padding(horizontal = 22.dp, vertical = 12.dp)
                ) {
                    Text(
                        text = flash,
                        color = Color.White,
                        fontSize = 20.sp,
                        fontWeight = FontWeight.Bold
                    )
                }
            }

        if (!qaOpen && uiState.showChapters) {
            PlayerChapterSheet(
                timeline = timeline,
                currentCueIndex = display.currentCueIndex,
                onDismiss = { uiState.showChapters = false },
                onJumpToCue = { cueId -> jumpToCue(cueId) }
            )
        }

        if (!qaOpen && uiState.showContentPanel) {
            PlayerContentPanel(
                contentState = contentState,
                onDismiss = { uiState.showContentPanel = false },
                onCheck = onCheckContentUpdate
            )
        }

        AnimatedVisibility(
            visible = qaOpen,
            enter = slideInVertically(
                animationSpec = tween(durationMillis = QA_TRANSITION_MILLIS),
                initialOffsetY = { fullHeight -> -fullHeight }
            ) + fadeIn(animationSpec = tween(durationMillis = QA_TRANSITION_MILLIS)),
            exit = slideOutVertically(
                animationSpec = tween(durationMillis = QA_TRANSITION_MILLIS),
                targetOffsetY = { fullHeight -> -fullHeight }
            ) + fadeOut(animationSpec = tween(durationMillis = QA_TRANSITION_MILLIS)),
            modifier = Modifier.fillMaxSize()
        ) {
            QaPanel(
                game = game,
                status = status,
                timeline = timeline,
                repository = qaRepository,
                onClose = onCloseQa,
                modifier = Modifier.fillMaxSize()
            )
        }
    }
}
