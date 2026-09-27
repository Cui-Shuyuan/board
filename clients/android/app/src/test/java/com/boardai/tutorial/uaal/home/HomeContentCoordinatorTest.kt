package com.boardai.tutorial.uaal.home

import com.boardai.tutorial.uaal.catalog.GameCatalogEntry
import com.boardai.tutorial.uaal.catalog.HomeCatalogSource
import com.boardai.tutorial.uaal.content.ContentUpdateExecutor
import com.boardai.tutorial.uaal.content.HomeContentStore
import com.boardai.tutorial.uaal.content.ActiveContent
import com.boardai.tutorial.uaal.content.ContentStatus
import com.boardai.tutorial.uaal.content.ContentUpdateResult
import com.boardai.tutorial.uaal.content.ContentUpdateStatus
import com.boardai.tutorial.uaal.content.DownloadControl
import com.boardai.tutorial.uaal.content.GamePromptAction
import com.boardai.tutorial.uaal.content.PausedContent
import com.boardai.tutorial.uaal.history.HomeHistorySource
import com.boardai.tutorial.uaal.history.PlayHistoryEntry
import com.boardai.tutorial.uaal.history.RecentGame
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.File

class HomeContentCoordinatorTest {

    @Test
    fun startReadsCacheAndHistoryBeforeRefreshingCatalog() {
        val events = mutableListOf<String>()
        val cached = game("cached")
        val catalog = FakeHomeCatalogSource(
            cache = listOf(cached),
            fetched = listOf(cached),
            events = events
        )
        val history = FakeHomeHistorySource(events = events)
        val coordinator = coordinator(catalog = catalog, history = history)

        coordinator.start()

        assertEquals(
            listOf("catalog.readCache", "history.read", "catalog.fetch"),
            events
        )
    }

    @Test
    fun onGameClickNoServerResourceShowsPromptAndDoesNotEnterGame() {
        val game = game("offline", contentVersion = null)
        val store = FakeHomeContentStore()
        val entered = mutableListOf<GameCatalogEntry>()
        val coordinator = coordinator(
            contentStore = store,
            onEnterGame = { entered += it }
        )

        coordinator.onGameClick(game)

        assertEquals(game, coordinator.state.value.gamePrompt?.game)
        assertEquals(ContentStatus.NoServerResource, coordinator.state.value.gamePrompt?.status)
        assertTrue(entered.isEmpty())
    }

    @Test
    fun onGameClickInstalledCurrentEntersGameDirectly() {
        val game = game("installed", contentVersion = "v1")
        val store = FakeHomeContentStore(
            active = active(game.id, "v1")
        )
        val entered = mutableListOf<GameCatalogEntry>()
        val coordinator = coordinator(
            contentStore = store,
            onEnterGame = { entered += it }
        )

        coordinator.onGameClick(game)

        assertEquals(listOf(game), entered)
        assertNull(coordinator.state.value.gamePrompt)
    }

    @Test
    fun clickingAnotherGameWhileDownloadingOnlyShowsToast() {
        val first = game("first", contentVersion = "v1")
        val second = game("second", contentVersion = "v1")
        val scheduler = QueuedHomeContentScheduler()
        val updater = FakeContentUpdateExecutor()
        val toasts = mutableListOf<String>()
        val coordinator = coordinator(
            scheduler = scheduler,
            updater = updater,
            onToast = { toasts += it }
        )

        coordinator.startDownload(first)
        coordinator.onGameClick(second)

        assertTrue(toasts.single().contains(first.nameZh))
        assertTrue(coordinator.hasActiveDownload())
        assertEquals(first.id, coordinator.state.value.activeDownload?.game?.id)

        scheduler.drainContent()
        assertEquals(listOf(first.id), updater.calls)
    }

    @Test
    fun promptCancelClearsPrompt() {
        val game = game("cancel", contentVersion = "v1")
        val coordinator = coordinator()
        coordinator.onGameClick(game)
        assertNotNull(coordinator.state.value.gamePrompt)

        coordinator.handlePromptAction(GamePromptAction.CANCEL)

        assertNull(coordinator.state.value.gamePrompt)
    }

    @Test
    fun promptUseCurrentEntersGame() {
        val game = game("use-current", contentVersion = "v1")
        val entered = mutableListOf<GameCatalogEntry>()
        val coordinator = coordinator(onEnterGame = { entered += it })
        coordinator.onGameClick(game)

        coordinator.handlePromptAction(GamePromptAction.USE_CURRENT)

        assertEquals(listOf(game), entered)
        assertNull(coordinator.state.value.gamePrompt)
    }

