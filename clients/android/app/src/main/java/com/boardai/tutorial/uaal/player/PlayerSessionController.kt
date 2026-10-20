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
    private var runtimeFallbackInProgress = false

    @Volatile
    private var generation = 0

    fun enterGame(game: GameCatalogEntry, trackId: String? = null) {
        val selectedTrack = trackId
            ?.trim()
            ?.takeIf { it.isNotBlank() }
            ?: game.tutorialTrack.trim()
        cancelPendingEnter()
        localLoadInProgress = true
        runtimeFallbackInProgress = false
        onSetPreparingGame(game.id)
        clearPlaybackError()

        val enteredGeneration = generation
        runIoTask {
            val loaded = localContentLoader.load(game, selectedTrack)
            postToMain {
                if (enteredGeneration != generation) return@postToMain
                localLoadInProgress = false

                if (loaded == null) {
                    onSetPreparingGame(null)
                    _state.value = _state.value.copy(playbackError = LOCAL_CONTENT_ERROR)
                    onRefreshHomeContentStatuses()
                    return@postToMain
                }

                val resolvedTrack = loaded.track.ifBlank { selectedTrack }
                _state.value = PlayerSessionState(
                    selectedGame = game,
                    playerActive = true,
                    timeline = loaded.timeline,
                    activeTrack = resolvedTrack.takeIf { it.isNotBlank() },
                    activeVersion = loaded.version,
                    cueTimelinePlanActive = loaded.timeline.usesCueTimelinePlan,
                    playbackError = null
                )
                onRecordPlay(game.id)
                onSetPreparingGame(null)
                loadQueue.queueLoad(
                    gameId = game.id,
                    versionRoot = loaded.versionRoot.absolutePath,
                    track = resolvedTrack,
                    cueTimelinePlanActive = loaded.timeline.usesCueTimelinePlan
                )
            }
        }
    }

    /**
     * Reconciles the cue-timeline mode Unity actually entered with the mode
     * Android used for its local timeline model.  A plan accepted by Android
     * but rejected by Unity must switch the whole session back to the runtime
     * fallback; continuing would leave the two clocks on different timelines.
     *
     * Returns true when a fallback reload was scheduled.
     */
    fun onUnityTimelineMode(planActive: Boolean): Boolean {
        val current = _state.value
        val game = current.selectedGame ?: return false
        val currentTimeline = current.timeline ?: return false
        if (!current.playerActive || !currentTimeline.usesCueTimelinePlan) return false
        if (planActive || runtimeFallbackInProgress) return false
        if (localLoadInProgress) return false

        runtimeFallbackInProgress = true
        val track = current.activeTrack?.trim()?.takeIf { it.isNotBlank() }
            ?: game.tutorialTrack.trim()
        val enteredGeneration = generation
        runIoTask {
            val loaded = localContentLoader.loadRuntimeFallback(game, track)
            postToMain {
                if (enteredGeneration != generation) {
                    runtimeFallbackInProgress = false
                    return@postToMain
                }
                if (loaded == null) {
                    runtimeFallbackInProgress = false
                    _state.value = PlayerSessionState(
                        playbackError = LOCAL_CONTENT_ERROR
                    )
                    onSetPreparingGame(null)
                    loadQueue.queueUnload()
                    onRefreshHomeContentStatuses()
                    log("Unity rejected cue timeline plan but runtime fallback failed", null)
                    return@postToMain
                }
                _state.value = _state.value.copy(
                    timeline = loaded.timeline,
                    activeTrack = loaded.track.ifBlank { track },
                    activeVersion = loaded.version,
                    cueTimelinePlanActive = false,
                    playbackError = null
                )
                loadQueue.queueLoad(
                    gameId = game.id,
                    versionRoot = loaded.versionRoot.absolutePath,
                    track = loaded.track.ifBlank { track },
                    cueTimelinePlanActive = false
                )
                runtimeFallbackInProgress = false
                log(
                    "Unity rejected cue timeline plan; whole track reloaded in runtime compatibility mode",
                    null
                )
            }
        }
        return true
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
        runtimeFallbackInProgress = false
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
        runtimeFallbackInProgress = false
        loadQueue.clear()
    }

    private fun hasPending(): Boolean =
        localLoadInProgress || loadQueue.hasPending()

    private companion object {
        const val LOCAL_CONTENT_ERROR = "本地内容缺失或损坏，请重新下载"
    }
}
