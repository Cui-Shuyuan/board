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
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.ComposeView
import androidx.core.app.ActivityCompat
import androidx.core.content.ContextCompat
import com.boardai.tutorial.uaal.catalog.GameCatalogEntry
import com.boardai.tutorial.uaal.catalog.GameCatalogRepository
import com.boardai.tutorial.uaal.content.ContentStore
import com.boardai.tutorial.uaal.content.ContentUpdateStatus
import com.boardai.tutorial.uaal.content.ContentUpdater
import com.boardai.tutorial.uaal.content.DownloadProgressOverlay
import com.boardai.tutorial.uaal.content.GameContentPromptDialog
import com.boardai.tutorial.uaal.history.PlayHistoryRepository
import com.boardai.tutorial.uaal.home.AndroidHomeContentScheduler
import com.boardai.tutorial.uaal.home.HomeContentCoordinator
import com.boardai.tutorial.uaal.home.HomeScreen
import com.boardai.tutorial.uaal.home.ResourceManagerOverlay
import com.boardai.tutorial.uaal.player.DefaultLocalContentLoader
import com.boardai.tutorial.uaal.player.PlayerSessionController
import com.boardai.tutorial.uaal.player.TutorialPlayerOverlay
import com.boardai.tutorial.uaal.qa.QaPanel
import com.boardai.tutorial.uaal.qa.QaSessionHolder
import com.boardai.tutorial.uaal.qa.QaRepository
import com.boardai.tutorial.uaal.qa.buildQaContext
import com.boardai.tutorial.uaal.voice.AsrRepository
import com.boardai.tutorial.uaal.voice.TtsRepository
import com.unity3d.player.UnityPlayer
import com.unity3d.player.UnityPlayerGameActivity
import java.util.Locale

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

    private lateinit var contentStore: ContentStore
    private lateinit var qaRepository: QaRepository
    private lateinit var asrRepository: AsrRepository
    private lateinit var ttsRepository: TtsRepository
    private lateinit var homeContent: HomeContentCoordinator
    private lateinit var playerSession: PlayerSessionController

    private var pendingMicPermissionCallback: ((Boolean) -> Unit)? = null

    private val searchQuery = mutableStateOf("")
    private val qaOpen = mutableStateOf(false)
    private val rulesQaGame = mutableStateOf<GameCatalogEntry?>(null)
    private val debugOverlayEnabled = mutableStateOf(BuildConfig.DEBUG)

    private var qaWasPlayingBeforeQuestion = false
    private var qaResumeCueId: String? = null
    private var qaResumePositionInCue = 0f

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)

        contentStore = ContentStore(this)
        val contentUpdater = ContentUpdater(BuildConfig.BOARD_API_BASE_URL, contentStore)
        val catalogRepository = GameCatalogRepository(this, BuildConfig.BOARD_API_BASE_URL)
        val historyRepository = PlayHistoryRepository(this)
        val homeScheduler = AndroidHomeContentScheduler()
        qaRepository = QaRepository(BuildConfig.BOARD_API_BASE_URL)
        asrRepository = AsrRepository(BuildConfig.BOARD_API_BASE_URL)
        ttsRepository = TtsRepository(this, BuildConfig.BOARD_API_BASE_URL)

        homeContent = HomeContentCoordinator(
            catalogRepository = catalogRepository,
            contentStore = contentStore,
            contentUpdater = contentUpdater,
            historyRepository = historyRepository,
            scheduler = homeScheduler,
            onEnterGame = { game ->
                homeContent.closeResourceManager()
                clearQaState()
                playerSession.enterGame(game)
            },
            onToast = { message -> showToast(message) },
            onOpenRulesQa = { game ->
                rulesQaGame.value = game
                QaSessionHolder.ensureSession(buildQaContext(game, null, null))
            },
            log = { message, throwable ->
                if (throwable == null) {
                    Log.w(TAG, message)
                } else {
                    Log.w(TAG, message, throwable)
                }
            },
            selectedGameIdProvider = { playerSession.state.value.selectedGame?.id },
            onSelectedGameUnavailable = { returnToHome() },
            onBeforeDownload = {
                clearQaState()
                playerSession.cancelPendingEnter()
            },
            onPlaybackErrorCleared = { playerSession.clearPlaybackError() }
        )

        playerSession = PlayerSessionController(
            localContentLoader = DefaultLocalContentLoader(contentStore),
            runIoTask = { block -> homeContent.runContentTask(block) },
            postToMain = { block -> mainHandler.post(block) },
            onSendToUnity = { method, value -> sendToUnity(method, value) },
            onRecordPlay = { gameId -> homeContent.recordPlay(gameId) },
            onSetPreparingGame = { gameId -> homeContent.setPreparingGame(gameId) },
            onRefreshHomeContentStatuses = { homeContent.refreshContentStatuses() },
            log = { message, throwable ->
                if (throwable == null) {
                    Log.w(TAG, message)
                } else {
                    Log.w(TAG, message, throwable)
                }
            }
        )

        homeContent.start()

        applyKeepScreenOn(shouldKeepScreenOn())
        addComposeControlLayer()
        applyLockScreenPolicy()
    }

    override fun onDestroy() {
        playerSession.dispose()
        QaSessionHolder.clear()
        homeContent.dispose()
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
        playerSession.requestStatusIfReady()
    }

    override fun onWindowFocusChanged(hasFocus: Boolean) {
        super.onWindowFocusChanged(hasFocus)
        if (hasFocus) {
            applyLockScreenPolicy()
        }
    }

    @Deprecated("Deprecated in Java")
    override fun onBackPressed() {
        if (!handleBackRequest()) {
            @Suppress("DEPRECATION")
            super.onBackPressed()
        }
    }

    /**
     * Single source of truth for Android and Unity back navigation.
     */
    private fun handleBackRequest(): Boolean {
        val homeState = homeContent.state.value
        return when {
            rulesQaGame.value != null -> {
                closeRulesQa()
                true
            }
            qaOpen.value -> {
                closeQa()
                true
            }
            homeState.gamePrompt != null -> {
                homeContent.dismissPrompt()
                true
            }
            homeState.resourceManagerOpen -> {
                homeContent.closeResourceManager()
                true
            }
            homeState.downloadBusy || homeState.activeDownload != null -> {
                homeContent.pauseDownloadAndReturnHome()
                true
            }
            playerSession.hasSessionOrPending() -> {
                returnToHome()
                true
            }
            else -> false
        }
    }

    private fun applyLockScreenPolicy() {
        // Clear the window flags Unity GameActivity / MIUI Game Turbo may set.
        // This is a normal tutorial app: after the screen is locked, waking it
        // shows the system keyguard until the user unlocks the phone.
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
        val activelyPlaying =
            status?.isPlaying == true && status?.isPaused != true

        val downloadStatus = homeContent.state.value.activeDownload?.status
        val downloadActive = when (downloadStatus) {
            ContentUpdateStatus.Checking,
            is ContentUpdateStatus.Downloading,
            is ContentUpdateStatus.Verifying,
            is ContentUpdateStatus.Switching -> true
            else -> false
        }

        val playerState = playerSession.state.value
        return downloadActive ||
            (playerState.selectedGame != null &&
                playerState.playerActive &&
                activelyPlaying &&
                !qaOpen.value)
    }

    private fun addComposeControlLayer() {
        val composeView = ComposeView(this).apply {
            setBackgroundColor(Color.TRANSPARENT)
            setContent {
                MaterialTheme(colorScheme = darkColorScheme()) {
                    Box(modifier = Modifier.fillMaxSize()) {
                        val homeState by homeContent.state.collectAsState()
                        val playerState by playerSession.state.collectAsState()
                        val status = UnityStatusHolder.status.value
                        val timeline = playerState.timeline
                        val selected = playerState.selectedGame
                        val showPlayer = selected != null && playerState.playerActive
                        val rulesQa = rulesQaGame.value

                        val keepScreenOn = shouldKeepScreenOn()
                        LaunchedEffect(keepScreenOn) {
                            applyKeepScreenOn(keepScreenOn)
                        }

                        // Unity can only deliver the back key while the player
                        // owns the input surface.  Route it into the native
                        // state machine instead of letting the prototype quit.
                        val backRequest = UnityBackRequestHolder.requestVersion.value
                        LaunchedEffect(backRequest) {
                            if (backRequest <= 0) return@LaunchedEffect
                            handleBackRequest()
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
                                activeVersion = playerState.activeVersion,
                                qaOpen = qaOpen.value,
                                debugOverlayEnabled = debugOverlayEnabled.value,
                                onToggleDebugOverlay = { toggleDebugOverlay() },
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
                                onBack = { returnToHome() },
                                onCommand = { method, value -> sendToUnity(method, value) }
                            )
                        } else {
                            HomeScreen(
                                games = homeState.visibleGames,
                                recentGames = homeState.recentGames,
                                query = searchQuery.value,
                                onQueryChange = { searchQuery.value = it },
                                onGameClick = { game -> homeContent.onGameClick(game) },
                                onRetry = {
                                    homeContent.refreshCatalog(showLoading = true)
                                },
                                isLoading = homeState.isLoading,
                                errorMessage = homeState.errorMessage,
                                preparingGameId = homeState.preparingGameId,
                                playbackError = playerState.playbackError,
                                contentStatuses = homeState.contentStatuses,
                                onOpenResourceManager = {
                                    homeContent.openResourceManager()
                                }
                            )
                        }

                        if (!showPlayer && rulesQa != null) {
                            QaPanel(
                                game = rulesQa,
                                status = null,
                                timeline = null,
                                repository = qaRepository,
                                asrRepository = asrRepository,
                                ttsRepository = ttsRepository,
                                hasRecordPermission = { hasRecordPermission() },
                                requestRecordPermission = { callback ->
                                    requestRecordPermission(callback)
                                },
                                onClose = { closeRulesQa() }
                            )
                        }

                        if (homeState.resourceManagerOpen) {
                            ResourceManagerOverlay(
                                games = homeState.visibleGames,
                                statuses = homeState.contentStatuses,
                                checking = homeState.resourceChecking,
                                onDismiss = { homeContent.closeResourceManager() },
                                onCheckUpdates = { homeContent.checkUpdates() },
                                onDownload = { game -> homeContent.startDownload(game) },
                                onContinueDownload = { game -> homeContent.startDownload(game) },
                                onUpdate = { game -> homeContent.startDownload(game) },
                                onContinueUpdate = { game -> homeContent.startDownload(game) },
                                onDelete = { game -> homeContent.deleteLocalResources(game) }
                            )
                        }

                        homeState.activeDownload?.let { downloadState ->
                            DownloadProgressOverlay(
                                state = downloadState,
                                onPause = { homeContent.pauseDownloadAndReturnHome() },
                                onRetry = { homeContent.startDownload(downloadState.game) },
                                onReturnHome = { homeContent.dismissDownloadError() }
                            )
                        }

                        homeState.gamePrompt?.let { promptState ->
                            GameContentPromptDialog(
                                state = promptState,
                                onAction = { action -> homeContent.handlePromptAction(action) }
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
        playerSession.onUnityReady()

        // Release builds explicitly disable Unity's debug gate.  Debug builds
        // enable the gate first, then restore the user's overlay state.
        sendToUnity("SetDebugBuild", if (BuildConfig.DEBUG) "1" else "0")
        if (BuildConfig.DEBUG) {
            sendToUnity("SetDebugOverlay", if (debugOverlayEnabled.value) "1" else "0")
        }
    }

    private fun toggleDebugOverlay() {
        if (!BuildConfig.DEBUG) return
        val enabled = !debugOverlayEnabled.value
        debugOverlayEnabled.value = enabled
        sendToUnity("SetDebugOverlay", if (enabled) "1" else "0")
    }

    private fun showToast(message: String) {
        Toast.makeText(this, message, Toast.LENGTH_SHORT).show()
    }

    private fun openQa(
        capturedCueId: String,
        capturedPositionInCue: Float,
        wasPlaying: Boolean
    ) {
        val game = playerSession.state.value.selectedGame ?: return
        if (qaOpen.value) return

        val status = UnityStatusHolder.status.value
        val timeline = playerSession.state.value.timeline
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
        rulesQaGame.value = null
        qaWasPlayingBeforeQuestion = false
        qaResumeCueId = null
        qaResumePositionInCue = 0f
        QaSessionHolder.clear()
    }

    private fun closeRulesQa() {
        rulesQaGame.value = null
        QaSessionHolder.clear()
    }

    private fun returnToHome() {
        val hadSelection = playerSession.hasSessionOrPending() ||
            homeContent.state.value.preparingGameId != null
        if (!hadSelection) return

        clearQaState()
        playerSession.returnToHome()
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
        private const val REQUEST_RECORD_AUDIO = 3401
    }
}
