package com.boardai.tutorial.uaal.qa

import com.boardai.tutorial.uaal.UnityStatus
import com.boardai.tutorial.uaal.catalog.GameCatalogEntry
import com.boardai.tutorial.uaal.timeline.TimelineCue
import com.boardai.tutorial.uaal.timeline.TutorialTimeline

/**
 * Latest playback position plus tutorial metadata attached to every QA request.
 *
 * The context is intentionally rebuilt from UnityStatus + TutorialTimeline at
 * send time instead of being frozen when the QA panel opens.
 */
data class QaContext(
    val gameId: String,
    val gameName: String,
    val cueId: String,
    val cueIndex: Int,
    val cueText: String,
    val groupPath: List<String>,
    val positionInCue: Float,
    val recentCues: List<RecentCueContext> = emptyList()
) {
    val sectionPath: List<String> get() = groupPath
}

/**
 * One entry in the rolling recent-cue window sent to /api/chat.
 *
 * [isCurrent] is true only for the cue that was active when the question was
 * sent.  The list is rebuilt from UnityStatus + TutorialTimeline at send time,
 * so a late question can still see the previous one or two cues.
 */
data class RecentCueContext(
    val id: String,
    val index: Int,
    val text: String,
    val groupPath: List<String>,
    val refs: List<String>,
    val actions: List<String>,
    val start: Float,
    val duration: Float,
    val isCurrent: Boolean
)

/**
 * Builds a [QaContext] from the currently selected catalog game and the latest
 * Unity status.  The timeline cue is preferred over status strings when it can
 * be resolved; otherwise we fall back to the cue fields already in UnityStatus.
 */
fun buildQaContext(
    game: GameCatalogEntry?,
    status: UnityStatus?,
    timeline: TutorialTimeline?
): QaContext {
    val timelineCue = resolveTimelineCue(status, timeline)
    val statusPosition = status?.position?.takeIf { it.isFinite() && it >= 0f } ?: 0f
    val gameName = game?.nameZh?.trim().orEmpty().ifBlank {
        game?.nameEn?.trim().orEmpty()
    }

    return QaContext(
        gameId = game?.id.orEmpty(),
        gameName = gameName,
        cueId = timelineCue?.id?.takeIf { it.isNotBlank() } ?: status?.cueId.orEmpty(),
        cueIndex = timelineCue?.index ?: status?.cueIndex ?: -1,
        cueText = timelineCue?.text?.takeIf { it.isNotBlank() } ?: status?.cueText.orEmpty(),
        groupPath = timelineCue?.groupPath.orEmpty(),
        positionInCue = statusPosition,
        recentCues = buildRecentCues(timeline, timelineCue)
    )
}

private fun buildRecentCues(
    timeline: TutorialTimeline?,
    currentCue: TimelineCue?
): List<RecentCueContext> {
    if (timeline == null || currentCue == null) return emptyList()

    val currentIndex = currentCue.index.coerceIn(0, timeline.cues.lastIndex)
    val firstIndex = (currentIndex - 2).coerceAtLeast(0)

    return timeline.cues.subList(firstIndex, currentIndex + 1).map { cue ->
        RecentCueContext(
            id = cue.id,
            index = cue.index,
            text = cue.text,
            groupPath = cue.groupPath,
            refs = cue.refs,
            actions = cue.actions,
            start = cue.start,
            duration = cue.duration,
            isCurrent = cue.index == currentIndex
        )
    }
}

private fun resolveTimelineCue(
    status: UnityStatus?,
    timeline: TutorialTimeline?
): TimelineCue? {
    val current = status ?: return null
    val byIndex = timeline?.cueAt(current.cueIndex)
    if (byIndex != null) return byIndex
    return timeline?.cueById(current.cueId)
}
