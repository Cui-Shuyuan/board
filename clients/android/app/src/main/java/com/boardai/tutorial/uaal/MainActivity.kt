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
import com.boardai.tutorial.uaal.content.ContentStore
import com.boardai.tutorial.uaal.content.ContentUpdateStateHolder
import com.boardai.tutorial.uaal.content.ContentUpdateStatus
import com.boardai.tutorial.uaal.content.ContentUpdater
import com.boardai.tutorial.uaal.player.TutorialPlayerOverlay
import com.boardai.tutorial.uaal.timeline.TutorialTimeline
import com.unity3d.player.UnityPlayer
import com.unity3d.player.UnityPlayerGameActivity
import java.io.File
import java.util.concurrent.ExecutorService
import java.util.concurrent.Executors

/**
 * Kotlin + Jetpack Compose playback shell for the UaaL runtime.
 *
 * The Unity surface remains the Activity's main content view.  A full-screen
 * transparent ComposeView is added on top through android.R.id.content; the
 * overlay is only responsible for transport chrome and does not own or replace
 * Unity's rendering surface.
 */
class MainActivity : UnityPlayerGameActivity() {

    private val mainHandler = Handler(Looper.getMainLooper())
    private val contentExecutor: ExecutorService = Executors.newSingleThreadExecutor()
    private lateinit var contentStore: ContentStore
    private lateinit var contentUpdater: ContentUpdater
    private var contentCheckInProgress = false
    private var unityReadyHandled = false
    private var timelineLoadGeneration = 0
    private val tutorialTimeline = mutableStateOf<TutorialTimeline?>(null)

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)

        contentStore = ContentStore(this)
        contentUpdater = ContentUpdater(this, BuildConfig.BOARD_API_BASE_URL, contentStore)
        ContentUpdateStateHolder.state.value = ContentUpdateStateHolder.state.value.copy(
            activeVersion = contentStore.readActive(CONTENT_GAME_ID)?.version
        )

        contentStore.readActive(CONTENT_GAME_ID)?.let { reloadTimeline(it.root) }

        keepScreenOn()
        addComposeControlLayer()
    }

    override fun onDestroy() {
        timelineLoadGeneration++
        contentExecutor.shutdownNow()
        super.onDestroy()
    }

    override fun onResume() {
        super.onResume()
        // Re-assert after Unity/GameActivity has finished its own window setup.
        keepScreenOn()
        // Coming back from background can happen after Unity/Android moved to
        // a paused state.  Ask the bridge for an authoritative snapshot
        // instead of relying on periodic polling.
        if (unityReadyHandled) {
            sendToUnity("RequestStatus", "")
        }
    }

    private fun keepScreenOn() {
        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        window.decorView.keepScreenOn = true
    }

    private fun addComposeControlLayer() {
        val composeView = ComposeView(this).apply {
            setBackgroundColor(Color.TRANSPARENT)
            setContent {
                MaterialTheme(colorScheme = darkColorScheme()) {
                    val status = UnityStatusHolder.status.value
                    val contentState = ContentUpdateStateHolder.state.value
                    val timeline = tutorialTimeline.value

                    // Unity commands can only be delivered after the bridge
                    // sends its first status containing unityReady=true.  The
                    // activity-level flag makes this a one-shot event even if
                    // Compose re-enters this effect.
                    LaunchedEffect(status?.unityReady) {
                        if (status?.unityReady == true) {
                            onUnityReady()
                        }
                    }

                    TutorialPlayerOverlay(
                        status = status,
                        timeline = timeline,
                        contentState = contentState,
                        onCommand = { method, value -> sendToUnity(method, value) },
                        onCheckContentUpdate = { checkContentUpdate() }
                    )
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
        // The bridge defaults to disabled on Android, but send this explicitly
        // so the policy stays visible and can be changed later through the
        // bridge API.
        sendToUnity("SetUnityTouchControlsEnabled", "false")
        sendToUnity("RequestStatus", "")
        checkContentUpdate()
    }

    private fun checkContentUpdate() {
        if (contentCheckInProgress) return
        contentCheckInProgress = true
        publishContentStatus(ContentUpdateStatus.Checking)

        contentExecutor.execute {
            val result = contentUpdater.update(CONTENT_GAME_ID) { status ->
                mainHandler.post { publishContentStatus(status) }
            }

            mainHandler.post {
                contentCheckInProgress = false
                val activeVersion = when (val status = result.status) {
                    is ContentUpdateStatus.UpToDate -> status.version
                    is ContentUpdateStatus.Updated -> status.version
                    else -> contentStore.readActive(CONTENT_GAME_ID)?.version
                }
                ContentUpdateStateHolder.state.value = ContentUpdateStateHolder.state.value.copy(
                    activeVersion = activeVersion,
                    status = result.status
                )

                result.error?.let {
                    Log.w(TAG, "content update failed; keeping previous active version", it)
                }

                if (result.changed && result.versionRoot != null) {
                    // SetContentRoot takes the version directory; TutorialCuePlayer
                    // still appends the game id (splendor/).
                    sendToUnity("SetContentRoot", result.versionRoot.absolutePath)
                    sendToUnity("ReloadGame", "")
                    sendToUnity("RequestStatus", "")
                    result.gameRoot?.let { reloadTimeline(it) }
                } else {
                    if (tutorialTimeline.value == null) {
                        val root = result.gameRoot
                            ?: contentStore.readActive(CONTENT_GAME_ID)?.root
                        root?.let { reloadTimeline(it) }
                    }
                }
            }
        }
    }

    private fun publishContentStatus(status: ContentUpdateStatus) {
        ContentUpdateStateHolder.state.value = ContentUpdateStateHolder.state.value.copy(
            status = status
        )
    }

    private fun reloadTimeline(gameRoot: File) {
        if (!gameRoot.isDirectory) {
            Log.w(TAG, "cannot load timeline; game root missing: ${gameRoot.absolutePath}")
            return
        }

        val generation = ++timelineLoadGeneration
        contentExecutor.execute {
            val loaded = try {
                TutorialTimeline.load(gameRoot)
            } catch (t: Throwable) {
                Log.w(TAG, "failed to load timeline from ${gameRoot.absolutePath}", t)
                null
            }

            mainHandler.post {
                if (generation != timelineLoadGeneration) return@post
                if (loaded != null) {
                    tutorialTimeline.value = loaded
                    Log.i(
                        TAG,
                        "timeline loaded: ${loaded.cueCount} cues, " +
                            "${"%.2f".format(loaded.totalDuration)}s, " +
                            "${loaded.allChapters.size} chapter nodes"
                    )
                }
            }
        }
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
        private const val CONTENT_GAME_ID = "splendor"
    }
}
