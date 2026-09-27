package com.boardai.tutorial.uaal.player

internal class UnityLoadQueue(
    private val sendToUnity: (String, String) -> Unit
) {
    private var ready = false
    private var pendingLoadGameId: String? = null
    private var pendingLoadGameVersionRoot: String? = null
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
        pendingLoadGameId = null
        pendingLoadGameVersionRoot = null
        if (gameId != null) {
            sendLoad(gameId, versionRoot)
        }

        sendToUnity("RequestStatus", "")
    }

    fun queueLoad(gameId: String, versionRoot: String?) {
        pendingUnload = false
        pendingLoadGameId = gameId
        pendingLoadGameVersionRoot = versionRoot

        if (ready) {
            pendingLoadGameId = null
            pendingLoadGameVersionRoot = null
            sendLoad(gameId, versionRoot)
            sendToUnity("RequestStatus", "")
        }
    }

    fun queueUnload() {
        pendingLoadGameId = null
        pendingLoadGameVersionRoot = null

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
        pendingUnload = false
    }

    fun hasPending(): Boolean =
        pendingLoadGameId != null || pendingUnload

    private fun sendLoad(gameId: String, versionRoot: String?) {
        if (versionRoot != null) {
            sendToUnity("LoadGameWithRoot", "$gameId|$versionRoot")
        } else {
            sendToUnity("LoadGame", gameId)
        }
    }
}
