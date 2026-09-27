package com.boardai.tutorial.uaal.home

import com.boardai.tutorial.uaal.catalog.GameCatalogEntry
import com.boardai.tutorial.uaal.catalog.HomeCatalogSource
import com.boardai.tutorial.uaal.history.HomeHistorySource
import com.boardai.tutorial.uaal.content.ContentUpdateExecutor
import com.boardai.tutorial.uaal.content.HomeContentStore
import com.boardai.tutorial.uaal.content.ContentStatus
import com.boardai.tutorial.uaal.content.ContentUpdateStatus
import com.boardai.tutorial.uaal.content.DownloadControl
import com.boardai.tutorial.uaal.content.DownloadOverlayState
import com.boardai.tutorial.uaal.content.GamePromptAction
import com.boardai.tutorial.uaal.content.GamePromptState
import com.boardai.tutorial.uaal.content.resolveContentStatus
import com.boardai.tutorial.uaal.history.PlayHistoryEntry
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import java.util.Locale

/**
 * Owns the home catalog, content status, resource manager, download and
 * content-prompt state that used to live directly in MainActivity.
 *
 * All state mutation happens on the Android main thread.  Blocking catalog and
 * content work is scheduled through [scheduler], whose production
 * implementation preserves the original single-thread executors and main
 * handler.
 */
