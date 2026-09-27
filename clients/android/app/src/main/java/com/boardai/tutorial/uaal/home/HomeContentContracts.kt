package com.boardai.tutorial.uaal.home

import com.boardai.tutorial.uaal.catalog.GameCatalogEntry
import com.boardai.tutorial.uaal.content.ActiveContent
import com.boardai.tutorial.uaal.content.ContentUpdateResult
import com.boardai.tutorial.uaal.content.ContentUpdateStatus
import com.boardai.tutorial.uaal.content.DownloadControl
import com.boardai.tutorial.uaal.content.PausedContent
import com.boardai.tutorial.uaal.history.PlayHistoryEntry
import com.boardai.tutorial.uaal.history.RecentGame
import java.io.File

interface HomeCatalogSource {
    fun readCache(): List<GameCatalogEntry>
    fun fetchCatalog(): List<GameCatalogEntry>
    fun visibleGames(allGames: List<GameCatalogEntry>): List<GameCatalogEntry>
}

interface HomeHistorySource {
    fun read(): List<PlayHistoryEntry>

    fun recordPlay(
        gameId: String,
        visibleGameIds: Set<String>
    ): List<PlayHistoryEntry>

    fun recentGames(
        visibleGames: List<GameCatalogEntry>,
        history: List<PlayHistoryEntry>
    ): List<RecentGame>
}

interface ContentStatusSource {
    fun readActiveValid(gameId: String): ActiveContent?
    fun readPaused(gameId: String): PausedContent?
    fun isVersionComplete(version: String, gameId: String? = null): Boolean
    fun gameRoot(version: String, gameId: String): File
}

interface HomeContentStore : ContentStatusSource {
    fun deletePaused(gameId: String)
    fun deleteLocalContent(gameId: String)
}

interface ContentUpdateExecutor {
    fun update(
        game: String,
        onStatus: (ContentUpdateStatus) -> Unit,
        control: DownloadControl = DownloadControl()
    ): ContentUpdateResult
}

interface HomeContentScheduler {
    fun postToMain(block: () -> Unit)
    fun runCatalogTask(block: () -> Unit)
    fun runContentTask(block: () -> Unit)
    fun dispose()
}