    @Test
    fun openResourceManagerAutomaticallyFetchesCatalogAndShowsUpdate() {
        val game = game("auto", contentVersion = "v1")
        val store = FakeHomeContentStore(active = active(game.id, "v1"))
        val events = mutableListOf<String>()
        val catalog = FakeHomeCatalogSource(
            cache = listOf(game),
            fetched = listOf(game),
            events = events
        )
        val coordinator = coordinator(catalog = catalog, contentStore = store)

        coordinator.start()
        assertEquals(
            ContentStatus.InstalledCurrent("v1"),
            coordinator.state.value.contentStatuses[game.id]
        )

        catalog.fetched = listOf(game.copy(contentVersion = "v2"))
        coordinator.openResourceManager()

        assertTrue(coordinator.state.value.resourceManagerOpen)
        assertEquals(2, events.count { it == "catalog.fetch" })
        assertEquals(
            ContentStatus.UpdateAvailable(localVersion = "v1", serverVersion = "v2"),
            coordinator.state.value.contentStatuses[game.id]
        )
        assertFalse(coordinator.state.value.resourceChecking)
    }

    @Test
    fun openResourceManagerWhileCatalogRefreshInFlightKeepsCheckingUntilDone() {
        val game = game("inflight", contentVersion = "v1")
        val store = FakeHomeContentStore(active = active(game.id, "v1"))
        val scheduler = QueuedHomeContentScheduler()
        val catalog = FakeHomeCatalogSource(
            cache = listOf(game),
            fetched = listOf(game.copy(contentVersion = "v2"))
        )
        val coordinator = coordinator(
            catalog = catalog,
            contentStore = store,
            scheduler = scheduler
        )

        coordinator.start()
        assertFalse(coordinator.state.value.resourceChecking)

        coordinator.openResourceManager()

        assertTrue(coordinator.state.value.resourceManagerOpen)
        assertTrue(coordinator.state.value.resourceChecking)
        assertEquals(
            ContentStatus.InstalledCurrent("v1"),
            coordinator.state.value.contentStatuses[game.id]
        )

        scheduler.drainCatalog()

        assertFalse(coordinator.state.value.resourceChecking)
        assertEquals(
            ContentStatus.UpdateAvailable(localVersion = "v1", serverVersion = "v2"),
            coordinator.state.value.contentStatuses[game.id]
        )
    }

    @Test
    fun startDownloadSuccessClearsOverlayAndStaysHome() {
        val game = game("success", contentVersion = "v2")
        val store = FakeHomeContentStore()
        val updater = FakeContentUpdateExecutor(
            status = ContentUpdateStatus.Updated("v2"),
            updateBlock = { _ ->
                store.active = active(game.id, "v2")
                ContentUpdateResult(status = ContentUpdateStatus.Updated("v2"))
            }
        )
        val entered = mutableListOf<GameCatalogEntry>()
        val toasts = mutableListOf<String>()
        val coordinator = coordinator(
            catalog = FakeHomeCatalogSource(cache = listOf(game), fetched = listOf(game)),
            contentStore = store,
            updater = updater,
            onEnterGame = { entered += it },
            onToast = { toasts += it }
        )
        coordinator.start()

        coordinator.startDownload(game)

        assertFalse(coordinator.state.value.resourceManagerOpen)
        assertNull(coordinator.state.value.activeDownload)
        assertFalse(coordinator.state.value.downloadBusy)
        assertNull(coordinator.state.value.preparingGameId)
        assertFalse(coordinator.hasActiveDownload())
        assertTrue(entered.isEmpty())
        assertEquals(listOf(game.id), updater.calls)
        assertEquals(ContentStatus.InstalledCurrent("v2"), coordinator.state.value.contentStatuses[game.id])
        assertEquals(listOf("《${game.nameZh}》已就绪，请选择游戏开始"), toasts)
    }

    @Test
    fun startDownloadPausedKeepsPartialAndClearsBusy() {
        val game = game("paused", contentVersion = "v2")
        val paused = paused(game.id, "v2")
        val store = FakeHomeContentStore()
        val updater = FakeContentUpdateExecutor(
            status = ContentUpdateStatus.Paused(
                version = "v2",
                completedFiles = 1,
                totalFiles = 2,
                bytesCompleted = 10L,
                totalBytes = 20L,
                currentPath = "a.bin"
            ),
            updateBlock = {
                store.paused = paused
                ContentUpdateResult(
                    status = ContentUpdateStatus.Paused(
                        version = "v2",
                        completedFiles = 1,
                        totalFiles = 2,
                        bytesCompleted = 10L,
                        totalBytes = 20L,
                        currentPath = "a.bin"
                    )
                )
            }
        )
        val coordinator = coordinator(
            catalog = FakeHomeCatalogSource(cache = listOf(game), fetched = listOf(game)),
            contentStore = store,
            updater = updater
        )
        coordinator.start()

        coordinator.startDownload(game)

        assertEquals(paused, store.paused)
        assertNull(store.active)
        assertFalse(coordinator.state.value.downloadBusy)
        assertNull(coordinator.state.value.activeDownload)
        assertTrue(coordinator.state.value.contentStatuses[game.id] is ContentStatus.Paused)
    }

