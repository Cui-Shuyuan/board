package com.boardai.tutorial.uaal.player

internal class UnityLoadQueue(
    private val sendToUnity: (String, String) -> Unit
) {
    private var ready = false
    private var pendingLoadGameId: String? = null
    private var pendingLoadGameVersionRoot: String? = null
    private var pendingLoadTrack: String = "full"
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
        pendingLoadGameId = null
        pendingLoadGameVersionRoot = null
        if (gameId != null) {
            sendLoad(gameId, versionRoot, track)
        }

        sendToUnity("RequestStatus", "")
    }

    fun queueLoad(gameId: String, versionRoot: String?, track: String = "full") {
        val normalizedTrack = track.trim().ifBlank { "full" }
        pendingUnload = false
        pendingLoadGameId = gameId
        pendingLoadGameVersionRoot = versionRoot
        pendingLoadTrack = normalizedTrack

        if (ready) {
            pendingLoadGameId = null
            pendingLoadGameVersionRoot = null
            sendLoad(gameId, versionRoot, normalizedTrack)
            sendToUnity("RequestStatus", "")
        }
    }

    fun queueUnload() {
        pendingLoadGameId = null
        pendingLoadGameVersionRoot = null
        pendingLoadTrack = "full"

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
        pendingUnload = false
    }

    fun hasPending(): Boolean =
        pendingLoadGameId != null || pendingUnload

    private fun sendLoad(gameId: String, versionRoot: String?, track: String) {
        if (versionRoot != null) {
            val payload = if (track.isBlank() || track == "full") {
                "$gameId|$versionRoot"
            } else {
                "$gameId|$versionRoot|$track"
            }
            sendToUnity("LoadGameWithRoot", payload)
        } else {
            sendToUnity("LoadGame", gameId)
        }
    }
}
