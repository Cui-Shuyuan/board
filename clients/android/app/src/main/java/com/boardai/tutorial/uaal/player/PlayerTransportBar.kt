package com.boardai.tutorial.uaal.player

import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.remember
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.boardai.tutorial.uaal.UnityStatus
import com.boardai.tutorial.uaal.content.ContentUpdateUiState
import com.boardai.tutorial.uaal.timeline.TimelineTarget
import com.boardai.tutorial.uaal.timeline.TutorialTimeline

@Composable
internal fun PlayerCenterPlayPause(
    paused: Boolean,
    enabled: Boolean,
    modifier: Modifier = Modifier,
    onClick: () -> Unit
) {
    val interactionSource = remember { MutableInteractionSource() }
    Box(
        modifier = modifier
            .size(52.dp)
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
            fontSize = 22.sp,
            fontWeight = FontWeight.Bold
        )
    }
}

@Composable
internal fun PlayerTransportBar(
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
    onVolumeChangeFinished: () -> Unit,
    onOpenChapters: () -> Unit,
    onSeekRelative: (Float) -> Unit,
    onPrevious: () -> Unit,
    onNext: () -> Unit,
    onTogglePlayPause: () -> Unit,
    onScrubStart: (Float) -> Unit,
    onScrubChange: (Float) -> Unit,
    onScrubFinished: () -> Unit,
    modifier: Modifier = Modifier
) {
    Column(
        modifier = modifier
            .fillMaxWidth()
            .padding(horizontal = 12.dp, vertical = 10.dp)
    ) {
        val cueText = status?.cueText.orEmpty()
        if (!scrubbing && cueText.isNotBlank()) {
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

        // Keep the chapter overview permanently available while the control
        // layer is visible.  During scrubbing only the target segment highlight
        // changes; the segment list itself is stable.
        PlayerChapterStrip(
            timeline = timeline,
            target = if (scrubbing) scrubTarget else null,
            modifier = Modifier.padding(bottom = 4.dp, start = 12.dp, end = 12.dp)
        )

        // Visual mask starts at the progress row and covers everything below it;
        // the cue/subtitle and chapter overview area above stays unobscured.
        Column(
            modifier = Modifier
                .fillMaxWidth()
                .background(
                    Brush.verticalGradient(
                        listOf(
                            Color.Black.copy(alpha = 0.34f),
                            Color.Black.copy(alpha = 0.94f)
                        )
                    )
                )
        ) {
            PlayerSeekBar(
                scrubbing = scrubbing,
                scrubGlobal = scrubGlobal,
                shownGlobal = shownGlobal,
                progressTotal = progressTotal,
                enabled = status?.unityReady == true,
                onScrubStart = onScrubStart,
                onScrubChange = onScrubChange,
                onScrubFinished = onScrubFinished
            )

            Spacer(Modifier.height(4.dp))

            Row(
                modifier = Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.spacedBy(6.dp, Alignment.Start),
                verticalAlignment = Alignment.CenterVertically
            ) {
                PlayerControlButton(
                    label = if (paused) "▶" else "❚❚",
                    compact = true,
                    modifier = Modifier.width(52.dp),
                    onClick = onTogglePlayPause
                )
                PlayerControlButton(
                    label = "⏮",
                    compact = true,
                    modifier = Modifier.width(52.dp),
                    onClick = onPrevious
                )
                PlayerControlButton(
                    label = "-15",
                    compact = true,
                    modifier = Modifier.width(52.dp),
                    onClick = { onSeekRelative(-15f) }
                )
                PlayerControlButton(
                    label = "+15",
                    compact = true,
                    modifier = Modifier.width(52.dp),
                    onClick = { onSeekRelative(15f) }
                )
                PlayerControlButton(
                    label = "⏭",
                    compact = true,
                    modifier = Modifier.width(52.dp),
                    onClick = onNext
                )
                Spacer(Modifier.weight(1f))
                Text(
                    text = "${formatTime(shownGlobal)} / ${formatTime(totalDuration)}",
                    color = Color.White.copy(alpha = 0.86f),
                    fontSize = 12.sp,
                    maxLines = 1,
                    textAlign = TextAlign.End
                )
            }

            Spacer(Modifier.height(6.dp))

            Row(
                modifier = Modifier.fillMaxWidth(),
                verticalAlignment = Alignment.CenterVertically
            ) {
                Text(
                    text = "音量",
                    color = Color.White.copy(alpha = 0.86f),
                    fontSize = 12.sp,
                    maxLines = 1
                )
                androidx.compose.material3.Slider(
                    value = volume.coerceIn(0f, 1f),
                    valueRange = 0f..1f,
                    onValueChange = onVolumeChange,
                    onValueChangeFinished = onVolumeChangeFinished,
                    modifier = Modifier
                        .width(150.dp)
                        .padding(horizontal = 6.dp)
                )
                Text(
                    text = "${(volume * 100f).toInt()}%",
                    color = Color.White.copy(alpha = 0.86f),
                    fontSize = 12.sp,
                    maxLines = 1,
                    textAlign = TextAlign.End,
                    modifier = Modifier.width(40.dp)
                )
                Spacer(Modifier.weight(1f))
                Text(
                    text = contentState.activeVersion?.let { "v$it" } ?: "内容未激活",
                    color = Color.White.copy(alpha = 0.62f),
                    fontSize = 11.sp,
                    maxLines = 1
                )
                Spacer(Modifier.width(8.dp))
                PlayerControlButton(
                    label = "章节",
                    compact = true,
                    modifier = Modifier.width(64.dp),
                    onClick = onOpenChapters
                )
            }
        }
    }
}
