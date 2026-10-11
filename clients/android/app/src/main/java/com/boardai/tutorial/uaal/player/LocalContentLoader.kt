package com.boardai.tutorial.uaal.player

import com.boardai.tutorial.uaal.catalog.GameCatalogEntry
import com.boardai.tutorial.uaal.timeline.TutorialTimeline
import java.io.File

fun interface LocalContentLoader {
    fun load(game: GameCatalogEntry): LoadedLocalContent?

    /**
     * Track-aware load.  The default keeps old test fakes and legacy callers
     * working; production loaders override this with the selected track.
     */
    fun load(game: GameCatalogEntry, trackId: String): LoadedLocalContent? =
        load(game)
}

data class LoadedLocalContent(
    val version: String,
    val versionRoot: File,
    val timeline: TutorialTimeline,
    val track: String = ""
)
