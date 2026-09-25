package com.boardai.tutorial.uaal

import android.os.SystemClock
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.gestures.detectTapGestures
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.ExperimentalLayoutApi
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.layout.systemBarsIgnoringVisibility
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.windowInsetsPadding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.Slider
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.rememberModalBottomSheetState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableFloatStateOf
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableLongStateOf
import androidx.compose.runtime.mutableStateMapOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberUpdatedState
import androidx.compose.runtime.setValue
import androidx.compose.runtime.withFrameNanos
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.boardai.tutorial.uaal.content.ContentUpdateUiState
import com.boardai.tutorial.uaal.content.toDisplayText
import com.boardai.tutorial.uaal.timeline.ChapterNode
import com.boardai.tutorial.uaal.timeline.TimelineTarget
import com.boardai.tutorial.uaal.timeline.TutorialTimeline
import kotlinx.coroutines.delay
import java.util.Locale

private data class ChapterRow(
    val node: ChapterNode,
    val depth: Int
)

/**
 * Full-screen native control layer.
 *
 * The composable is transparent apart from the actual chrome.  It keeps a
 * root tap/double-tap detector so the picture itself toggles controls and the
 * left/right half performs double-tap relative seek without binding horizontal
 * drag gestures to seek.
 */
@OptIn(ExperimentalMaterial3Api::class, ExperimentalLayoutApi::class)
@Composable
fun TutorialPlayerOverlay(
    status: UnityStatus?,
    timeline: TutorialTimeline?,
    contentState: ContentUpdateUiState,
    onCommand: (method: String, value: String) -> Unit,
    onCheckContentUpdate: () -> Unit
) {
    var controlsVisible by remember { mutableStateOf(false) }
    var controlGeneration by remember { mutableIntStateOf(0) }
    var scrubbing by remember { mutableStateOf(false) }
    var scrubGlobal by remember { mutableFloatStateOf(0f) }
    var scrubTarget by remember { mutableStateOf<TimelineTarget?>(null) }
    var showChapters by remember { mutableStateOf(false) }
    var showContentPanel by remember { mutableStateOf(false) }
    var seekFlash by remember { mutableStateOf<String?>(null) }
    var localVolume by remember { mutableFloatStateOf(1f) }

    // Native smoothing anchor.  Unity posts roughly every 0.5 s; between posts
    // we advance the displayed cue-local position from the last known anchor.
    var anchorPosition by remember { mutableFloatStateOf(0f) }
    var anchorCueIndex by remember { mutableIntStateOf(-1) }
    var receivedAtMs by remember { mutableLongStateOf(SystemClock.elapsedRealtime()) }
    var frameNowMs by remember { mutableLongStateOf(SystemClock.elapsedRealtime()) }

    val paused = status?.isPaused == true
    val unityReady = status?.unityReady == true
    val volume = (status?.volume ?: localVolume).coerceIn(0f, 1f)
    val totalDuration = timeline?.totalDuration ?: status?.duration ?: 0f
    val currentCueIndex = status?.cueIndex ?: -1

    LaunchedEffect(status?.volume) {
        status?.volume?.let { localVolume = it.coerceIn(0f, 1f) }
    }

    LaunchedEffect(status) {
        val current = status ?: return@LaunchedEffect
        anchorPosition = current.position
        anchorCueIndex = current.cueIndex
        receivedAtMs = SystemClock.elapsedRealtime()
        frameNowMs = receivedAtMs
    }

    val isActuallyPlaying = status?.isPlaying == true && !paused
    LaunchedEffect(isActuallyPlaying, status?.position, status?.isPaused) {
        if (!isActuallyPlaying) return@LaunchedEffect
        while (true) {
            withFrameNanos { }
            frameNowMs = SystemClock.elapsedRealtime()
        }
    }

    val cueDuration = timeline?.cueAt(anchorCueIndex)?.duration
        ?: status?.duration
        ?: 0f
    val interpolatedLocal = if (isActuallyPlaying && cueDuration > 0f) {
        (anchorPosition + (frameNowMs - receivedAtMs) / 1000f)
            .coerceIn(0f, cueDuration)
    } else {
        anchorPosition.coerceAtLeast(0f)
    }
    val displayGlobal = if (timeline != null && anchorCueIndex in 0 until timeline.cueCount) {
        timeline.cueIndexToGlobal(anchorCueIndex, interpolatedLocal)
    } else {
        interpolatedLocal.coerceIn(0f, totalDuration.coerceAtLeast(0f))
    }
    val shownGlobal = if (scrubbing) scrubGlobal else displayGlobal
    val progressTotal = totalDuration.coerceAtLeast(1f)

    fun revealControls() {
        controlsVisible = true
        controlGeneration++
    }

    fun togglePlayPause() {
        revealControls()
        if (paused) onCommand("Resume", "") else onCommand("Pause", "")
    }

    fun seekBy(seconds: Float, reveal: Boolean) {
        val base = if (scrubbing) scrubGlobal else shownGlobal
        val currentTimeline = timeline
        if (currentTimeline != null && currentTimeline.cueCount > 0) {
            val desired = (base + seconds).coerceIn(0f, currentTimeline.totalDuration)
            sendTimelineSeek(
                target = currentTimeline.globalToCue(desired),
                currentCueIndex = currentCueIndex,
                send = onCommand
            )
        } else {
            onCommand("SeekRelative", formatPayload(seconds))
        }
        seekFlash = if (seconds < 0f) "-15s" else "+15s"
        if (reveal) revealControls()
    }

    fun jumpToCue(cueId: String) {
        if (cueId.isBlank()) return
        revealControls()
        onCommand("PlayCueAt", "$cueId|0")
        showChapters = false
    }

    LaunchedEffect(status?.unityReady) {
        if (status?.unityReady == true && !controlsVisible) {
            controlsVisible = true
            controlGeneration++
        }
    }

    LaunchedEffect(
        controlsVisible,
        scrubbing,
        showChapters,
        showContentPanel,
        controlGeneration,
        paused
    ) {
        if (controlsVisible && !scrubbing && !showChapters && !showContentPanel) {
            delay(3000)
            controlsVisible = false
        }
    }

    LaunchedEffect(seekFlash) {
        val flash = seekFlash ?: return@LaunchedEffect
        delay(700)
        if (seekFlash == flash) seekFlash = null
    }

    val statusState = rememberUpdatedState(status)
    val timelineState = rememberUpdatedState(timeline)
    val shownGlobalState = rememberUpdatedState(shownGlobal)
    val onCommandState = rememberUpdatedState(onCommand)

    Box(
        modifier = Modifier
            .fillMaxSize()
            .windowInsetsPadding(WindowInsets.systemBarsIgnoringVisibility)
            .pointerInput(Unit) {
                detectTapGestures(
                    onTap = {
                        if (controlsVisible) {
                            controlsVisible = false
                        } else {
                            controlsVisible = true
                            controlGeneration++
                        }
                    },
                    onDoubleTap = { offset ->
                        val delta = if (offset.x < size.width / 2f) -15f else 15f
                        val currentTimeline = timelineState.value
                        if (currentTimeline != null && currentTimeline.cueCount > 0) {
                            val desired = (shownGlobalState.value + delta)
                                .coerceIn(0f, currentTimeline.totalDuration)
                            sendTimelineSeek(
                                target = currentTimeline.globalToCue(desired),
                                currentCueIndex = statusState.value?.cueIndex ?: -1,
                                send = onCommandState.value
                            )
                        } else {
                            onCommandState.value("SeekRelative", formatPayload(delta))
                        }
                        seekFlash = if (delta < 0f) "-15s" else "+15s"
                    }
                )
            }
    ) {
        if (controlsVisible || scrubbing) {
            TopControlRow(
                status = status,
                timedOut = !unityReady,
                onOpenContent = {
                    revealControls()
                    showContentPanel = true
                },
                modifier = Modifier.align(Alignment.TopCenter)
            )

            CenterPlayPause(
                paused = paused,
                enabled = unityReady,
                modifier = Modifier.align(Alignment.Center),
                onClick = { togglePlayPause() }
            )

            BottomControlBar(
                status = status,
                timeline = timeline,
                contentState = contentState,
                volume = volume,
                paused = paused,
                scrubbing = scrubbing,
                scrubGlobal = scrubGlobal,
                scrubTarget = scrubTarget,
                shownGlobal = shownGlobal,
                totalDuration = totalDuration,
                progressTotal = progressTotal,
                onVolumeChange = { delta ->
                    localVolume = (volume + delta).coerceIn(0f, 1f)
                    onCommand("AdjustVolume", formatPayload(delta))
                    revealControls()
                },
                onOpenChapters = {
                    revealControls()
                    showChapters = true
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
                    scrubbing = true
                    scrubGlobal = value
                    scrubTarget = timeline?.globalToCue(value)
                    controlGeneration++
                },
                onScrubChange = { value ->
                    scrubGlobal = value
                    scrubTarget = timeline?.globalToCue(value)
                },
                onScrubFinished = {
                    val currentTimeline = timeline
                    val target = if (currentTimeline != null && currentTimeline.cueCount > 0) {
                        currentTimeline.globalToCue(scrubGlobal)
                    } else {
                        scrubTarget
                    }
                    if (target != null) {
                        sendTimelineSeek(target, currentCueIndex, onCommand)
                    } else if (totalDuration > 0f) {
                        onCommand("SeekTo", formatPayload(scrubGlobal))
                    }
                    scrubbing = false
                    scrubTarget = null
                    revealControls()
                },
                onSeekToGlobal = { value ->
                    val currentTimeline = timeline
                    if (currentTimeline != null && currentTimeline.cueCount > 0) {
                        sendTimelineSeek(
                            target = currentTimeline.globalToCue(value),
                            currentCueIndex = currentCueIndex,
                            send = onCommand
                        )
                    } else if (totalDuration > 0f) {
                        onCommand("SeekTo", formatPayload(value))
                    }
                    revealControls()
                },
                modifier = Modifier.align(Alignment.BottomCenter)
            )
        }

        seekFlash?.let { flash ->
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

        if (showChapters) {
            ChaptersSheet(
                timeline = timeline,
                currentCueIndex = currentCueIndex,
                onDismiss = { showChapters = false },
                onJumpToCue = { cueId -> jumpToCue(cueId) }
            )
        }

        if (showContentPanel) {
            ContentStatusDialog(
                contentState = contentState,
                onDismiss = { showContentPanel = false },
                onCheck = onCheckContentUpdate
            )
        }
    }
}

@Composable
private fun TopControlRow(
    status: UnityStatus?,
    timedOut: Boolean,
    onOpenContent: () -> Unit,
    modifier: Modifier = Modifier
) {
    Row(
        modifier = modifier
            .fillMaxWidth()
            .background(
                Brush.verticalGradient(
                    listOf(Color.Black.copy(alpha = 0.68f), Color.Transparent)
                )
            )
            .padding(horizontal = 12.dp, vertical = 8.dp),
        verticalAlignment = Alignment.CenterVertically
    ) {
        Text(
            text = "BoardAI · ${buildStateLabel(status, timedOut)}",
            color = Color.White,
            fontSize = 14.sp,
            fontWeight = FontWeight.Medium,
            maxLines = 1,
            overflow = TextOverflow.Ellipsis
        )
        val cueLabel = buildCueLabel(status)
        if (!cueLabel.isNullOrBlank()) {
            Spacer(Modifier.width(10.dp))
            Text(
                text = cueLabel,
                color = Color.White.copy(alpha = 0.82f),
                fontSize = 12.sp,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
                modifier = Modifier.weight(1f)
            )
        } else {
            Spacer(Modifier.weight(1f))
        }
        ControlButton(
            label = "内容",
            compact = true,
            modifier = Modifier.width(72.dp),
            onClick = onOpenContent
        )
    }
}

@Composable
private fun CenterPlayPause(
    paused: Boolean,
    enabled: Boolean,
    modifier: Modifier = Modifier,
    onClick: () -> Unit
) {
    val interactionSource = remember { MutableInteractionSource() }
    Box(
        modifier = modifier
            .size(86.dp)
            .clip(CircleShape)
            .background(Color.Black.copy(alpha = if (enabled) 0.55f else 0.28f))
            .clickable(
                interactionSource = interactionSource,
                indication = null,
                enabled = enabled,
                onClick = onClick
            ),
        contentAlignment = Alignment.Center
    ) {
        Text(
            text = if (paused) "▶" else "❚❚",
            color = Color.White.copy(alpha = if (enabled) 1f else 0.4f),
            fontSize = 34.sp,
            fontWeight = FontWeight.Bold
        )
    }
}

@Composable
private fun BottomControlBar(
    status: UnityStatus?,
    timeline: TutorialTimeline?,
    contentState: ContentUpdateUiState,
    volume: Float,
    paused: Boolean,
    scrubbing: Boolean,
    scrubGlobal: Float,
    scrubTarget: TimelineTarget?,
    shownGlobal: Float,
    totalDuration: Float,
    progressTotal: Float,
    onVolumeChange: (Float) -> Unit,
    onOpenChapters: () -> Unit,
    onSeekRelative: (Float) -> Unit,
    onPrevious: () -> Unit,
    onNext: () -> Unit,
    onTogglePlayPause: () -> Unit,
    onScrubStart: (Float) -> Unit,
    onScrubChange: (Float) -> Unit,
    onScrubFinished: () -> Unit,
    onSeekToGlobal: (Float) -> Unit,
    modifier: Modifier = Modifier
) {
    Column(
        modifier = modifier
            .fillMaxWidth()
            .background(
                Brush.verticalGradient(
                    listOf(Color.Transparent, Color.Black.copy(alpha = 0.88f))
                )
            )
            .padding(horizontal = 12.dp, vertical = 10.dp)
    ) {
        val cueText = status?.cueText.orEmpty()
        if (cueText.isNotBlank()) {
            Text(
                text = cueText,
                color = Color.White.copy(alpha = 0.92f),
                fontSize = 13.sp,
                fontWeight = FontWeight.Medium,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
                modifier = Modifier.padding(bottom = 4.dp)
            )
        }

        if (scrubbing) {
            ScrubPreview(
                timeline = timeline,
                target = scrubTarget,
                globalSeconds = scrubGlobal,
                modifier = Modifier.padding(bottom = 6.dp)
            )
        }

        Row(
            modifier = Modifier.fillMaxWidth(),
            verticalAlignment = Alignment.CenterVertically
        ) {
            Text(
                text = formatTime(scrubGlobalOr(scrubbing, scrubGlobal, shownGlobal)),
                color = Color.White,
                fontSize = 12.sp,
                maxLines = 1,
                textAlign = TextAlign.Start,
                modifier = Modifier.width(56.dp)
            )
            Slider(
                value = (if (scrubbing) scrubGlobal else shownGlobal)
                    .coerceIn(0f, progressTotal),
                valueRange = 0f..progressTotal,
                onValueChange = { value ->
                    if (status?.unityReady == true) {
                        if (!scrubbing) onScrubStart(value) else onScrubChange(value)
                    }
                },
                onValueChangeFinished = {
                    if (status?.unityReady == true) onScrubFinished()
                },
                modifier = Modifier
                    .weight(1f)
                    .padding(horizontal = 8.dp)
            )
            Text(
                text = formatTime(totalDuration),
                color = Color.White,
                fontSize = 12.sp,
                maxLines = 1,
                textAlign = TextAlign.End,
                modifier = Modifier.width(56.dp)
            )
        }

        Spacer(Modifier.height(4.dp))

        Row(
            modifier = Modifier.fillMaxWidth(),
            horizontalArrangement = Arrangement.spacedBy(6.dp),
            verticalAlignment = Alignment.CenterVertically
        ) {
            ControlButton(
                label = if (paused) "▶" else "❚❚",
                compact = true,
                modifier = Modifier.weight(1f),
                onClick = onTogglePlayPause
            )
            ControlButton(
                label = "⏮",
                compact = true,
                modifier = Modifier.weight(1f),
                onClick = onPrevious
            )
            ControlButton(
                label = "-15",
                compact = true,
                modifier = Modifier.weight(1f),
                onClick = { onSeekRelative(-15f) }
            )
            ControlButton(
                label = "+15",
                compact = true,
                modifier = Modifier.weight(1f),
                onClick = { onSeekRelative(15f) }
            )
            ControlButton(
                label = "⏭",
                compact = true,
                modifier = Modifier.weight(1f),
                onClick = onNext
            )
        }

        Spacer(Modifier.height(6.dp))

        Row(
            modifier = Modifier.fillMaxWidth(),
            verticalAlignment = Alignment.CenterVertically
        ) {
            Text(
                text = "音量 ${(volume * 100f).toInt()}%",
                color = Color.White.copy(alpha = 0.86f),
                fontSize = 12.sp,
                maxLines = 1
            )
            Spacer(Modifier.width(6.dp))
            ControlButton(
                label = "−",
                compact = true,
                modifier = Modifier.width(38.dp),
                onClick = { onVolumeChange(-0.1f) }
            )
            Spacer(Modifier.width(6.dp))
            ControlButton(
                label = "+",
                compact = true,
                modifier = Modifier.width(38.dp),
                onClick = { onVolumeChange(0.1f) }
            )
            Spacer(Modifier.weight(1f))
            Text(
                text = contentState.activeVersion?.let { "v$it" } ?: "内容未激活",
                color = Color.White.copy(alpha = 0.62f),
                fontSize = 11.sp,
                maxLines = 1
            )
            Spacer(Modifier.width(8.dp))
            ControlButton(
                label = "章节",
                compact = true,
                modifier = Modifier.width(84.dp),
                onClick = onOpenChapters
            )
        }
    }
}

@Composable
private fun ScrubPreview(
    timeline: TutorialTimeline?,
    target: TimelineTarget?,
    globalSeconds: Float,
    modifier: Modifier = Modifier
) {
    Column(
        modifier = modifier
            .fillMaxWidth()
            .clip(RoundedCornerShape(10.dp))
            .background(Color.Black.copy(alpha = 0.86f))
            .padding(horizontal = 10.dp, vertical = 8.dp)
    ) {
        val cue = target?.targetCue
        if (cue != null && timeline != null) {
            if (cue.groupPath.isNotEmpty()) {
                Text(
                    text = cue.groupPath.joinToString(" > "),
                    color = Color.White.copy(alpha = 0.74f),
                    fontSize = 11.sp,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis
                )
            }
            Text(
                text = cue.id,
                color = Color.White,
                fontSize = 12.sp,
                fontWeight = FontWeight.SemiBold,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis
            )
            Text(
                text = "cue ${cue.index + 1}/${timeline.cueCount} · " +
                    "${formatTime(target.localSeconds)} / ${formatTime(cue.duration)}",
                color = Color.White.copy(alpha = 0.86f),
                fontSize = 12.sp,
                maxLines = 1
            )
            if (cue.text.isNotBlank()) {
                Text(
                    text = cue.text,
                    color = Color.White.copy(alpha = 0.72f),
                    fontSize = 11.sp,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis
                )
            }
            Text(
                text = "目标 ${formatTime(globalSeconds)}",
                color = Color.White.copy(alpha = 0.78f),
                fontSize = 11.sp,
                maxLines = 1
            )
            if (target.snappedToChapterStart) {
                Text(
                    text = "已吸附：${target.snappedChapter?.title ?: cue.text.ifBlank { cue.id }}",
                    color = Color(0xFFFFCC80),
                    fontSize = 11.sp,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis
                )
            } else if (target.snappedToCueStart) {
                Text(
                    text = "已吸附：${cue.text.ifBlank { cue.id }}",
                    color = Color(0xFFFFCC80),
                    fontSize = 11.sp,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis
                )
            }
        } else {
            Text(
                text = "目标 ${formatTime(globalSeconds)}",
                color = Color.White.copy(alpha = 0.86f),
                fontSize = 12.sp
            )
        }
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun ChaptersSheet(
    timeline: TutorialTimeline?,
    currentCueIndex: Int,
    onDismiss: () -> Unit,
    onJumpToCue: (String) -> Unit
) {
    val sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true)
    ModalBottomSheet(
        onDismissRequest = onDismiss,
        sheetState = sheetState
    ) {
        ChapterTree(
            timeline = timeline,
            currentCueIndex = currentCueIndex,
            onJumpToCue = onJumpToCue
        )
    }
}

@Composable
private fun ChapterTree(
    timeline: TutorialTimeline?,
    currentCueIndex: Int,
    onJumpToCue: (String) -> Unit
) {
    if (timeline == null) {
        Text(
            text = "时间轴未加载",
            color = Color.White,
            modifier = Modifier.padding(24.dp)
        )
        return
    }

    val expanded = remember(timeline) { mutableStateMapOf<String, Boolean>() }
    val currentPath = timeline.cueAt(currentCueIndex)?.groupPath ?: emptyList()
    val currentPathKey = pathKey(currentPath)

    LaunchedEffect(timeline, currentCueIndex) {
        val prefix = mutableListOf<String>()
        for (title in currentPath) {
            prefix += title
            expanded[pathKey(prefix)] = true
        }
    }

    val rows = mutableListOf<ChapterRow>()
    fun visit(nodes: List<ChapterNode>, depth: Int) {
        for (node in nodes) {
            rows += ChapterRow(node = node, depth = depth)
            if (expanded[node.key] == true) {
                visit(node.children, depth + 1)
            }
        }
    }
    visit(timeline.rootChapters, 0)

    Column(
        modifier = Modifier
            .fillMaxWidth()
            .navigationBarsPadding()
            .padding(bottom = 8.dp)
    ) {
        Text(
            text = "章节",
            color = Color.White,
            fontSize = 20.sp,
            fontWeight = FontWeight.Bold,
            modifier = Modifier.padding(horizontal = 16.dp, vertical = 8.dp)
        )
        if (rows.isEmpty()) {
            Text(
                text = "当前内容没有章节信息",
                color = Color.White.copy(alpha = 0.7f),
                modifier = Modifier.padding(16.dp)
            )
        } else {
            LazyColumn(
                modifier = Modifier
                    .fillMaxWidth()
                    .heightIn(max = 480.dp)
            ) {
                items(rows, key = { it.node.key }) { row ->
                    ChapterRowItem(
                        row = row,
                        isCurrent = row.node.key == currentPathKey,
                        expanded = expanded[row.node.key] == true,
                        onToggle = {
                            expanded[row.node.key] = expanded[row.node.key] != true
                        },
                        onJump = { onJumpToCue(row.node.firstCueId) }
                    )
                }
            }
        }
    }
}

@Composable
private fun ChapterRowItem(
    row: ChapterRow,
    isCurrent: Boolean,
    expanded: Boolean,
    onToggle: () -> Unit,
    onJump: () -> Unit
) {
    val interactionSource = remember { MutableInteractionSource() }
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .background(
                if (isCurrent) MaterialTheme.colorScheme.primary.copy(alpha = 0.20f)
                else Color.Transparent
            )
            .clickable(
                interactionSource = interactionSource,
                indication = null,
                onClick = {
                    if (row.node.children.isNotEmpty()) onToggle() else onJump()
                }
            )
            .padding(
                start = (12 + row.depth * 16).dp,
                end = 12.dp,
                top = 7.dp,
                bottom = 7.dp
            ),
        verticalAlignment = Alignment.CenterVertically
    ) {
        if (row.node.children.isNotEmpty()) {
            Text(
                text = if (expanded) "▾" else "▸",
                color = Color.White.copy(alpha = 0.78f),
                fontSize = 14.sp,
                modifier = Modifier.width(20.dp)
            )
        } else {
            Spacer(Modifier.width(20.dp))
        }

        Text(
            text = row.node.title,
            color = Color.White,
            fontSize = 14.sp,
            fontWeight = if (isCurrent) FontWeight.SemiBold else FontWeight.Normal,
            maxLines = 1,
            overflow = TextOverflow.Ellipsis,
            modifier = Modifier.weight(1f)
        )

        if (isCurrent) {
            Spacer(Modifier.width(6.dp))
            Text(
                text = "当前",
                color = MaterialTheme.colorScheme.primary,
                fontSize = 11.sp,
                fontWeight = FontWeight.SemiBold
            )
        }

        if (row.node.children.isNotEmpty() && row.node.firstCueId.isNotBlank()) {
            Spacer(Modifier.width(8.dp))
            ControlButton(
                label = "跳到本节",
                compact = true,
                modifier = Modifier.width(84.dp),
                onClick = onJump
            )
        }
    }
}

@Composable
private fun ContentStatusDialog(
    contentState: ContentUpdateUiState,
    onDismiss: () -> Unit,
    onCheck: () -> Unit
) {
    val busy = contentState.status == com.boardai.tutorial.uaal.content.ContentUpdateStatus.Checking ||
        contentState.status is com.boardai.tutorial.uaal.content.ContentUpdateStatus.Downloading

    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text("内容版本") },
        text = {
            Column {
                Text(
                    text = "当前版本：" +
                        (contentState.activeVersion?.let { "v$it" } ?: "未激活"),
                    fontSize = 14.sp
                )
                Spacer(Modifier.height(6.dp))
                Text(
                    text = contentState.status.toDisplayText(),
                    fontSize = 13.sp,
                    color = Color.White.copy(alpha = 0.78f)
                )
            }
        },
        confirmButton = {
            TextButton(onClick = onCheck, enabled = !busy) {
                Text(if (busy) "检查中…" else "检查更新")
            }
        },
        dismissButton = {
            TextButton(onClick = onDismiss) {
                Text("关闭")
            }
        }
    )
}

