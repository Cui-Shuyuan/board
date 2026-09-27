package com.boardai.tutorial.uaal.player

import com.boardai.tutorial.uaal.catalog.GameCatalogEntry
import com.boardai.tutorial.uaal.timeline.TutorialTimeline
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder
import java.io.File

class PlayerSessionControllerTest {

    @get:Rule
    val temp = TemporaryFolder()

    @Test
    fun enterGameSuccessPreparesEntersRecordsAndSendsLoad() {
        val loaded = loadedContent()
        val commands = mutableListOf<Pair<String, String>>()
        val preparing = mutableListOf<String?>()
        val records = mutableListOf<String>()
        val controller = controller(
            loader = FakeLocalContentLoader(loaded),
            onSendToUnity = { method, value -> commands += method to value },
            onRecordPlay = { records += it },
            onSetPreparingGame = { preparing += it }
        )
        val game = game("g1")

        controller.enterGame(game)
        controller.onUnityReady()

        assertEquals(game, controller.state.value.selectedGame)
        assertTrue(controller.state.value.playerActive)
        assertEquals(loaded.version, controller.state.value.activeVersion)
        assertEquals(loaded.timeline, controller.state.value.timeline)
        assertEquals(listOf(game.id), records)
        assertEquals(listOf(game.id, null), preparing)
        assertTrue(
            commands.contains(
                "LoadGameWithRoot" to "${game.id}|${loaded.versionRoot.absolutePath}"
            )
        )
    }

    @Test
    fun enterGameFailureSetsPlaybackErrorRefreshesHomeAndDoesNotLoadUnity() {
        val commands = mutableListOf<Pair<String, String>>()
        val preparing = mutableListOf<String?>()
        var refreshCalls = 0
        val controller = controller(
            loader = FakeLocalContentLoader(null),
            onSendToUnity = { method, value -> commands += method to value },
            onSetPreparingGame = { preparing += it },
            onRefreshHomeContentStatuses = { refreshCalls += 1 }
        )
        val game = game("missing")

        controller.enterGame(game)
        controller.onUnityReady()

        assertEquals("本地内容缺失或损坏，请重新下载", controller.state.value.playbackError)
        assertNull(controller.state.value.selectedGame)
        assertFalse(controller.state.value.playerActive)
        assertEquals(listOf(game.id, null), preparing)
        assertEquals(1, refreshCalls)
        assertFalse(commands.any { it.first == "LoadGame" || it.first == "LoadGameWithRoot" })
    }

    @Test
    fun returnToHomeDuringEnterCancelsGenerationAndNeverLoadsUnity() {
        val ioBlocks = mutableListOf<() -> Unit>()
        val commands = mutableListOf<Pair<String, String>>()
        val controller = controller(
            loader = FakeLocalContentLoader(loadedContent()),
            runIoTask = { ioBlocks += it },
            onSendToUnity = { method, value -> commands += method to value }
        )
        val game = game("cancel-generation")

        controller.enterGame(game)
        assertTrue(controller.hasSessionOrPending())

        assertTrue(controller.returnToHome())
        ioBlocks.single().invoke()

        assertNull(controller.state.value.selectedGame)
        assertFalse(controller.state.value.playerActive)
        assertTrue(controller.hasSessionOrPending())

        controller.onUnityReady()

        assertFalse(commands.any { it.first == "LoadGameWithRoot" || it.first == "LoadGame" })
        assertTrue(commands.contains("UnloadGame" to ""))
        assertFalse(controller.hasSessionOrPending())
    }

    @Test
    fun cancelPendingEnterDropsInFlightLoad() {
        val ioBlocks = mutableListOf<() -> Unit>()
        val commands = mutableListOf<Pair<String, String>>()
        val controller = controller(
            loader = FakeLocalContentLoader(loadedContent()),
            runIoTask = { ioBlocks += it },
            onSendToUnity = { method, value -> commands += method to value }
        )
        val game = game("cancel-pending")

        controller.enterGame(game)
        assertTrue(controller.hasSessionOrPending())

        controller.cancelPendingEnter()
        assertFalse(controller.hasSessionOrPending())

        ioBlocks.single().invoke()

        assertNull(controller.state.value.selectedGame)
        assertTrue(commands.isEmpty())
    }

    @Test
    fun returnToHomeWithSessionClearsStateSendsUnloadAndClearsPreparing() {
        val loaded = loadedContent()
        val commands = mutableListOf<Pair<String, String>>()
        val preparing = mutableListOf<String?>()
        val controller = controller(
            loader = FakeLocalContentLoader(loaded),
            onSendToUnity = { method, value -> commands += method to value },
            onSetPreparingGame = { preparing += it }
        )
        val game = game("active")

        controller.enterGame(game)
        controller.onUnityReady()
        commands.clear()

        assertTrue(controller.returnToHome())

        assertNull(controller.state.value.selectedGame)
        assertFalse(controller.state.value.playerActive)
        assertNull(controller.state.value.timeline)
        assertNull(controller.state.value.activeVersion)
        assertEquals(
            listOf(
                "UnloadGame" to "",
                "RequestStatus" to ""
            ),
            commands
        )
        assertEquals(listOf(game.id, null, null), preparing)
        assertFalse(controller.hasSessionOrPending())
    }

