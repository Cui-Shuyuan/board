package com.boardai.tutorial.uaal

import android.Manifest
import android.content.pm.PackageManager
import android.graphics.Color
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.util.Log
import android.view.Gravity
import android.view.ViewGroup
import android.view.WindowManager
import android.widget.FrameLayout
import android.widget.Toast
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.darkColorScheme
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.mutableStateOf
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.ComposeView
import androidx.core.app.ActivityCompat
import androidx.core.content.ContextCompat
import com.boardai.tutorial.uaal.catalog.GameCatalogEntry
import com.boardai.tutorial.uaal.catalog.GameCatalogRepository
import com.boardai.tutorial.uaal.content.ContentStatus
import com.boardai.tutorial.uaal.content.ContentStore
import com.boardai.tutorial.uaal.content.DownloadControl
import com.boardai.tutorial.uaal.content.DownloadOverlayState
import com.boardai.tutorial.uaal.content.DownloadProgressOverlay
import com.boardai.tutorial.uaal.content.GameContentPromptDialog
import com.boardai.tutorial.uaal.content.GamePromptAction
import com.boardai.tutorial.uaal.content.GamePromptState
import com.boardai.tutorial.uaal.content.resolveContentStatus
import com.boardai.tutorial.uaal.content.ContentUpdateStateHolder
import com.boardai.tutorial.uaal.content.ContentUpdateStatus
import com.boardai.tutorial.uaal.content.ContentUpdater
import com.boardai.tutorial.uaal.history.PlayHistoryEntry
import com.boardai.tutorial.uaal.history.PlayHistoryRepository
import com.boardai.tutorial.uaal.history.RecentGame
import com.boardai.tutorial.uaal.home.HomeScreen
import com.boardai.tutorial.uaal.home.ResourceManagerOverlay
import com.boardai.tutorial.uaal.player.TutorialPlayerOverlay
import com.boardai.tutorial.uaal.qa.QaSessionHolder
import com.boardai.tutorial.uaal.qa.QaRepository
import com.boardai.tutorial.uaal.qa.buildQaContext
import com.boardai.tutorial.uaal.timeline.TutorialTimeline
import com.boardai.tutorial.uaal.voice.AsrRepository
import com.boardai.tutorial.uaal.voice.TtsRepository
import com.unity3d.player.UnityPlayer
import com.unity3d.player.UnityPlayerGameActivity
import java.io.File
import java.util.Locale
import java.util.concurrent.ExecutorService
import java.util.concurrent.Executors

/**
 * Kotlin + Jetpack Compose playback shell for the UaaL runtime.
 *
 * The Unity surface remains the Activity's main content view.  A full-screen
 * ComposeView is added on top through android.R.id.content.  The home screen is
 * deliberately opaque so Unity never shows through before a game is selected;
 * the player overlay stays transparent so the tutorial picture remains visible.
 */
class MainActivity : UnityPlayerGameActivity() {

    private val mainHandler = Handler(Looper.getMainLooper())
    private val contentExecutor: ExecutorService = Executors.newSingleThreadExecutor()
    private val catalogExecutor: ExecutorService = Executors.newSingleThreadExecutor()

    private lateinit var contentStore: ContentStore
    private lateinit var contentUpdater: ContentUpdater
    private lateinit var catalogRepository: GameCatalogRepository
    private lateinit var historyRepository: PlayHistoryRepository
    private lateinit var qaRepository: QaRepository
    private lateinit var asrRepository: AsrRepository
    private lateinit var ttsRepository: TtsRepository

    private var pendingMicPermissionCallback: ((Boolean) -> Unit)? = null

    private var catalogRefreshInProgress = false
    private var unityReadyHandled = false
    private var pendingLoadGameId: String? = null
    private var pendingLoadGameVersionRoot: String? = null
    private var pendingUnload = false
    private var activeDownloadGameId: String? = null
    private var currentDownloadControl: DownloadControl? = null
    private var downloadGeneration = 0

    @Volatile
    private var selectionGeneration = 0

    private val allCatalogGames = mutableStateOf<List<GameCatalogEntry>>(emptyList())
    private val visibleGames = mutableStateOf<List<GameCatalogEntry>>(emptyList())
    private val recentGames = mutableStateOf<List<RecentGame>>(emptyList())
    private val historyEntries = mutableStateOf<List<PlayHistoryEntry>>(emptyList())
    private val catalogLoading = mutableStateOf(false)
    private val catalogError = mutableStateOf<String?>(null)

