package com.boardai.tutorial.uaal.home

import com.boardai.tutorial.uaal.catalog.GameCatalogEntry
import com.boardai.tutorial.uaal.content.ContentStatus
import com.boardai.tutorial.uaal.content.DownloadOverlayState
import com.boardai.tutorial.uaal.content.GamePromptState
import com.boardai.tutorial.uaal.history.RecentGame

/**
 * UI-facing home/catalog/download state owned by [HomeContentCoordinator].
 *
 * The coordinator keeps the full catalog and history internally; only the
 * filtered/sorted catalog and recent rows are exposed here.
 */
data class HomeContentState(
    val visibleGames: List<GameCatalogEntry> = emptyList(),
    val recentGames: List<RecentGame> = emptyList(),
    val contentStatuses: Map<String, ContentStatus> = emptyMap(),
    val isLoading: Boolean = false,
    val errorMessage: String? = null,
    val gamePrompt: GamePromptState? = null,
    val activeDownload: DownloadOverlayState? = null,
    val resourceManagerOpen: Boolean = false,
    val resourceChecking: Boolean = false,
    val preparingGameId: String? = null,
    val downloadBusy: Boolean = false
)
