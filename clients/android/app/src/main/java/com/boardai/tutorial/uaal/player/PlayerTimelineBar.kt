package com.boardai.tutorial.uaal.player

import androidx.compose.foundation.Canvas
import androidx.compose.foundation.gestures.awaitEachGesture
import androidx.compose.foundation.gestures.awaitFirstDown
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberUpdatedState
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.Shadow
import androidx.compose.ui.graphics.StrokeCap
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.boardai.tutorial.uaal.UnityStatus
import com.boardai.tutorial.uaal.timeline.TimelineCue
import com.boardai.tutorial.uaal.timeline.TimelineTarget
import com.boardai.tutorial.uaal.timeline.TutorialTimeline

internal data class TimelineSegment(
    val key: String,
    val title: String,
    val start: Float,
    val end: Float,
    val duration: Float
)

internal fun buildTimelineSegments(
    cues: List<TimelineCue>,
    totalDuration: Float
): List<TimelineSegment> {
    val firstStarts = LinkedHashMap<String, Pair<List<String>, Float>>()
    cues.sortedWith(compareBy<TimelineCue> { it.start }.thenBy { it.index }).forEach { cue ->
        val key = chapterPathKey(cue.groupPath)
        if (key.isNotEmpty() && !firstStarts.containsKey(key)) {
            firstStarts[key] = cue.groupPath to cue.start
        }
    }

    val entries = firstStarts.entries.toList()
    return entries.mapIndexed { index, entry ->
        val start = entry.value.second
        val end = entries.getOrNull(index + 1)?.value?.second ?: totalDuration
        TimelineSegment(
            key = entry.key,
            title = entry.value.first.lastOrNull()?.takeIf { it.isNotBlank() } ?: entry.key,
            start = start,
            end = end,
            duration = (end - start).coerceAtLeast(0.1f)
        )
    }
}