class HomeContentCoordinator(
    private val catalogRepository: HomeCatalogSource,
    private val contentStore: HomeContentStore,
    private val contentUpdater: ContentUpdateExecutor,
    private val historyRepository: HomeHistorySource,
    private val scheduler: HomeContentScheduler,
    private val onEnterGame: (GameCatalogEntry) -> Unit,
    private val onToast: (String) -> Unit,
    private val log: (String, Throwable?) -> Unit,
    private val selectedGameIdProvider: () -> String? = { null },
    private val onSelectedGameUnavailable: () -> Unit = {},
    private val onBeforeDownload: () -> Unit = {},
    private val onPlaybackErrorCleared: () -> Unit = {}
) {
    private val _state = MutableStateFlow(HomeContentState())
    val state: StateFlow<HomeContentState> = _state.asStateFlow()

    private var allCatalogGames: List<GameCatalogEntry> = emptyList()
    private var historyEntries: List<PlayHistoryEntry> = emptyList()
    private var catalogRefreshInProgress = false
    private var activeDownloadGameId: String? = null
    private var currentDownloadControl: DownloadControl? = null
    private var downloadGeneration = 0
    private var started = false
    private var disposed = false

    fun start() {
        if (started || disposed) return
        started = true

        allCatalogGames = catalogRepository.readCache()
        historyEntries = historyRepository.read()
        recomputeCatalogState()
        refreshCatalog(showLoading = allCatalogGames.isEmpty())
    }

    fun dispose() {
        if (disposed) return
        disposed = true
        currentDownloadControl?.requestPause()
        scheduler.dispose()
    }

    fun refreshCatalog(showLoading: Boolean) {
        if (disposed || catalogRefreshInProgress) return
        catalogRefreshInProgress = true
        if (showLoading) {
            updateState { it.copy(isLoading = true) }
        }

        scheduler.runCatalogTask {
            try {
                val fetched = catalogRepository.fetchCatalog()
                scheduler.postToMain {
                    if (disposed) return@postToMain
                    catalogRefreshInProgress = false
                    updateState {
                        it.copy(
                            isLoading = false,
                            resourceChecking = false,
                            errorMessage = null
                        )
                    }
                    allCatalogGames = fetched
                    recomputeCatalogState()
                }
            } catch (t: Throwable) {
                log("catalog refresh failed", t)
                scheduler.postToMain {
                    if (disposed) return@postToMain
                    val wasChecking = _state.value.resourceChecking
                    catalogRefreshInProgress = false
                    updateState {
                        it.copy(
                            isLoading = false,
                            resourceChecking = false,
                            errorMessage = if (allCatalogGames.isEmpty()) {
                                "无法加载游戏目录，请检查网络后重试"
                            } else {
                                it.errorMessage
                            }
                        )
                    }
                    if (wasChecking) {
                        onToast("检查更新失败，请稍后重试")
                    }
                }
            }
        }
    }

    fun checkUpdates() {
        // 只要用户/页面发起了检查，就保持 checking
        updateState { it.copy(resourceChecking = true) }

        if (catalogRefreshInProgress) {
            // 已有刷新在飞行中；等它完成即可，refreshCatalog 成功/失败会负责清掉 resourceChecking
            return
        }

        refreshCatalog(showLoading = false)
    }

    fun onGameClick(game: GameCatalogEntry) {
        val downloading = activeDownloadGameId
        if (downloading == game.id) {
            onToast("正在下载《${game.nameZh}》，请稍后")
            return
        }
        if (downloading != null) {
            val name = _state.value.activeDownload?.game?.nameZh ?: "其他游戏"
            onToast("正在下载《$name》，请稍后")
            return
        }

        val status = resolveContentStatus(game, contentStore)
        updateState { it.copy(contentStatuses = it.contentStatuses + (game.id to status)) }
        when (status) {
            ContentStatus.NoServerResource -> {
                updateState { it.copy(gamePrompt = GamePromptState(game, status)) }
            }
            is ContentStatus.InstalledOffline,
            is ContentStatus.InstalledCurrent -> {
                onEnterGame(game)
            }
            else -> {
                updateState { it.copy(gamePrompt = GamePromptState(game, status)) }
            }
        }
    }

    fun handlePromptAction(action: GamePromptAction) {
        val prompt = _state.value.gamePrompt ?: return
        updateState { it.copy(gamePrompt = null) }

        when (action) {
            GamePromptAction.DOWNLOAD -> startDownload(prompt.game)
            GamePromptAction.REDOWNLOAD -> startDownload(prompt.game, redownload = true)
            GamePromptAction.CONTINUE_DOWNLOAD,
            GamePromptAction.UPDATE,
            GamePromptAction.CONTINUE_UPDATE -> startDownload(prompt.game)
            GamePromptAction.USE_CURRENT -> onEnterGame(prompt.game)
            GamePromptAction.CANCEL -> Unit
        }
    }

    fun dismissPrompt() {
        updateState { it.copy(gamePrompt = null) }
    }

    fun openResourceManager() {
        updateState { it.copy(resourceManagerOpen = true) }
        refreshContentStatuses()  // 先用当前本地状态快速渲染
        checkUpdates()            // 再异步请求最新 catalog
    }

    fun closeResourceManager() {
        updateState { it.copy(resourceManagerOpen = false) }
    }

    fun startDownload(game: GameCatalogEntry, redownload: Boolean = false) {
        if (activeDownloadGameId != null) {
            val name = _state.value.activeDownload?.game?.nameZh ?: "其他游戏"
            onToast("正在下载《$name》，请稍后")
            return
        }

        if (redownload) {
            try {
                contentStore.deletePaused(game.id)
            } catch (t: Throwable) {
                log("failed to clear partial before redownload for ${game.id}", t)
            }
        }

        updateState { it.copy(resourceManagerOpen = false, gamePrompt = null) }
        onBeforeDownload()
        onPlaybackErrorCleared()

        val generation = ++downloadGeneration
        activeDownloadGameId = game.id
        currentDownloadControl = DownloadControl()
        val control = currentDownloadControl!!
        updateState {
            it.copy(
                activeDownload = DownloadOverlayState(game, ContentUpdateStatus.Checking),
                preparingGameId = game.id,
                downloadBusy = true
            )
        }

        scheduler.runContentTask {
            val result = contentUpdater.update(
                game = game.id,
                onStatus = { status ->
                    scheduler.postToMain {
                        if (disposed ||
                            generation != downloadGeneration ||
                            activeDownloadGameId != game.id ||
                            _state.value.activeDownload == null
                        ) {
                            return@postToMain
                        }
                        updateState { it.copy(activeDownload = DownloadOverlayState(game, status)) }
                    }
                },
                control = control
            )

            scheduler.postToMain {
                if (disposed || generation != downloadGeneration) return@postToMain
                when (val status = result.status) {
                    is ContentUpdateStatus.Paused -> {
                        clearDownloadRun(activeDownload = null)
                        refreshContentStatuses()
                    }

                    is ContentUpdateStatus.Failed -> {
                        clearDownloadRun(
                            activeDownload = DownloadOverlayState(game, status)
                        )
                        refreshContentStatuses()
                    }

                    is ContentUpdateStatus.Updated -> {
                        clearDownloadRun(activeDownload = null)
                        refreshContentStatuses()
                        onToast("《${game.nameZh}》已就绪，请选择游戏开始")
                    }

                    is ContentUpdateStatus.UpToDate -> {
                        clearDownloadRun(activeDownload = null)
                        refreshContentStatuses()
                        onToast("《${game.nameZh}》已是最新")
                    }

                    else -> {
                        clearDownloadRun(activeDownload = null)
                        refreshContentStatuses()
                    }
                }
            }
        }
    }

    fun pauseDownloadAndReturnHome() {
        currentDownloadControl?.requestPause()
        updateState {
            it.copy(
                resourceManagerOpen = false,
                activeDownload = null,
                preparingGameId = null
            )
        }
        onPlaybackErrorCleared()
    }

    fun hasActiveDownload(): Boolean =
        activeDownloadGameId != null || _state.value.activeDownload != null

    fun deleteLocalResources(game: GameCatalogEntry) {
        if (activeDownloadGameId != null) {
            onToast("下载进行中，暂不能删除资源")
            return
        }

        scheduler.runContentTask {
            try {
                contentStore.deleteLocalContent(game.id)
            } catch (t: Throwable) {
                log("failed to delete local content for ${game.id}", t)
            }
            scheduler.postToMain {
                if (disposed) return@postToMain
                refreshContentStatuses()
                onToast("已删除《${game.nameZh}》本地资源")
            }
        }
    }

    fun refreshContentStatuses() {
        refreshContentStatuses(_state.value.visibleGames)
    }

    fun recordPlay(gameId: String) {
        val visibleGames = _state.value.visibleGames
        val visibleGameIds = visibleGames.map { it.id }.toSet()

        scheduler.runContentTask {
            val updated = try {
                historyRepository.recordPlay(gameId, visibleGameIds)
            } catch (t: Throwable) {
                log("failed to record play history for $gameId", t)
                null
            }

            scheduler.postToMain {
                if (disposed || updated == null) return@postToMain
                historyEntries = updated
                updateState {
                    it.copy(
                        recentGames = historyRepository.recentGames(
                            visibleGames = _state.value.visibleGames,
                            history = updated
                        )
                    )
                }
            }
        }
    }

    fun setPreparingGame(gameId: String?) {
        updateState { it.copy(preparingGameId = gameId) }
    }

    /**
     * Schedules a blocking content task on the same scheduler slot used by
     * downloads.  MainActivity uses this for local timeline loading so
     * playback setup remains serialized with downloads exactly as before.
     */
    internal fun runContentTask(block: () -> Unit) {
        scheduler.runContentTask(block)
    }

    fun dismissDownloadError() {
        updateState { it.copy(activeDownload = null) }
        refreshContentStatuses()
    }

    private fun clearDownloadRun(activeDownload: DownloadOverlayState?) {
        activeDownloadGameId = null
        currentDownloadControl = null
        updateState {
            it.copy(
                activeDownload = activeDownload,
                preparingGameId = null,
                downloadBusy = false
            )
        }
    }

    private fun refreshContentStatuses(games: List<GameCatalogEntry>) {
        val statuses = games.associate { game ->
            game.id to resolveContentStatus(game, contentStore)
        }
        updateState { it.copy(contentStatuses = statuses) }
    }

    private fun recomputeCatalogState() {
        val orderedVisible = catalogRepository.visibleGames(allCatalogGames)
            .sortedWith(
                compareBy<GameCatalogEntry> { it.nameZh.lowercase(Locale.ROOT) }
                    .thenBy { it.nameEn.lowercase(Locale.ROOT) }
                    .thenBy { it.id }
            )
        val statuses = orderedVisible.associate { game ->
            game.id to resolveContentStatus(game, contentStore)
        }
        updateState {
            it.copy(
                visibleGames = orderedVisible,
                contentStatuses = statuses
            )
        }

        val currentGameId = selectedGameIdProvider()
        if (currentGameId != null && orderedVisible.none { it.id == currentGameId }) {
            onSelectedGameUnavailable()
            return
        }

        updateState {
            it.copy(
                recentGames = historyRepository.recentGames(
                    visibleGames = orderedVisible,
                    history = historyEntries
                )
            )
        }
    }

    private fun updateState(transform: (HomeContentState) -> HomeContentState) {
        _state.value = transform(_state.value)
    }
}