    private val searchQuery = mutableStateOf("")
    private val contentStatuses = mutableStateOf<Map<String, ContentStatus>>(emptyMap())
    private val gamePrompt = mutableStateOf<GamePromptState?>(null)
    private val activeDownload = mutableStateOf<DownloadOverlayState?>(null)
    private val resourceManagerOpen = mutableStateOf(false)
    private val resourceChecking = mutableStateOf(false)
    private val selectedGame = mutableStateOf<GameCatalogEntry?>(null)
    private val playerActive = mutableStateOf(false)
    private val preparingGameId = mutableStateOf<String?>(null)
    private val playbackError = mutableStateOf<String?>(null)
    private val qaOpen = mutableStateOf(false)

    private var qaWasPlayingBeforeQuestion = false
    private var qaResumeCueId: String? = null
    private var qaResumePositionInCue = 0f

    private val tutorialTimeline = mutableStateOf<TutorialTimeline?>(null)

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)

        contentStore = ContentStore(this)
        contentUpdater = ContentUpdater(this, BuildConfig.BOARD_API_BASE_URL, contentStore)
        catalogRepository = GameCatalogRepository(this, BuildConfig.BOARD_API_BASE_URL)
        historyRepository = PlayHistoryRepository(this)
        qaRepository = QaRepository(BuildConfig.BOARD_API_BASE_URL)
        asrRepository = AsrRepository(BuildConfig.BOARD_API_BASE_URL)
        ttsRepository = TtsRepository(this, BuildConfig.BOARD_API_BASE_URL)

        loadCachedCatalogAndHistory()
        refreshCatalog(showLoading = allCatalogGames.value.isEmpty())

        applyKeepScreenOn(true)
        addComposeControlLayer()
        applyLockScreenPolicy()
    }

    override fun onDestroy() {
        selectionGeneration++
        QaSessionHolder.clear()
        contentExecutor.shutdownNow()
        catalogExecutor.shutdownNow()
        super.onDestroy()
    }

    fun hasRecordPermission(): Boolean {
        return ContextCompat.checkSelfPermission(
            this,
            Manifest.permission.RECORD_AUDIO
        ) == PackageManager.PERMISSION_GRANTED
    }

    fun requestRecordPermission(onResult: (Boolean) -> Unit) {
        if (hasRecordPermission()) {
            onResult(true)
            return
        }

        pendingMicPermissionCallback = onResult
        ActivityCompat.requestPermissions(
            this,
            arrayOf(Manifest.permission.RECORD_AUDIO),
            REQUEST_RECORD_AUDIO
        )
    }

    override fun onRequestPermissionsResult(
        requestCode: Int,
        permissions: Array<out String>,
        grantResults: IntArray
    ) {
        super.onRequestPermissionsResult(requestCode, permissions, grantResults)
        if (requestCode != REQUEST_RECORD_AUDIO) return

        val granted = grantResults.isNotEmpty() &&
            grantResults[0] == PackageManager.PERMISSION_GRANTED
        val callback = pendingMicPermissionCallback
        pendingMicPermissionCallback = null
        callback?.invoke(granted)
    }

    override fun onResume() {
        super.onResume()
        // Re-assert after Unity/GameActivity has finished its own window setup.
        applyLockScreenPolicy()
        applyKeepScreenOn(shouldKeepScreenOn())
        if (unityReadyHandled) {
            sendToUnity("RequestStatus", "")
        }
    }

    override fun onWindowFocusChanged(hasFocus: Boolean) {
        super.onWindowFocusChanged(hasFocus)
        if (hasFocus) {
            applyLockScreenPolicy()
        }
    }

    @Deprecated("Deprecated in Java")
    override fun onBackPressed() {
        when {
            qaOpen.value -> closeQa()
            gamePrompt.value != null -> gamePrompt.value = null
            resourceManagerOpen.value -> resourceManagerOpen.value = false
            activeDownload.value != null || activeDownloadGameId != null ->
                pauseDownloadAndReturnHome()
            selectedGame.value != null -> returnToHome()
            else -> {
                @Suppress("DEPRECATION")
                super.onBackPressed()
            }
        }
    }

    private fun applyLockScreenPolicy() {
        // Unity GameActivity / MIUI Game Turbo may place the game window above
        // the keyguard with these legacy flags.  This app is a normal tutorial
        // app: after the screen is locked, waking it should show the system
        // keyguard until the user unlocks the phone.
        window.clearFlags(
            WindowManager.LayoutParams.FLAG_SHOW_WHEN_LOCKED or
                WindowManager.LayoutParams.FLAG_TURN_SCREEN_ON or
                WindowManager.LayoutParams.FLAG_DISMISS_KEYGUARD
        )
    }

    private fun applyKeepScreenOn(keepOn: Boolean) {
        if (keepOn) {
            window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
            window.decorView.keepScreenOn = true
        } else {
            window.clearFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
            window.decorView.keepScreenOn = false
        }
    }

    private fun shouldKeepScreenOn(): Boolean {
        val status = UnityStatusHolder.status.value
        val activelyPlaying = status?.isPlaying == true && status?.isPaused != true
        // Keep the catalog/home screen awake; once a tutorial is selected,
        // only active playback may keep the screen on. Paused playback and the
        // QA panel fall back to the system screen timeout after inactivity.
        return selectedGame.value == null || (activelyPlaying && !qaOpen.value)
    }

    private fun loadCachedCatalogAndHistory() {
        val cached = catalogRepository.readCache()
        allCatalogGames.value = cached
        historyEntries.value = historyRepository.read()
        recomputeCatalogState()
    }

    private fun recomputeCatalogState() {
        val orderedVisible = catalogRepository.visibleGames(allCatalogGames.value)
            .sortedWith(
                compareBy<GameCatalogEntry> { it.nameZh.lowercase(Locale.ROOT) }
                    .thenBy { it.nameEn.lowercase(Locale.ROOT) }
                    .thenBy { it.id }
            )
        visibleGames.value = orderedVisible
        refreshContentStatuses(orderedVisible)

        val current = selectedGame.value
        if (current != null && orderedVisible.none { it.id == current.id }) {
            returnToHome()
            return
        }

        recentGames.value = historyRepository.recentGames(
            visibleGames = orderedVisible,
            history = historyEntries.value
        )
    }

    private fun refreshCatalog(showLoading: Boolean) {
        if (catalogRefreshInProgress) return
        catalogRefreshInProgress = true
        if (showLoading) catalogLoading.value = true

        catalogExecutor.execute {
            try {
                val fetched = catalogRepository.fetchCatalog()
                mainHandler.post {
                    catalogRefreshInProgress = false
                    catalogLoading.value = false
                    resourceChecking.value = false
                    catalogError.value = null
                    allCatalogGames.value = fetched
                    recomputeCatalogState()
                }
            } catch (t: Throwable) {
                Log.w(TAG, "catalog refresh failed", t)
                mainHandler.post {
                    catalogRefreshInProgress = false
                    catalogLoading.value = false
                    resourceChecking.value = false
                    if (allCatalogGames.value.isEmpty()) {
                        catalogError.value = "无法加载游戏目录，请检查网络后重试"
                    }
                }
            }
        }
    }

    private fun addComposeControlLayer() {
        val composeView = ComposeView(this).apply {
            setBackgroundColor(Color.TRANSPARENT)
            setContent {
                MaterialTheme(colorScheme = darkColorScheme()) {
                    Box(modifier = Modifier.fillMaxSize()) {
                        val status = UnityStatusHolder.status.value
                        val contentState = ContentUpdateStateHolder.state.value
                        val timeline = tutorialTimeline.value
                        val selected = selectedGame.value
                        val showPlayer = selected != null && playerActive.value
                        val contentStatusesState = contentStatuses.value
                        val promptState = gamePrompt.value
                        val downloadState = activeDownload.value
                        val resourceOpen = resourceManagerOpen.value
                        val resourceCheck = resourceChecking.value

                        val keepScreenOn = selected == null ||
                            (status?.isPlaying == true && status?.isPaused != true && !qaOpen.value)
                        LaunchedEffect(keepScreenOn) {
                            applyKeepScreenOn(keepScreenOn)
                        }

                        // Unity can only deliver the back key while the player
                        // owns the input surface.  Route it into the native
                        // state machine instead of letting the prototype quit.
                        val backRequest = UnityBackRequestHolder.requestVersion.value
                        LaunchedEffect(backRequest) {
                            if (backRequest <= 0) return@LaunchedEffect
                            when {
                                qaOpen.value -> closeQa()
                                gamePrompt.value != null -> gamePrompt.value = null
                                resourceManagerOpen.value -> resourceManagerOpen.value = false
                                activeDownload.value != null || activeDownloadGameId != null ->
                                    pauseDownloadAndReturnHome()
                                selectedGame.value != null -> returnToHome()
                            }
                        }

                        // Unity commands can only be delivered after the
                        // bridge sends its first status with unityReady=true.
                        LaunchedEffect(status?.unityReady) {
                            if (status?.unityReady == true) {
                                onUnityReady()
                            }
                        }

                        // The Pause command is asynchronous.  Once Unity
                        // confirms the same cue is paused, use that
                        // authoritative position for the exact resume command.
                        LaunchedEffect(status?.cueId, status?.isPaused, qaOpen.value) {
                            val current = status ?: return@LaunchedEffect
                            if (!qaOpen.value || !current.isPaused || current.cueId.isBlank()) {
                                return@LaunchedEffect
                            }
                            if (qaResumeCueId.isNullOrBlank() || qaResumeCueId == current.cueId) {
                                qaResumeCueId = current.cueId
                                current.position
                                    .takeIf { it.isFinite() && it >= 0f }
                                    ?.let { qaResumePositionInCue = it }
                            }
                        }

                        if (showPlayer) {
                            TutorialPlayerOverlay(
                                status = status,
                                timeline = timeline,
                                game = selected,
                                activeVersion = contentState.activeVersion,
                                qaOpen = qaOpen.value,
                                qaRepository = qaRepository,
                                asrRepository = asrRepository,
                                ttsRepository = ttsRepository,
                                hasRecordPermission = { hasRecordPermission() },
                                requestRecordPermission = { callback ->
                                    requestRecordPermission(callback)
                                },
                                onOpenQa = { cueId, positionInCue, wasPlaying ->
                                    openQa(cueId, positionInCue, wasPlaying)
                                },
                                onCloseQa = { closeQa() },
                                onCommand = { method, value -> sendToUnity(method, value) }
                            )
                        } else {
                            HomeScreen(
                                games = visibleGames.value,
                                recentGames = recentGames.value,
                                query = searchQuery.value,
                                onQueryChange = { searchQuery.value = it },
                                onGameClick = { game -> onGameClick(game) },
                                onRetry = {
                                    catalogError.value = null
                                    refreshCatalog(showLoading = true)
                                },
                                isLoading = catalogLoading.value,
                                errorMessage = catalogError.value,
                                preparingGameId = preparingGameId.value,
                                playbackError = playbackError.value,
                                contentStatuses = contentStatusesState,
                                onOpenResourceManager = {
                                    resourceManagerOpen.value = true
                                    refreshContentStatuses()
                                }
                            )
                        }

                        if (resourceOpen) {
                            ResourceManagerOverlay(
                                games = visibleGames.value,
                                statuses = contentStatusesState,
                                checking = resourceCheck,
                                onDismiss = { resourceManagerOpen.value = false },
                                onCheckUpdates = { requestCatalogCheck() },
                                onDownload = { game -> startDownload(game) },
                                onContinueDownload = { game -> startDownload(game) },
                                onUpdate = { game -> startDownload(game) },
                                onContinueUpdate = { game -> startDownload(game) },
                                onDelete = { game -> deleteLocalResources(game) }
                            )
                        }

                        if (downloadState != null) {
                            DownloadProgressOverlay(
                                state = downloadState,
                                onPause = { pauseDownloadAndReturnHome() },
                                onRetry = { startDownload(downloadState.game) },
                                onReturnHome = {
                                    activeDownload.value = null
                                    refreshContentStatuses()
                                }
                            )
                        }

                        if (promptState != null) {
                            GameContentPromptDialog(
                                state = promptState,
                                onAction = { action -> handleGamePromptAction(action) }
                            )
                        }
                    }
                }
            }
        }

        val params = FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MATCH_PARENT,
            ViewGroup.LayoutParams.MATCH_PARENT,
            Gravity.TOP or Gravity.CENTER_HORIZONTAL
        )
        val content = findViewById<ViewGroup>(android.R.id.content)
        if (content != null) {
            content.addView(composeView, params)
        } else {
            addContentView(composeView, params)
        }
    }

    private fun onUnityReady() {
        if (unityReadyHandled) return
        unityReadyHandled = true

        Log.i(TAG, "Unity ready; sending initial commands")
        sendToUnity("SetUnityTouchControlsEnabled", "false")

        if (pendingUnload) {
            pendingUnload = false
            sendToUnity("UnloadGame", "")
        }

        val gameId = pendingLoadGameId
        val versionRoot = pendingLoadGameVersionRoot
        pendingLoadGameId = null
        pendingLoadGameVersionRoot = null
        if (gameId != null) {
            if (versionRoot != null) {
                sendToUnity("LoadGameWithRoot", "$gameId|$versionRoot")
            } else {
                sendToUnity("LoadGame", gameId)
            }
        }
        sendToUnity("RequestStatus", "")
    }

    private fun onGameClick(game: GameCatalogEntry) {
        val downloading = activeDownloadGameId
        if (downloading == game.id) {
            showToast("正在下载《${game.nameZh}》，请稍后")
            return
        }
        if (downloading != null) {
            val name = activeDownload.value?.game?.nameZh ?: "其他游戏"
            showToast("正在下载《$name》，请稍后")
            return
        }

        val status = resolveContentStatus(game, contentStore)
        contentStatuses.value = contentStatuses.value + (game.id to status)
        when (status) {
            ContentStatus.NoServerResource -> {
                gamePrompt.value = GamePromptState(game, status)
            }
            is ContentStatus.InstalledOffline,
            is ContentStatus.InstalledCurrent -> {
                enterGameWithLocalContent(game)
            }
            else -> {
                gamePrompt.value = GamePromptState(game, status)
            }
        }
    }

    private fun handleGamePromptAction(action: GamePromptAction) {
        val state = gamePrompt.value ?: return
        gamePrompt.value = null

        when (action) {
            GamePromptAction.DOWNLOAD -> startDownload(state.game)
            GamePromptAction.REDOWNLOAD -> startDownload(state.game, redownload = true)
            GamePromptAction.CONTINUE_DOWNLOAD,
            GamePromptAction.UPDATE,
            GamePromptAction.CONTINUE_UPDATE -> startDownload(state.game)
            GamePromptAction.USE_CURRENT -> enterGameWithLocalContent(state.game)
            GamePromptAction.CANCEL -> Unit
        }
    }

    private fun startDownload(game: GameCatalogEntry, redownload: Boolean = false) {
        if (activeDownloadGameId != null) {
            val name = activeDownload.value?.game?.nameZh ?: "其他游戏"
            showToast("正在下载《$name》，请稍后")
            return
        }

        if (redownload) {
            try {
                contentStore.deletePaused(game.id)
            } catch (t: Throwable) {
                Log.w(TAG, "failed to clear partial before redownload for ${game.id}", t)
            }
        }

        resourceManagerOpen.value = false
        gamePrompt.value = null

        clearQaState()
        selectionGeneration++
        val generation = ++downloadGeneration
        activeDownloadGameId = game.id
        currentDownloadControl = DownloadControl()
        val control = currentDownloadControl!!
        activeDownload.value = DownloadOverlayState(game, ContentUpdateStatus.Checking)
        preparingGameId.value = game.id
        playbackError.value = null

        contentExecutor.execute {
            val result = contentUpdater.update(
                game = game.id,
                onStatus = { status ->
                    mainHandler.post {
                        if (generation == downloadGeneration &&
                            activeDownloadGameId == game.id &&
                            activeDownload.value != null
                        ) {
                            activeDownload.value = DownloadOverlayState(game, status)
                        }
                    }
                },
                control = control
            )

            mainHandler.post {
                if (generation != downloadGeneration) return@post

                when (val status = result.status) {
                    is ContentUpdateStatus.Paused -> {
                        activeDownloadGameId = null
                        currentDownloadControl = null
                        activeDownload.value = null
                        preparingGameId.value = null
                        refreshContentStatuses()
                    }

                    is ContentUpdateStatus.Failed -> {
                        activeDownloadGameId = null
                        currentDownloadControl = null
                        preparingGameId.value = null
                        activeDownload.value = DownloadOverlayState(game, status)
                        refreshContentStatuses()
                    }

                    is ContentUpdateStatus.Updated,
                    is ContentUpdateStatus.UpToDate -> {
                        activeDownloadGameId = null
                        currentDownloadControl = null
                        activeDownload.value = null
                        preparingGameId.value = null
                        refreshContentStatuses()
                        enterGameWithLocalContent(game)
                    }

                    else -> {
                        activeDownloadGameId = null
                        currentDownloadControl = null
                        activeDownload.value = null
                        preparingGameId.value = null
                        refreshContentStatuses()
                    }
                }
            }
        }
    }

    private fun pauseDownloadAndReturnHome() {
        currentDownloadControl?.requestPause()
        resourceManagerOpen.value = false
        activeDownload.value = null
        preparingGameId.value = null
        playbackError.value = null
    }

    private fun enterGameWithLocalContent(game: GameCatalogEntry) {
        resourceManagerOpen.value = false
        clearQaState()
        val generation = ++selectionGeneration
        val visibleGameIds = visibleGames.value.map { it.id }.toSet()
        preparingGameId.value = game.id
        playbackError.value = null
        activeDownload.value = null

        contentExecutor.execute {
            val loaded = loadLocalContentSync(game, visibleGameIds)
            mainHandler.post {
                if (generation != selectionGeneration) return@post

                if (loaded == null) {
                    preparingGameId.value = null
                    playbackError.value = "本地内容缺失或损坏，请重新下载"
                    refreshContentStatuses()
                    return@post
                }

                tutorialTimeline.value = loaded.timeline
                ContentUpdateStateHolder.state.value = ContentUpdateStateHolder.state.value.copy(
                    activeVersion = loaded.version,
                    status = ContentUpdateStatus.UpToDate(loaded.version)
                )

                if (loaded.history != null) {
                    historyEntries.value = loaded.history
                    recentGames.value = historyRepository.recentGames(
                        visibleGames = visibleGames.value,
                        history = loaded.history
                    )
                }

                selectedGame.value = game
                playerActive.value = true
                preparingGameId.value = null
                playbackError.value = null
                sendLoadGameWithRoot(game.id, loaded.versionRoot.absolutePath)
            }
        }
    }

    private fun loadLocalContentSync(
        game: GameCatalogEntry,
        visibleGameIds: Set<String>
    ): LoadedLocalContent? {
        val active = contentStore.readActiveValid(game.id) ?: return null
        val gameRoot = contentStore.gameRoot(active.version, game.id)
        if (!gameRoot.isDirectory) return null

        val timeline = loadTimelineSync(gameRoot, game.tutorialTrack) ?: return null
        val history = try {
            historyRepository.recordPlay(game.id, visibleGameIds)
        } catch (t: Throwable) {
            Log.w(TAG, "failed to record play history for ${game.id}", t)
            null
        }

        return LoadedLocalContent(
            version = active.version,
            versionRoot = contentStore.versionDir(active.version),
            timeline = timeline,
            history = history
        )
    }

    private fun deleteLocalResources(game: GameCatalogEntry) {
        if (activeDownloadGameId != null) {
            showToast("下载进行中，暂不能删除资源")
            return
        }

        contentExecutor.execute {
            try {
                contentStore.deleteLocalContent(game.id)
            } catch (t: Throwable) {
                Log.w(TAG, "failed to delete local content for ${game.id}", t)
            }
            mainHandler.post {
                refreshContentStatuses()
                showToast("已删除《${game.nameZh}》本地资源")
            }
        }
    }

    private fun requestCatalogCheck() {
        if (catalogRefreshInProgress) {
            resourceChecking.value = false
            return
        }
        resourceChecking.value = true
        refreshCatalog(showLoading = false)
    }

    private fun refreshContentStatuses(games: List<GameCatalogEntry> = visibleGames.value) {
        contentStatuses.value = games.associate { game ->
            game.id to resolveContentStatus(game, contentStore)
        }
    }

    private fun showToast(message: String) {
        Toast.makeText(this, message, Toast.LENGTH_SHORT).show()
    }

    private fun loadTimelineSync(gameRoot: File, track: String): TutorialTimeline? {
        if (!gameRoot.isDirectory) {
            Log.w(TAG, "cannot load timeline; game root missing: ${gameRoot.absolutePath}")
            return null
        }

        return try {
            val loaded = TutorialTimeline.load(gameRoot, track)
            Log.i(
                TAG,
                "timeline loaded: ${loaded.cueCount} cues, " +
                    "${"%.2f".format(loaded.totalDuration)}s, " +
                    "${loaded.allChapters.size} chapter nodes"
            )
            loaded
        } catch (t: Throwable) {
            Log.w(TAG, "failed to load timeline from ${gameRoot.absolutePath} track=$track", t)
            null
        }
    }

    private fun sendLoadGameWithRoot(gameId: String, versionRoot: String) {
        if (unityReadyHandled) {
            pendingUnload = false
            pendingLoadGameId = null
            pendingLoadGameVersionRoot = null
            sendToUnity("LoadGameWithRoot", "$gameId|$versionRoot")
            sendToUnity("RequestStatus", "")
        } else {
            pendingUnload = false
            pendingLoadGameId = gameId
            pendingLoadGameVersionRoot = versionRoot
        }
    }

    private fun sendLoadGame(gameId: String) {
        if (unityReadyHandled) {
            pendingUnload = false
            pendingLoadGameId = null
            pendingLoadGameVersionRoot = null
            sendToUnity("LoadGame", gameId)
            sendToUnity("RequestStatus", "")
        } else {
            pendingUnload = false
            pendingLoadGameId = gameId
            pendingLoadGameVersionRoot = null
        }
    }

    private fun sendUnloadGame() {
        clearQaState()
        if (unityReadyHandled) {
            pendingLoadGameId = null
            pendingLoadGameVersionRoot = null
            pendingUnload = false
            sendToUnity("UnloadGame", "")
            sendToUnity("RequestStatus", "")
        } else {
            pendingLoadGameId = null
            pendingLoadGameVersionRoot = null
            pendingUnload = true
        }
    }

    private fun openQa(
        capturedCueId: String,
        capturedPositionInCue: Float,
        wasPlaying: Boolean
    ) {
        val game = selectedGame.value ?: return
        if (qaOpen.value) return

        val status = UnityStatusHolder.status.value
        val timeline = tutorialTimeline.value
        val timelineCue = status?.let { current ->
            timeline?.cueAt(current.cueIndex)
                ?: timeline?.cueById(current.cueId)
        }

        qaResumeCueId = capturedCueId
            .takeIf { it.isNotBlank() }
            ?: timelineCue?.id?.takeIf { it.isNotBlank() }
            ?: status?.cueId.orEmpty()
        qaResumePositionInCue = capturedPositionInCue
            .takeIf { it.isFinite() && it >= 0f }
            ?: status?.position?.takeIf { it.isFinite() && it >= 0f }
            ?: 0f
        qaWasPlayingBeforeQuestion = wasPlaying

        QaSessionHolder.ensureSession(buildQaContext(game, status, timeline))

        if (qaWasPlayingBeforeQuestion) {
            sendToUnity("Pause", "")
        }

        qaOpen.value = true
    }

    private fun closeQa() {
        if (!qaOpen.value) return
        qaOpen.value = false

        if (qaWasPlayingBeforeQuestion) {
            val cueId = qaResumeCueId.orEmpty()
            if (cueId.isNotBlank()) {
                val position = String.format(
                    Locale.US,
                    "%.3f",
                    qaResumePositionInCue.coerceAtLeast(0f)
                )
                sendToUnity("PlayCueAt", "$cueId|$position|0")
            } else {
                sendToUnity("Resume", "")
            }
        }

        qaWasPlayingBeforeQuestion = false
        qaResumeCueId = null
        qaResumePositionInCue = 0f
    }

    private fun clearQaState() {
        qaOpen.value = false
        qaWasPlayingBeforeQuestion = false
        qaResumeCueId = null
        qaResumePositionInCue = 0f
        QaSessionHolder.clear()
    }

    private fun returnToHome() {
        val hadSelection = selectedGame.value != null || playerActive.value || preparingGameId.value != null
        if (!hadSelection) return

        clearQaState()
        selectionGeneration++
        selectedGame.value = null
        playerActive.value = false
        preparingGameId.value = null
        playbackError.value = null
        tutorialTimeline.value = null
        ContentUpdateStateHolder.state.value = ContentUpdateStateHolder.state.value.copy(
            activeVersion = null,
            status = ContentUpdateStatus.Idle
        )
        sendUnloadGame()
    }


    private fun sendToUnity(method: String, value: String = "") {
        try {
            UnityPlayer.UnitySendMessage(BRIDGE_OBJECT, method, value)
        } catch (t: Throwable) {
            Log.e(TAG, "UnitySendMessage failed: $method($value)", t)
        }
    }

    private data class LoadedLocalContent(
        val version: String,
        val versionRoot: File,
        val timeline: TutorialTimeline,
        val history: List<PlayHistoryEntry>?
    )

    companion object {
        private const val TAG = "BoardAI-UaaL"
        private const val BRIDGE_OBJECT = "AndroidTutorialBridge"
        private const val REQUEST_RECORD_AUDIO = 3401
    }
}
