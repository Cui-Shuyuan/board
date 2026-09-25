package com.boardai.tutorial.uaal.timeline

/**
 * One cue on the tutorial's continuous global audio timeline.
 *
 * [start] is the cue's global start time in seconds.  [duration] is the audio
 * clip duration in seconds.  The same model is used by the native seek bar and
 * chapter menu; it does not own or mutate any Unity state.
 */
data class TimelineCue(
    val id: String,
    val index: Int,
    val start: Float,
    val duration: Float,
    val text: String,
    val groupPath: List<String>
) {
    val endExclusive: Float get() = start + duration
}

/**
 * Result of mapping a global timeline position back to a cue-local position.
 *
 * [snappedToCueStart] is true when the caller's magnetic rule pulled the
 * position to a cue start.  [snappedToChapterStart] is true when the stronger
 * chapter-start magnet was applied.
 */
data class TimelineTarget(
    val targetCue: TimelineCue,
    val localSeconds: Float,
    val snappedToCueStart: Boolean,
    val snappedToChapterStart: Boolean = false,
    val snappedChapter: ChapterNode? = null
)
