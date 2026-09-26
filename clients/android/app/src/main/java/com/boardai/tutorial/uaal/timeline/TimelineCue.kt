package com.boardai.tutorial.uaal.timeline


/**
 * A key/value dimension inside a logical zone, for example
 * `color = <diamond>` on a physical gem supply pile.
 */
data class PartRef(
    val key: String,
    val value: String
)

/**
 * Physical zone -> logical zone mapping read from the compiled track's
 * top-level `zone_bindings` object.
 *
 * [label] is preferred for QA text.  If it is blank, callers fall back to
 * [logicalZone], then to the raw physical zone id.  [qaIgnore] marks an
 * animation-only physical space that must not appear in QA action summaries.
 */
data class ZoneBinding(
    val logicalZone: String,
    val label: String,
    val parts: List<PartRef> = emptyList(),
    val qaIgnore: Boolean = false
)

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
    val groupPath: List<String>,
    val refs: List<String> = emptyList(),
    val actions: List<String> = emptyList()
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
