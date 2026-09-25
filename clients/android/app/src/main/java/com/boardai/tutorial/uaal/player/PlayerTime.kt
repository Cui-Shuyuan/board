package com.boardai.tutorial.uaal.player

import com.boardai.tutorial.uaal.timeline.TimelineTarget
import java.util.Locale

internal fun formatPayload(value: Float): String =
    String.format(Locale.US, "%.3f", value)

internal fun formatTime(seconds: Float): String {
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

internal fun sendTimelineSeek(
    target: TimelineTarget,
    currentCueIndex: Int,
    send: (method: String, value: String) -> Unit,
    startPaused: Boolean
) {
    val cue = target.targetCue
    val local = target.localSeconds.coerceIn(0f, cue.duration)
    if (currentCueIndex == cue.index) {
        send("SeekTo", formatPayload(local))
    } else {
        val pausedFlag = if (startPaused) "1" else "0"
        send("PlayCueAt", "${cue.id}|${formatPayload(local)}|$pausedFlag")
    }
}
