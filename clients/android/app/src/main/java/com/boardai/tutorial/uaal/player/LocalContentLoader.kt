package com.boardai.tutorial.uaal.player

import com.boardai.tutorial.uaal.catalog.GameCatalogEntry
import com.boardai.tutorial.uaal.timeline.TutorialTimeline
import java.io.File

fun interface LocalContentLoader {
    fun load(game: GameCatalogEntry): LoadedLocalContent?
}

data class LoadedLocalContent(
    val version: String,
    val versionRoot: File,
    val timeline: TutorialTimeline
)