    @Test
    fun pauseDownloadAndReturnHomeClearsOverlayWhileBackendFinishes() {
        val game = game("pause-in-flight", contentVersion = "v2")
        val store = FakeHomeContentStore()
        val paused = paused(game.id, "v2")
        val pausedStatus = ContentUpdateStatus.Paused(
            version = "v2",
            completedFiles = 0,
            totalFiles = 1,
            bytesCompleted = 0L,
            totalBytes = 10L,
            currentPath = "a.bin"
        )
        val scheduler = QueuedHomeContentScheduler()
        val updater = FakeContentUpdateExecutor(
            updateBlock = {
                store.paused = paused
                ContentUpdateResult(status = pausedStatus)
            }
        )
        val coordinator = coordinator(
            contentStore = store,
            scheduler = scheduler,
            updater = updater
        )

        coordinator.startDownload(game)
        assertTrue(coordinator.hasActiveDownload())
        assertNotNull(coordinator.state.value.activeDownload)

        coordinator.pauseDownloadAndReturnHome()

        assertNull(coordinator.state.value.activeDownload)
        assertNull(coordinator.state.value.preparingGameId)
        assertTrue(coordinator.hasActiveDownload())

        scheduler.drainContent()

        assertFalse(coordinator.hasActiveDownload())
        assertEquals(paused, store.paused)
    }

    @Test
    fun deleteLocalResourcesDeletesAndRefreshesStatus() {
        val game = game("delete", contentVersion = null)
        val store = FakeHomeContentStore(active = active(game.id, "v1"))
        val toasts = mutableListOf<String>()
        val coordinator = coordinator(
            catalog = FakeHomeCatalogSource(cache = listOf(game), fetched = listOf(game)),
            contentStore = store,
            onToast = { toasts += it }
        )
        coordinator.start()

        coordinator.deleteLocalResources(game)

        assertEquals(listOf(game.id), store.deletedLocalIds)
        assertEquals(ContentStatus.NoServerResource, coordinator.state.value.contentStatuses[game.id])
        assertTrue(toasts.single().contains(game.nameZh))
    }

    @Test
    fun recomputeCatalogStateReportsRemovedSelectedGame() {
        val removed = game("removed", contentVersion = "v1")
        val replacement = game("replacement", contentVersion = "v1")
        var unavailableCalls = 0
        val coordinator = coordinator(
            catalog = FakeHomeCatalogSource(cache = listOf(removed), fetched = listOf(replacement)),
            selectedGameIdProvider = { removed.id },
            onSelectedGameUnavailable = { unavailableCalls += 1 }
        )

        coordinator.start()

        assertEquals(1, unavailableCalls)
        assertFalse(coordinator.state.value.visibleGames.any { it.id == removed.id })
        assertTrue(coordinator.state.value.visibleGames.any { it.id == replacement.id })
    }

    private fun coordinator(
        catalog: HomeCatalogSource = FakeHomeCatalogSource(),
        contentStore: HomeContentStore = FakeHomeContentStore(),
        updater: ContentUpdateExecutor = FakeContentUpdateExecutor(),
        history: HomeHistorySource = FakeHomeHistorySource(),
        scheduler: HomeContentScheduler = DirectHomeContentScheduler(),
        onEnterGame: (GameCatalogEntry) -> Unit = {},
        onToast: (String) -> Unit = {},
        selectedGameIdProvider: () -> String? = { null },
        onSelectedGameUnavailable: () -> Unit = {}
    ): HomeContentCoordinator =
        HomeContentCoordinator(
            catalogRepository = catalog,
            contentStore = contentStore,
            contentUpdater = updater,
            historyRepository = history,
            scheduler = scheduler,
            onEnterGame = onEnterGame,
            onToast = onToast,
            log = { _, _ -> },
            selectedGameIdProvider = selectedGameIdProvider,
            onSelectedGameUnavailable = onSelectedGameUnavailable
        )

    private fun game(id: String, contentVersion: String? = "v1"): GameCatalogEntry =
        GameCatalogEntry(
            id = id,
            released = true,
            nameZh = "游戏$id",
            nameEn = "Game $id",
            aliases = emptyList(),
            searchKeys = emptyList(),
            minPlayers = 1,
            maxPlayers = 4,
            tutorialTrack = "full",
            contentVersion = contentVersion
        )