@Composable
internal fun PlayerTimelineBar(
    timeline: TutorialTimeline?,
    status: UnityStatus?,
    scrubbing: Boolean,
    scrubGlobal: Float,
    scrubTarget: TimelineTarget?,
    shownGlobal: Float,
    totalDuration: Float,
    enabled: Boolean,
    onScrubStart: (Float) -> Unit,
    onScrubChange: (Float) -> Unit,
    onScrubFinished: () -> Unit,
    modifier: Modifier = Modifier
) {
    if (timeline == null || timeline.cueCount == 0) return

    val segments = remember(timeline, totalDuration) {
        buildTimelineSegments(timeline.cues, totalDuration)
    }
    if (segments.isEmpty()) return

    val currentKey = when {
        scrubbing -> scrubTarget?.targetCue
        else -> timeline.cueAt(status?.cueIndex ?: -1)
    }?.groupPath?.let(::chapterPathKey)

    val progress = (if (scrubbing) scrubGlobal else shownGlobal)
        .takeIf { it.isFinite() }
        ?.coerceIn(0f, totalDuration.coerceAtLeast(0f))
        ?: 0f
    val canInteract = enabled && totalDuration > 0f

    val currentOnScrubStart by rememberUpdatedState(onScrubStart)
    val currentOnScrubChange by rememberUpdatedState(onScrubChange)
    val currentOnScrubFinished by rememberUpdatedState(onScrubFinished)

    Box(
        modifier = modifier
            .fillMaxWidth()
            .height(30.dp)
            .clip(RoundedCornerShape(6.dp))
            .then(
                if (canInteract) {
                    Modifier.pointerInput(canInteract, totalDuration) {
                        awaitEachGesture {
                            val down = awaitFirstDown(requireUnconsumed = false)
                            val width = size.width.toFloat().coerceAtLeast(1f)

                            fun globalFor(x: Float): Float =
                                (x / width * totalDuration).coerceIn(0f, totalDuration)

                            currentOnScrubStart(globalFor(down.position.x))
                            currentOnScrubChange(globalFor(down.position.x))
                            down.consume()

                            var finished = false
                            try {
                                while (true) {
                                    val event = awaitPointerEvent()
                                    val change = event.changes.firstOrNull { it.id == down.id }
                                    if (change == null) {
                                        currentOnScrubFinished()
                                        finished = true
                                        break
                                    }
                                    if (change.pressed) {
                                        currentOnScrubChange(globalFor(change.position.x))
                                        change.consume()
                                    } else {
                                        currentOnScrubChange(globalFor(change.position.x))
                                        currentOnScrubFinished()
                                        change.consume()
                                        finished = true
                                        break
                                    }
                                }
                            } finally {
                                if (!finished) currentOnScrubFinished()
                            }
                        }
                    }
                } else {
                    Modifier
                }
            )
    ) {
        Canvas(Modifier.matchParentSize()) {
            val weightTotal = segments
                .sumOf { it.duration.toDouble() }
                .toFloat()
                .coerceAtLeast(0.001f)
            val progressFraction = if (totalDuration > 0f) {
                progress / totalDuration
            } else {
                0f
            }

            val edges = FloatArray(segments.size + 1)
            var cumulativeWidth = 0f
            segments.forEachIndexed { index, segment ->
                edges[index] = cumulativeWidth
                cumulativeWidth += size.width * (segment.duration / weightTotal)
            }
            edges[segments.size] = size.width

            drawRect(color = Color.Black.copy(alpha = 0.38f))
            segments.forEachIndexed { index, _ ->
                drawRect(
                    color = Color.White.copy(alpha = if (index % 2 == 0) 0.10f else 0.055f),
                    topLeft = Offset(edges[index], 0f),
                    size = Size(
                        (edges[index + 1] - edges[index]).coerceAtLeast(0f),
                        size.height
                    )
                )
            }

            if (currentKey != null) {
                segments.forEachIndexed { index, segment ->
                    if (segment.key != currentKey) return@forEachIndexed
                    drawRect(
                        color = Color(0xFFFFCC80).copy(alpha = 0.18f),
                        topLeft = Offset(edges[index], 0f),
                        size = Size(
                            (edges[index + 1] - edges[index]).coerceAtLeast(0f),
                            size.height
                        )
                    )
                }
            }

            if (progressFraction > 0f) {
                drawRect(
                    color = Color.White.copy(alpha = 0.22f),
                    topLeft = Offset.Zero,
                    size = Size(size.width * progressFraction.coerceIn(0f, 1f), size.height)
                )
            }

            for (index in 0 until segments.lastIndex) {
                val x = edges[index + 1]
                drawLine(
                    color = Color.White.copy(alpha = 0.24f),
                    start = Offset(x, 0f),
                    end = Offset(x, size.height),
                    strokeWidth = 0.6.dp.toPx()
                )
            }

            if (totalDuration > 0f) {
                val x = size.width * progressFraction.coerceIn(0f, 1f)
                val playheadTop = size.height - 3.dp.toPx()
                drawLine(
                    color = Color.White.copy(alpha = 0.96f),
                    start = Offset(x, playheadTop),
                    end = Offset(x, size.height),
                    strokeWidth = 2.5.dp.toPx(),
                    cap = StrokeCap.Round
                )
            }
        }

        Row(Modifier.matchParentSize()) {
            segments.forEach { segment ->
                Box(
                    modifier = Modifier
                        .weight(segment.duration.coerceAtLeast(0.1f))
                        .fillMaxHeight()
                        .padding(horizontal = 1.dp),
                    contentAlignment = Alignment.Center
                ) {
                    Text(
                        text = segment.title,
                        color = Color.White.copy(alpha = 0.96f),
                        fontSize = 9.sp,
                        fontWeight = FontWeight.Medium,
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis,
                        textAlign = TextAlign.Center,
                        style = TextStyle(
                            shadow = Shadow(
                                color = Color.Black.copy(alpha = 0.92f),
                                blurRadius = 2.5f
                            )
                        ),
                        modifier = Modifier.padding(horizontal = 2.dp)
                    )
                }
            }
        }
    }
}
