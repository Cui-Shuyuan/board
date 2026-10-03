package com.boardai.tutorial.uaal.player

import com.boardai.tutorial.uaal.catalog.GameCatalogEntry
import com.boardai.tutorial.uaal.timeline.TutorialTimeline

/**
 * UI-facing state for the local playback session.
 */
data class PlayerSessionState(
    val selectedGame: GameCatalogEntry? = null,
    val playerActive: Boolean = false,
    val timeline: TutorialTimeline? = null,
    val activeVersion: String? = null,
    val playbackError: String? = null
)
