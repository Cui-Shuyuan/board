package com.boardai.tutorial.uaal.history

import com.boardai.tutorial.uaal.catalog.GameCatalogEntry

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
