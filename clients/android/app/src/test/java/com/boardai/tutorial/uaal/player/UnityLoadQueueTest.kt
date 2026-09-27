package com.boardai.tutorial.uaal.player

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class UnityLoadQueueTest {

    private val commands = mutableListOf<Pair<String, String>>()
    private val queue = UnityLoadQueue { method, value -> commands += method to value }

    @Test
    fun queueLoadBeforeReadyOnlyCaches() {
        queue.queueLoad(GAME_ID, VERSION_ROOT)

        assertTrue(commands.isEmpty())
        assertTrue(queue.hasPending())
    }

    @Test
    fun onUnityReadySendsTouchControlsThenLoadThenStatus() {
        queue.queueLoad(GAME_ID, VERSION_ROOT)

        queue.onUnityReady()

        assertEquals(
            listOf(
                "SetUnityTouchControlsEnabled" to "false",
                "LoadGameWithRoot" to "$GAME_ID|$VERSION_ROOT",
                "RequestStatus" to ""
            ),
            commands
        )
        assertFalse(queue.hasPending())
    }

    @Test
    fun queueUnloadBeforeReadySendsUnloadOnReady() {
        queue.queueUnload()

        queue.onUnityReady()

        assertEquals(
            listOf(
                "SetUnityTouchControlsEnabled" to "false",
                "UnloadGame" to "",
                "RequestStatus" to ""
            ),
            commands
        )
        assertFalse(queue.hasPending())
    }

    @Test
    fun queueUnloadClearsPendingLoad() {
        queue.queueLoad(GAME_ID, VERSION_ROOT)
        queue.queueUnload()

        queue.onUnityReady()

        assertTrue(commands.contains("UnloadGame" to ""))
        assertFalse(commands.any { it.first == "LoadGameWithRoot" || it.first == "LoadGame" })
    }

    @Test
    fun queueLoadClearsPendingUnload() {
        queue.queueUnload()
        queue.queueLoad(GAME_ID, VERSION_ROOT)

        queue.onUnityReady()

        assertTrue(commands.contains("LoadGameWithRoot" to "$GAME_ID|$VERSION_ROOT"))
        assertFalse(commands.any { it.first == "UnloadGame" })
    }

    @Test
    fun requestStatusIfReadyOnlySendsAfterReady() {
        queue.requestStatusIfReady()
        assertTrue(commands.isEmpty())

        queue.onUnityReady()
        commands.clear()
        queue.requestStatusIfReady()

        assertEquals(listOf("RequestStatus" to ""), commands)
    }

    @Test
    fun clearDropsPendingLoadAndUnload() {
        queue.queueLoad(GAME_ID, VERSION_ROOT)
        queue.queueUnload()
        queue.clear()

        queue.onUnityReady()

        assertFalse(queue.hasPending())
        assertFalse(commands.any { it.first == "LoadGameWithRoot" || it.first == "UnloadGame" })
        assertEquals(
            listOf(
                "SetUnityTouchControlsEnabled" to "false",
                "RequestStatus" to ""
            ),
            commands
        )
    }

    @Test
    fun queueLoadAfterReadySendsImmediately() {
        queue.onUnityReady()
        commands.clear()

        queue.queueLoad(GAME_ID, VERSION_ROOT)

        assertEquals(
            listOf(
                "LoadGameWithRoot" to "$GAME_ID|$VERSION_ROOT",
                "RequestStatus" to ""
            ),
            commands
        )
        assertFalse(queue.hasPending())
    }

    private companion object {
        const val GAME_ID = "splendor"
        const val VERSION_ROOT = "/content/v1/splendor"
    }
}