    @Test
    fun returnToHomeWithoutSessionOrPendingReturnsFalseAndSendsNothing() {
        val commands = mutableListOf<Pair<String, String>>()
        val controller = controller(
            onSendToUnity = { method, value -> commands += method to value }
        )

        assertFalse(controller.returnToHome())
        assertTrue(commands.isEmpty())
    }

    @Test
    fun hasSessionOrPendingCoversActiveLocalPendingLoadAndPendingUnload() {
        val loaded = loadedContent()

        // Active session.
        val activeController = controller(loader = FakeLocalContentLoader(loaded))
        activeController.enterGame(game("active"))
        activeController.onUnityReady()
        assertTrue(activeController.hasSessionOrPending())
        activeController.returnToHome()
        assertFalse(activeController.hasSessionOrPending())

        // Local load in progress.
        val pendingIoBlocks = mutableListOf<() -> Unit>()
        val loadingController = controller(
            loader = FakeLocalContentLoader(loaded),
            runIoTask = { pendingIoBlocks += it }
        )
        loadingController.enterGame(game("loading"))
        assertTrue(loadingController.hasSessionOrPending())
        loadingController.cancelPendingEnter()
        assertFalse(loadingController.hasSessionOrPending())

        // Pending Unity load (also has the active session set by enterGame).
        val pendingLoadController = controller(loader = FakeLocalContentLoader(loaded))
        pendingLoadController.enterGame(game("pending-load"))
        assertTrue(pendingLoadController.hasSessionOrPending())
        pendingLoadController.onUnityReady()
        assertTrue(pendingLoadController.hasSessionOrPending())

        // Pending Unity unload after a cancelled in-flight enter.
        val unloadIoBlocks = mutableListOf<() -> Unit>()
        val pendingUnloadController = controller(
            loader = FakeLocalContentLoader(loaded),
            runIoTask = { unloadIoBlocks += it }
        )
        pendingUnloadController.enterGame(game("pending-unload"))
        pendingUnloadController.returnToHome()
        assertTrue(pendingUnloadController.hasSessionOrPending())
        pendingUnloadController.onUnityReady()
        assertFalse(pendingUnloadController.hasSessionOrPending())
    }

    private fun controller(
        loader: LocalContentLoader = FakeLocalContentLoader(null),
        runIoTask: (() -> Unit) -> Unit = { it() },
        postToMain: (() -> Unit) -> Unit = { it() },
        onSendToUnity: (String, String) -> Unit = { _, _ -> },
        onRecordPlay: (String) -> Unit = {},
        onSetPreparingGame: (String?) -> Unit = {},
        onRefreshHomeContentStatuses: () -> Unit = {}
    ): PlayerSessionController =
        PlayerSessionController(
            localContentLoader = loader,
            runIoTask = runIoTask,
            postToMain = postToMain,
            onSendToUnity = onSendToUnity,
            onRecordPlay = onRecordPlay,
            onSetPreparingGame = onSetPreparingGame,
            onRefreshHomeContentStatuses = onRefreshHomeContentStatuses,
            log = { _, _ -> }
        )

    private fun game(id: String): GameCatalogEntry =
        GameCatalogEntry(
            id = id,
            released = true,
            nameZh = "游戏$id",
            nameEn = "Game $id",
            aliases = emptyList(),
            searchKeys = emptyList(),
            minPlayers = 1,
            maxPlayers = 4,
            tutorialTrack = "full",
            contentVersion = "v1"
        )

    private fun loadedContent(version: String = "v1"): LoadedLocalContent {
        val gameRoot = temp.newFolder("game-$version")
        val runtimeFile = File(gameRoot, "tutorial/full.runtime.json")
        runtimeFile.parentFile?.mkdirs()
        runtimeFile.writeText(
            """
            {
              "cues": [
                {
                  "id": "cue-1",
                  "start": 0.0,
                  "duration": 1.0,
                  "text": "hello",
                  "group_path": []
                }
              ]
            }
            """.trimIndent(),
            Charsets.UTF_8
        )
        val timeline = TutorialTimeline.load(gameRoot, "full")
        return LoadedLocalContent(
            version = version,
            versionRoot = File(gameRoot, "versions/$version"),
            timeline = timeline
        )
    }

    private class FakeLocalContentLoader(
        var result: LoadedLocalContent?
    ) : LocalContentLoader {
        override fun load(game: GameCatalogEntry): LoadedLocalContent? = result
    }
}