    private fun active(gameId: String, version: String): ActiveContent =
        ActiveContent(game = gameId, version = version, root = File("/fake/$version/$gameId"))

    private fun paused(gameId: String, version: String): PausedContent =
        PausedContent(
            game = gameId,
            version = version,
            completedFiles = 1,
            totalFiles = 2,
            bytesCompleted = 10L,
            totalBytes = 20L,
            updatedAt = 1L
        )

    private class FakeHomeCatalogSource(
        var cache: List<GameCatalogEntry> = emptyList(),
        var fetched: List<GameCatalogEntry> = emptyList(),
        private val events: MutableList<String>? = null
    ) : HomeCatalogSource {
        override fun readCache(): List<GameCatalogEntry> {
            events?.add("catalog.readCache")
            return cache
        }

        override fun fetchCatalog(): List<GameCatalogEntry> {
            events?.add("catalog.fetch")
            return fetched
        }

        override fun visibleGames(allGames: List<GameCatalogEntry>): List<GameCatalogEntry> =
            allGames.filter { it.released }
    }

    private class FakeHomeHistorySource(
        var entries: List<PlayHistoryEntry> = emptyList(),
        private val events: MutableList<String>? = null
    ) : HomeHistorySource {
        override fun read(): List<PlayHistoryEntry> {
            events?.add("history.read")
            return entries
        }

        override fun recordPlay(
            gameId: String,
            visibleGameIds: Set<String>
        ): List<PlayHistoryEntry> {
            entries = entries + PlayHistoryEntry(gameId, 1, 1L)
            return entries
        }

        override fun recentGames(
            visibleGames: List<GameCatalogEntry>,
            history: List<PlayHistoryEntry>
        ): List<RecentGame> =
            history.mapNotNull { entry ->
                visibleGames.firstOrNull { it.id == entry.gameId }?.let { game ->
                    RecentGame(game, entry.playCount, entry.lastPlayedAt)
                }
            }
    }

    private class FakeHomeContentStore(
        var active: ActiveContent? = null,
        var paused: PausedContent? = null
    ) : HomeContentStore {
        val deletedPausedIds = mutableListOf<String>()
        val deletedLocalIds = mutableListOf<String>()

        override fun readActiveValid(gameId: String): ActiveContent? =
            active?.takeIf { it.game == gameId }

        override fun readPaused(gameId: String): PausedContent? =
            paused?.takeIf { it.game == gameId }

        override fun isVersionComplete(version: String, gameId: String?): Boolean =
            active?.version == version && (gameId == null || active?.game == gameId)

        override fun gameRoot(version: String, gameId: String): File =
            File("/fake/$version/$gameId")

        override fun deletePaused(gameId: String) {
            deletedPausedIds += gameId
            if (paused?.game == gameId) paused = null
        }

        override fun deleteLocalContent(gameId: String) {
            deletedLocalIds += gameId
            if (active?.game == gameId) active = null
            if (paused?.game == gameId) paused = null
        }
    }

    private class FakeContentUpdateExecutor(
        var status: ContentUpdateStatus = ContentUpdateStatus.Updated("v1"),
        var updateBlock: ((DownloadControl) -> ContentUpdateResult)? = null
    ) : ContentUpdateExecutor {
        val calls = mutableListOf<String>()

        override fun update(
            game: String,
            onStatus: (ContentUpdateStatus) -> Unit,
            control: DownloadControl
        ): ContentUpdateResult {
            calls += game
            val result = updateBlock?.invoke(control) ?: ContentUpdateResult(status = status)
            onStatus(result.status)
            return result
        }
    }

    private class DirectHomeContentScheduler : HomeContentScheduler {
        override fun postToMain(block: () -> Unit) = block()
        override fun runCatalogTask(block: () -> Unit) = block()
        override fun runContentTask(block: () -> Unit) = block()
        override fun dispose() = Unit
    }

    private class QueuedHomeContentScheduler : HomeContentScheduler {
        private val catalogTasks = mutableListOf<() -> Unit>()
        private val contentTasks = mutableListOf<() -> Unit>()

        override fun postToMain(block: () -> Unit) = block()

        override fun runCatalogTask(block: () -> Unit) {
            catalogTasks += block
        }

        override fun runContentTask(block: () -> Unit) {
            contentTasks += block
        }

        override fun dispose() = Unit

        fun drainCatalog() {
            val tasks = catalogTasks.toList()
            catalogTasks.clear()
            tasks.forEach { it() }
        }

        fun drainContent() {
            val tasks = contentTasks.toList()
            contentTasks.clear()
            tasks.forEach { it() }
        }
    }
}