@Composable
private fun ControlButton(
    label: String,
    modifier: Modifier = Modifier,
    enabled: Boolean = true,
    compact: Boolean = false,
    onClick: () -> Unit
) {
    val interactionSource = remember { MutableInteractionSource() }
    val buttonHeight = if (compact) 40.dp else 54.dp
    val buttonFontSize = if (compact) 13.sp else 18.sp
    val backgroundAlpha = if (enabled) 0.14f else 0.06f
    val textColor = if (enabled) {
        Color.White
    } else {
        Color.White.copy(alpha = 0.42f)
    }

    Box(
        modifier = modifier
            .height(buttonHeight)
            .clip(RoundedCornerShape(10.dp))
            .background(Color.White.copy(alpha = backgroundAlpha))
            .clickable(
                interactionSource = interactionSource,
                indication = null,
                enabled = enabled,
                onClick = onClick
            ),
        contentAlignment = Alignment.Center
    ) {
        Text(
            text = label,
            color = textColor,
            fontSize = buttonFontSize,
            fontWeight = FontWeight.SemiBold,
            maxLines = 1,
            overflow = TextOverflow.Ellipsis
        )
    }
}

private fun sendTimelineSeek(
    target: TimelineTarget,
    currentCueIndex: Int,
    send: (method: String, value: String) -> Unit
) {
    val cue = target.targetCue
    val local = target.localSeconds.coerceIn(0f, cue.duration)
    if (currentCueIndex == cue.index) {
        send("SeekTo", formatPayload(local))
    } else {
        send("PlayCueAt", "${cue.id}|${formatPayload(local)}")
    }
}

