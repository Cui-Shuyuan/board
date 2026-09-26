package com.boardai.tutorial.uaal

import android.graphics.Color
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.util.Log
import android.view.Gravity
import android.view.ViewGroup
import android.view.WindowManager
import android.widget.FrameLayout
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.darkColorScheme
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.mutableStateOf
import androidx.compose.ui.platform.ComposeView
import com.boardai.tutorial.uaal.catalog.GameCatalogEntry
import com.boardai.tutorial.uaal.catalog.GameCatalogRepository
import com.boardai.tutorial.uaal.content.ContentStore
import com.boardai.tutorial.uaal.content.ContentUpdateStateHolder
import com.boardai.tutorial.uaal.content.ContentUpdateStatus
import com.boardai.tutorial.uaal.content.ContentUpdater
import com.boardai.tutorial.uaal.history.PlayHistoryEntry
import com.boardai.tutorial.uaal.history.PlayHistoryRepository
import com.boardai.tutorial.uaal.history.RecentGame
import com.boardai.tutorial.uaal.home.HomeScreen
import com.boardai.tutorial.uaal.player.TutorialPlayerOverlay
import com.boardai.tutorial.uaal.qa.QaSessionHolder
import com.boardai.tutorial.uaal.qa.QaRepository
import com.boardai.tutorial.uaal.qa.buildQaContext
import com.boardai.tutorial.uaal.timeline.TutorialTimeline
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

    private var contentCheckInProgress = false
    private var catalogRefreshInProgress = false
    private var unityReadyHandled = false
    private var pendingLoadGameId: String? = null
    private var pendingUnload = false

    @Volatile
    private var selectionGeneration = 0

    private val allCatalogGames = mutableStateOf<List<GameCatalogEntry>>(emptyList())
    private val visibleGames = mutableStateOf<List<GameCatalogEntry>>(emptyList())
    private val recentGames = mutableStateOf<List<RecentGame>>(emptyList())
    private val historyEntries = mutableStateOf<List<PlayHistoryEntry>>(emptyList())
    private val catalogLoading = mutableStateOf(false)
    private val catalogError = mutableStateOf<String?>(null)

    private val searchQuery = mutableStateOf("")
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

        loadCachedCatalogAndHistory()
        refreshCatalog(showLoading = allCatalogGames.value.isEmpty())

        keepScreenOn()
        addComposeControlLayer()
    }

    override fun onDestroy() {
        selectionGeneration++
        QaSessionHolder.clear()
        contentExecutor.shutdownNow()
        catalogExecutor.shutdownNow()
        super.onDestroy()
    }

    override fun onResume() {
        super.onResume()
        // Re-assert after Unity/GameActivity has finished its own window setup.
        keepScreenOn()
        if (unityReadyHandled) {
            sendToUnity("RequestStatus", "")
        }
    }

    @Deprecated("Deprecated in Java")
    override fun onBackPressed() {
        if (qaOpen.value) {
            closeQa()
        } else if (selectedGame.value != null) {
            returnToHome()
        } else {
            @Suppress("DEPRECATION")
            super.onBackPressed()
        }
    }

    private fun keepScreenOn() {
        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        window.decorView.keepScreenOn = true
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
                    catalogError.value = null
                    allCatalogGames.value = fetched
                    recomputeCatalogState()
                }
            } catch (t: Throwable) {
                Log.w(TAG, "catalog refresh failed", t)
                mainHandler.post {
                    catalogRefreshInProgress = false
                    catalogLoading.value = false
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
                    val status = UnityStatusHolder.status.value
                    val contentState = ContentUpdateStateHolder.state.value
                    val timeline = tutorialTimeline.value
                    val selected = selectedGame.value
                    val showPlayer = selected != null && playerActive.value

                    // Unity can only deliver the back key while the player owns
                    // the input surface.  Route it into the native home state
                    // instead of letting the old prototype call Application.Quit.
                    val backRequest = UnityBackRequestHolder.requestVersion.value
                    LaunchedEffect(backRequest) {
                        if (backRequest <= 0) return@LaunchedEffect
                        if (qaOpen.value) {
                            closeQa()
                        } else if (selectedGame.value != null) {
                            returnToHome()
                        }
                    }

                    // Unity commands can only be delivered after the bridge
                    // sends its first status containing unityReady=true.
                    LaunchedEffect(status?.unityReady) {
                        if (status?.unityReady == true) {
                            onUnityReady()
                        }
                    }

                    // The Pause command is asynchronous.  Once Unity confirms
                    // the same cue is paused, use that authoritative position
                    // for the exact resume command instead of the pre-pause
                    // interpolation anchor.
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
                            contentState = contentState,
                            game = selected,
                            qaOpen = qaOpen.value,
                            qaRepository = qaRepository,
                            onOpenQa = { cueId, positionInCue, wasPlaying ->
                                openQa(cueId, positionInCue, wasPlaying)
                            },
                            onCloseQa = { closeQa() },
                            onCommand = { method, value -> sendToUnity(method, value) },
                            onCheckContentUpdate = { refreshCurrentContent() }
                        )
                    } else {
                        HomeScreen(
                            games = visibleGames.value,
                            recentGames = recentGames.value,
                            query = searchQuery.value,
                            onQueryChange = { searchQuery.value = it },
                            onGameClick = { game -> prepareGame(game) },
                            onRetry = {
                                catalogError.value = null
                                refreshCatalog(showLoading = true)
                            },
                            isLoading = catalogLoading.value,
                            errorMessage = catalogError.value,
                            preparingGameId = preparingGameId.value,
                            playbackError = playbackError.value
                        )
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
        pendingLoadGameId?.let { gameId ->
            pendingLoadGameId = null
            sendToUnity("LoadGame", gameId)
        }
        sendToUnity("RequestStatus", "")
    }

    /**
     * Full selection flow:
     *   1. select a catalog game;
     *   2. update/download its content;
     *   3. load its tutorial timeline;
     *   4. record the play in local JSON;
     *   5. ask Unity to load the same game.
     */
    private fun prepareGame(game: GameCatalogEntry, recordPlay: Boolean = true) {
        clearQaState()
        val generation = ++selectionGeneration
        val visibleGameIds = visibleGames.value.map { it.id }.toSet()
        val cachedVersion = contentStore.readActive(game.id)?.version

        selectedGame.value = game
        playerActive.value = false
        preparingGameId.value = game.id
        playbackError.value = null
        tutorialTimeline.value = null
        ContentUpdateStateHolder.state.value = ContentUpdateStateHolder.state.value.copy(
            activeVersion = cachedVersion,
            status = ContentUpdateStatus.Idle
        )

        contentExecutor.execute {
            val result = contentUpdater.update(game.id) { status ->
                mainHandler.post { publishContentStatus(status) }
            }

            if (generation != selectionGeneration) return@execute

            val gameRoot = result.gameRoot ?: contentStore.readActive(game.id)?.root
            if (gameRoot == null) {
                mainHandler.post {
                    if (generation == selectionGeneration) {
                        preparingGameId.value = null
                        playbackError.value = result.error?.message
                            ?.takeIf { it.isNotBlank() }
                            ?.let { "内容更新失败：$it" }
                            ?: "无法加载游戏内容"
                    }
                }
                return@execute
            }

            val loaded = loadTimelineSync(gameRoot, game.tutorialTrack)
            if (loaded == null) {
                mainHandler.post {
                    if (generation == selectionGeneration) {
                        preparingGameId.value = null
                        playbackError.value = "该游戏教程内容缺失或损坏"
                    }
                }
                return@execute
            }

            val activeVersion = resolveActiveVersion(result.status, game.id)
            val updatedHistory = if (recordPlay) {
                try {
                    historyRepository.recordPlay(game.id, visibleGameIds)
                } catch (t: Throwable) {
                    Log.w(TAG, "failed to record play history for ${game.id}", t)
                    null
                }
            } else {
                null
            }

            mainHandler.post {
                if (generation != selectionGeneration) return@post

                tutorialTimeline.value = loaded
                ContentUpdateStateHolder.state.value = ContentUpdateStateHolder.state.value.copy(
                    activeVersion = activeVersion,
                    status = result.status
                )

                if (recordPlay && updatedHistory != null) {
                    historyEntries.value = updatedHistory
                    recentGames.value = historyRepository.recentGames(
                        visibleGames = visibleGames.value,
                        history = updatedHistory
                    )
                }

                preparingGameId.value = null
                playerActive.value = true
                playbackError.value = null
                sendLoadGame(game.id)
            }
        }
    }

    /**
     * Re-check content for the already visible game without touching the home
     * selection state.  Used by the player content panel.
     */
    private fun refreshCurrentContent() {
        val game = selectedGame.value ?: return
        if (contentCheckInProgress) return
        contentCheckInProgress = true
        publishContentStatus(ContentUpdateStatus.Checking)

        val generation = selectionGeneration
        contentExecutor.execute {
            val result = contentUpdater.update(game.id) { status ->
                mainHandler.post { publishContentStatus(status) }
            }

            if (generation != selectionGeneration || selectedGame.value?.id != game.id) {
                mainHandler.post { contentCheckInProgress = false }
                return@execute
            }

            val gameRoot = result.gameRoot ?: contentStore.readActive(game.id)?.root
            val loaded = gameRoot?.let { loadTimelineSync(it, game.tutorialTrack) }

            mainHandler.post {
                contentCheckInProgress = false
                if (generation != selectionGeneration || selectedGame.value?.id != game.id) {
                    return@post
                }

                val activeVersion = resolveActiveVersion(result.status, game.id)
                ContentUpdateStateHolder.state.value = ContentUpdateStateHolder.state.value.copy(
                    activeVersion = activeVersion,
                    status = result.status
                )
                loaded?.let { tutorialTimeline.value = it }

                result.error?.let {
                    Log.w(TAG, "content refresh failed; keeping previous active version", it)
                }

                if (result.changed) {
                    sendLoadGame(game.id)
                } else {
                    sendToUnity("RequestStatus", "")
                }
            }
        }
    }

    private fun resolveActiveVersion(
        status: ContentUpdateStatus,
        gameId: String
    ): String? = when (status) {
        is ContentUpdateStatus.UpToDate -> status.version
        is ContentUpdateStatus.Updated -> status.version
        else -> contentStore.readActive(gameId)?.version
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

    private fun sendLoadGame(gameId: String) {
        if (unityReadyHandled) {
            pendingUnload = false
            pendingLoadGameId = null
            sendToUnity("LoadGame", gameId)
            sendToUnity("RequestStatus", "")
        } else {
            pendingUnload = false
            pendingLoadGameId = gameId
        }
    }

    private fun sendUnloadGame() {
        clearQaState()
        if (unityReadyHandled) {
            pendingLoadGameId = null
            pendingUnload = false
            sendToUnity("UnloadGame", "")
            sendToUnity("RequestStatus", "")
        } else {
            pendingLoadGameId = null
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

    private fun publishContentStatus(status: ContentUpdateStatus) {
        ContentUpdateStateHolder.state.value = ContentUpdateStateHolder.state.value.copy(
            status = status
        )
    }

    private fun sendToUnity(method: String, value: String = "") {
        try {
            UnityPlayer.UnitySendMessage(BRIDGE_OBJECT, method, value)
        } catch (t: Throwable) {
            Log.e(TAG, "UnitySendMessage failed: $method($value)", t)
        }
    }

    companion object {
        private const val TAG = "BoardAI-UaaL"
        private const val BRIDGE_OBJECT = "AndroidTutorialBridge"
    }
}
