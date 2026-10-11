package com.boardai.tutorial.uaal.player

import com.boardai.tutorial.uaal.catalog.GameCatalogEntry
import com.boardai.tutorial.uaal.content.ContentStore
import com.boardai.tutorial.uaal.timeline.TutorialTimeline
import java.io.File

class DefaultLocalContentLoader(
    private val contentStore: ContentStore
) : LocalContentLoader {
    override fun load(game: GameCatalogEntry): LoadedLocalContent? =
        load(game, game.tutorialTrack)

    override fun load(game: GameCatalogEntry, trackId: String): LoadedLocalContent? {
        val track = trackId.trim().ifBlank { game.tutorialTrack.trim() }
        if (!game.tutorialReady || track.isBlank()) return null

        val active = contentStore.readActiveValid(game.id) ?: return null
        val gameRoot = contentStore.gameRoot(active.version, game.id)
        if (!gameRoot.isDirectory) return null

        val timeline = loadTimeline(gameRoot, track) ?: return null
        return LoadedLocalContent(
            version = active.version,
            versionRoot = contentStore.versionRoot(active.version, game.id),
            timeline = timeline,
            track = track
        )
    }

    private fun loadTimeline(gameRoot: File, track: String): TutorialTimeline? {
        if (!gameRoot.isDirectory) return null

        return try {
            TutorialTimeline.load(gameRoot, track)
        } catch (_: Throwable) {
            null
        }
    }
}