private fun formatPayload(value: Float): String =
    String.format(Locale.US, "%.3f", value)

private fun formatTime(seconds: Float): String {
    val totalSeconds = seconds.coerceAtLeast(0f).toInt()
    val hours = totalSeconds / 3600
    val minutes = (totalSeconds % 3600) / 60
    val secs = totalSeconds % 60
    return if (hours > 0) {
        String.format(Locale.US, "%d:%02d:%02d", hours, minutes, secs)
    } else {
        String.format(Locale.US, "%02d:%02d", minutes, secs)
    }
}

private fun scrubGlobalOr(
    scrubbing: Boolean,
    scrubGlobal: Float,
    displayGlobal: Float
): Float = if (scrubbing) scrubGlobal else displayGlobal

private fun buildStateLabel(status: UnityStatus?, timedOut: Boolean): String = when {
    timedOut -> "等待 Unity…"
    status?.isPaused == true -> "已暂停"
    status?.isPlaying == true -> "播放中"
    else -> "加载中 / 停止"
}

private fun buildCueLabel(status: UnityStatus?): String? {
    if (status == null || !status.unityReady || status.cueId.isBlank()) return null
    val current = if (status.cueIndex >= 0) status.cueIndex + 1 else "?"
    val total = if (status.cueTotal > 0) status.cueTotal.toString() else "?"
    return "cue $current/$total · ${status.cueId}"
}

private fun pathKey(path: List<String>): String =
    path.joinToString(ChapterNode.CHAPTER_KEY_SEPARATOR)
