package com.boardai.tutorial.uaal.player

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.remember
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.boardai.tutorial.uaal.timeline.ChapterNode
import com.boardai.tutorial.uaal.timeline.TimelineTarget
import com.boardai.tutorial.uaal.timeline.TutorialTimeline

private data class ChapterSegment(
    val key: String,
    val title: String,
    val durationSeconds: Float
)

@Composable
internal fun PlayerChapterStrip(
    timeline: TutorialTimeline?,
    target: TimelineTarget?,
    modifier: Modifier = Modifier
) {
    if (timeline == null || timeline.cueCount == 0) return

    val segments = remember(timeline) { buildChapterSegments(timeline) }
    if (segments.isEmpty()) return

    val targetKey = target?.targetCue?.groupPath?.let { chapterPathKey(it) }
    Row(
        modifier = modifier
            .fillMaxWidth()
            .height(20.dp)
            .clip(RoundedCornerShape(3.dp))
            .background(Color.Black.copy(alpha = 0.22f))
    ) {
        segments.forEachIndexed { index, segment ->
            val selected = targetKey != null && segment.key == targetKey
            Box(
                modifier = Modifier
                    .weight(segment.durationSeconds)
                    .fillMaxHeight()
                    .background(
                        when {
                            selected -> Color(0xFFFFCC80).copy(alpha = 0.58f)
                            index % 2 == 0 -> Color.White.copy(alpha = 0.12f)
                            else -> Color.White.copy(alpha = 0.06f)
                        }
                    )
                    .border(
                        width = 0.5.dp,
                        color = Color.White.copy(alpha = 0.22f)
                    ),
                contentAlignment = Alignment.Center
            ) {
                Text(
                    text = segment.title,
                    color = if (selected) {
                        Color.Black.copy(alpha = 0.86f)
                    } else {
                        Color.White.copy(alpha = 0.78f)
                    },
                    fontSize = 8.sp,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                    textAlign = TextAlign.Center,
                    modifier = Modifier.padding(horizontal = 1.dp)
                )
            }
        }
    }
}

private fun buildChapterSegments(timeline: TutorialTimeline): List<ChapterSegment> {
    val firstStarts = LinkedHashMap<String, Pair<List<String>, Float>>()
    timeline.cues.sortedBy { it.start }.forEach { cue ->
        val key = chapterPathKey(cue.groupPath)
        if (key.isNotEmpty() && !firstStarts.containsKey(key)) {
            firstStarts[key] = cue.groupPath to cue.start
        }
    }

    val entries = firstStarts.entries.toList()
    return entries.mapIndexed { index, entry ->
        val start = entry.value.second
        val end = entries.getOrNull(index + 1)?.value?.second ?: timeline.totalDuration
        ChapterSegment(
            key = entry.key,
            title = entry.value.first.lastOrNull()?.takeIf { it.isNotBlank() } ?: entry.key,
            durationSeconds = (end - start).coerceAtLeast(0.1f)
        )
    }
}

internal fun chapterPathKey(path: List<String>): String =
    path.joinToString(ChapterNode.CHAPTER_KEY_SEPARATOR)
