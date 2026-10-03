package com.boardai.tutorial.uaal.player

import com.boardai.tutorial.uaal.catalog.GameCatalogEntry
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow

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
    private val localContentLoader: LocalContentLoader,
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

    private val loadQueue = UnityLoadQueue(onSendToUnity)

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
            val loaded = localContentLoader.load(game)
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
                loadQueue.queueLoad(game.id, loaded.versionRoot.absolutePath)
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
        loadQueue.queueUnload()
        return true
    }

    fun clearPlaybackError() {
        if (_state.value.playbackError != null) {
            _state.value = _state.value.copy(playbackError = null)
        }
    }

    fun onUnityReady() {
        loadQueue.onUnityReady()
    }

    /** Re-asserts Unity status after Activity resume. */
    fun requestStatusIfReady() {
        loadQueue.requestStatusIfReady()
    }

    fun hasSession(): Boolean =
        _state.value.selectedGame != null || _state.value.playerActive

    fun hasSessionOrPending(): Boolean =
        hasSession() || hasPending()

    fun dispose() {
        generation++
        localLoadInProgress = false
        loadQueue.clear()
    }

    private fun hasPending(): Boolean =
        localLoadInProgress || loadQueue.hasPending()

    private companion object {
        const val LOCAL_CONTENT_ERROR = "本地内容缺失或损坏，请重新下载"
    }
}
