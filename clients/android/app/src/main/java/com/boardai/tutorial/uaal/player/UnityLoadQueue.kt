package com.boardai.tutorial.uaal.player

internal class UnityLoadQueue(
    private val sendToUnity: (String, String) -> Unit
) {
    private var ready = false
    private var pendingLoadGameId: String? = null
    private var pendingLoadGameVersionRoot: String? = null
    private var pendingLoadTrack: String = "full"
    private var pendingCueTimelinePlanActive = false
    private var pendingUnload = false

    fun onUnityReady() {
        if (ready) return
        ready = true

        sendToUnity("SetUnityTouchControlsEnabled", "false")

        if (pendingUnload) {
            pendingUnload = false
            sendToUnity("UnloadGame", "")
        }

        val gameId = pendingLoadGameId
        val versionRoot = pendingLoadGameVersionRoot
        val track = pendingLoadTrack
        val cueTimelinePlanActive = pendingCueTimelinePlanActive
        pendingLoadGameId = null
        pendingLoadGameVersionRoot = null
        pendingCueTimelinePlanActive = false
        if (gameId != null) {
            sendLoad(gameId, versionRoot, track, cueTimelinePlanActive)
        }

        sendToUnity("RequestStatus", "")
    }

    fun queueLoad(
        gameId: String,
        versionRoot: String?,
        track: String = "full",
        cueTimelinePlanActive: Boolean = false
    ) {
        val normalizedTrack = track.trim().ifBlank { "full" }
        pendingUnload = false
        pendingLoadGameId = gameId
        pendingLoadGameVersionRoot = versionRoot
        pendingLoadTrack = normalizedTrack
        pendingCueTimelinePlanActive = cueTimelinePlanActive

        if (ready) {
            pendingLoadGameId = null
            pendingLoadGameVersionRoot = null
            pendingCueTimelinePlanActive = false
            sendLoad(gameId, versionRoot, normalizedTrack, cueTimelinePlanActive)
            sendToUnity("RequestStatus", "")
        }
    }

    fun queueUnload() {
        pendingLoadGameId = null
        pendingLoadGameVersionRoot = null
        pendingLoadTrack = "full"
        pendingCueTimelinePlanActive = false

        if (ready) {
            pendingUnload = false
            sendToUnity("UnloadGame", "")
            sendToUnity("RequestStatus", "")
        } else {
            pendingUnload = true
        }
    }

    fun requestStatusIfReady() {
        if (ready) {
            sendToUnity("RequestStatus", "")
        }
    }

    fun clear() {
        pendingLoadGameId = null
        pendingLoadGameVersionRoot = null
        pendingLoadTrack = "full"
        pendingCueTimelinePlanActive = false
        pendingUnload = false
    }

    fun hasPending(): Boolean =
        pendingLoadGameId != null || pendingUnload

    private fun sendLoad(
        gameId: String,
        versionRoot: String?,
        track: String,
        cueTimelinePlanActive: Boolean
    ) {
        if (versionRoot != null) {
            // Always forward the mode Android actually used.  Dropping the
            // flag for any track would let Android and Unity choose different
            // timing sources.  Phase 4 only puts "quick" in the catalog switch.
            val payload = when {
                track.isBlank() -> "$gameId|$versionRoot"
                cueTimelinePlanActive -> "$gameId|$versionRoot|$track|cue_timeline=1"
                track == "full" -> "$gameId|$versionRoot"
                else -> "$gameId|$versionRoot|$track"
            }
            sendToUnity("LoadGameWithRoot", payload)
        } else {
            sendToUnity("LoadGame", gameId)
        }
    }
}
