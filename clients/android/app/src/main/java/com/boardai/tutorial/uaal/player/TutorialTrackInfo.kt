package com.boardai.tutorial.uaal.player

/**
 * Display metadata for platform-level tutorial tracks.
 *
 * Track ids stay data-driven in the catalog; the app owns the generic
 * quick/full presentation so a game does not have to repeat the same labels.
 */
data class TutorialTrackInfo(
    val id: String,
    val title: String,
    val subtitle: String,
    val recommended: Boolean
)

fun tutorialTrackInfo(trackId: String): TutorialTrackInfo = when (trackId) {
    "quick" -> TutorialTrackInfo(
        id = "quick",
        title = "快速开始",
        subtitle = "约 3 分钟 · 先学会开始玩",
        recommended = true
    )
    "full" -> TutorialTrackInfo(
        id = "full",
        title = "完整讲规",
        subtitle = "约 9 分钟 · 完整规则和细节",
        recommended = false
    )
    else -> TutorialTrackInfo(
        id = trackId,
        title = trackId.ifBlank { "教程" },
        subtitle = "教程",
        recommended = false
    )
}
