package com.boardai.tutorial.uaal.player

import com.boardai.tutorial.uaal.catalog.GameCatalogEntry
import com.boardai.tutorial.uaal.content.ContentStore
import com.boardai.tutorial.uaal.timeline.TutorialTimeline
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import java.io.File

/**
 * Owns the local playback-session state machine: selected game, timeline
 * loading, Unity-ready pending load/unload, stale-selection cancellation and
 * return-to-home.  Android/Unity details are injected by MainActivity so this
 * class stays a plain Kotlin controller.
 *
 * Public methods are expected to be called on the Android main thread.  Only
 * the blocking local-content work runs on [runIoTask]; its result is posted
 * back through [postToMain].
 */
class PlayerSessionController(
    private val contentStore: ContentStore,
    private val runIoTask: (() -> Unit) -> Unit,
    private val postToMain: (() -> Unit) -> Unit,
    private val onSendToUnity: (String, String) -> Unit,
    private val onRecordPlay: (String) -> Unit,
    private val onSetPreparingGame: (String?) -> Unit,
    private val onRefreshHomeContentStatuses: () -> Unit,
    private val log: (String, Throwable?) -> Unit
) {
    private val _state = MutableStateFlow(PlayerSessionState())
    val state: StateFlow<PlayerSessionState> = _state.asStateFlow()

    private var unityReadyHandled = false
    private var pendingLoadGameId: String? = null
    private var pendingLoadGameVersionRoot: String? = null
    private var pendingUnload = false
    private var localLoadInProgress = false

    @Volatile
    private var generation = 0

    fun enterGame(game: GameCatalogEntry) {
        cancelPendingEnter()
        localLoadInProgress = true
        onSetPreparingGame(game.id)
        clearPlaybackError()

        val enteredGeneration = generation
        runIoTask {
            val loaded = loadLocalContentSync(game)
            postToMain {
                if (enteredGeneration != generation) return@postToMain
                localLoadInProgress = false

                if (loaded == null) {
                    onSetPreparingGame(null)
                    _state.value = _state.value.copy(playbackError = LOCAL_CONTENT_ERROR)
                    onRefreshHomeContentStatuses()
                    return@postToMain
                }

                _state.value = PlayerSessionState(
                    selectedGame = game,
                    playerActive = true,
                    timeline = loaded.timeline,
                    activeVersion = loaded.version,
                    playbackError = null
                )
                onRecordPlay(game.id)
                onSetPreparingGame(null)
                sendLoadGameWithRoot(game.id, loaded.versionRoot.absolutePath)
            }
        }
    }

    /**
     * Cancels any in-flight local-content selection without touching an already
     * active session.
     */
    fun cancelPendingEnter() {
        generation++
        localLoadInProgress = false
    }

    /**
     * Clears the player session and tells Unity to unload.  Returns true only
     * when there was active session work to clear.
     */
    fun returnToHome(): Boolean {
        if (!hasSession() && !hasPending()) return false

        generation++
        localLoadInProgress = false
        _state.value = PlayerSessionState()
        onSetPreparingGame(null)
        sendUnloadGame()
        return true
    }

    fun clearPlaybackError() {
        if (_state.value.playbackError != null) {
            _state.value = _state.value.copy(playbackError = null)
        }
    }

    fun onUnityReady() {
        if (unityReadyHandled) return
        unityReadyHandled = true

        log("Unity ready; sending initial commands", null)
        onSendToUnity("SetUnityTouchControlsEnabled", "false")

        if (pendingUnload) {
            pendingUnload = false
            onSendToUnity("UnloadGame", "")
        }

        val gameId = pendingLoadGameId
        val versionRoot = pendingLoadGameVersionRoot
        pendingLoadGameId = null
        pendingLoadGameVersionRoot = null
        if (gameId != null) {
            if (versionRoot != null) {
                onSendToUnity("LoadGameWithRoot", "$gameId|$versionRoot")
            } else {
                onSendToUnity("LoadGame", gameId)
            }
        }
        onSendToUnity("RequestStatus", "")
    }

    /** Re-asserts Unity status after Activity resume, matching the old behavior. */
    fun requestStatusIfReady() {
        if (unityReadyHandled) {
            onSendToUnity("RequestStatus", "")
        }
    }

    fun hasSession(): Boolean =
        _state.value.selectedGame != null || _state.value.playerActive

    fun hasSessionOrPending(): Boolean =
        hasSession() || hasPending()

    fun dispose() {
        generation++
        localLoadInProgress = false
        pendingLoadGameId = null
        pendingLoadGameVersionRoot = null
        pendingUnload = false
    }

    private fun sendLoadGameWithRoot(gameId: String, versionRoot: String) {
        if (unityReadyHandled) {
            pendingUnload = false
            pendingLoadGameId = null
            pendingLoadGameVersionRoot = null
            onSendToUnity("LoadGameWithRoot", "$gameId|$versionRoot")
            onSendToUnity("RequestStatus", "")
        } else {
            pendingUnload = false
            pendingLoadGameId = gameId
            pendingLoadGameVersionRoot = versionRoot
        }
    }

    private fun sendUnloadGame() {
        pendingLoadGameId = null
        pendingLoadGameVersionRoot = null
        if (unityReadyHandled) {
            pendingUnload = false
            onSendToUnity("UnloadGame", "")
            onSendToUnity("RequestStatus", "")
        } else {
            pendingUnload = true
        }
    }

    private fun hasPending(): Boolean =
        localLoadInProgress || pendingLoadGameId != null || pendingUnload

    private fun loadLocalContentSync(game: GameCatalogEntry): LoadedLocalContent? {
        val active = contentStore.readActiveValid(game.id) ?: return null
        val gameRoot = contentStore.gameRoot(active.version, game.id)
        if (!gameRoot.isDirectory) return null

        val timeline = loadTimelineSync(gameRoot, game.tutorialTrack) ?: return null
        return LoadedLocalContent(
            version = active.version,
            versionRoot = contentStore.versionDir(active.version),
            timeline = timeline
        )
    }

    private fun loadTimelineSync(gameRoot: File, track: String): TutorialTimeline? {
        if (!gameRoot.isDirectory) {
            log("cannot load timeline; game root missing: ${gameRoot.absolutePath}", null)
            return null
        }

        return try {
            val loaded = TutorialTimeline.load(gameRoot, track)
            log(
                "timeline loaded: ${loaded.cueCount} cues, " +
                    "${"%.2f".format(loaded.totalDuration)}s, " +
                    "${loaded.allChapters.size} chapter nodes",
                null
            )
            loaded
        } catch (t: Throwable) {
            log("failed to load timeline from ${gameRoot.absolutePath} track=$track", t)
            null
        }
    }

    private data class LoadedLocalContent(
        val version: String,
        val versionRoot: File,
        val timeline: TutorialTimeline
    )

    private companion object {
        const val LOCAL_CONTENT_ERROR = "本地内容缺失或损坏，请重新下载"
    }
}
