package com.boardai.tutorial.uaal.content

import com.boardai.tutorial.uaal.catalog.GameCatalogEntry
import com.boardai.tutorial.uaal.content.ActiveContent
import com.boardai.tutorial.uaal.content.ContentStatus
import com.boardai.tutorial.uaal.content.PausedContent
import com.boardai.tutorial.uaal.content.resolveContentStatus
import org.junit.Assert.assertEquals
import org.junit.Test
import java.io.File

class ContentStatusTest {

    @Test
    fun noServerVersionWithCompleteLocalContentIsInstalledOffline() {
        val source = FakeContentStatusSource(
            activeVersion = "v1",
            completeVersions = setOf("v1")
        )

        assertEquals(
            ContentStatus.InstalledOffline("v1"),
            resolveContentStatus(game(contentVersion = null), source)
        )
    }

    @Test
    fun noServerVersionWithoutLocalContentIsNoServerResource() {
        val source = FakeContentStatusSource()

        assertEquals(
            ContentStatus.NoServerResource,
            resolveContentStatus(game(contentVersion = null), source)
        )
    }

    @Test
    fun serverVersionWithoutLocalContentOrPartialIsNotDownloaded() {
        val source = FakeContentStatusSource()

        assertEquals(
            ContentStatus.NotDownloaded,
            resolveContentStatus(game(contentVersion = "v2"), source)
        )
    }

    @Test
    fun serverVersionWithoutLocalContentButMatchingPartialIsPaused() {
        val paused = paused(version = "v2")
        val source = FakeContentStatusSource(paused = paused)

        assertEquals(
            ContentStatus.Paused(paused),
            resolveContentStatus(game(contentVersion = "v2"), source)
        )
    }

    @Test
    fun completeLocalVersionMatchingServerIsInstalledCurrent() {
        val source = FakeContentStatusSource(
            activeVersion = "v2",
            completeVersions = setOf("v2")
        )

        assertEquals(
            ContentStatus.InstalledCurrent("v2"),
            resolveContentStatus(game(contentVersion = "v2"), source)
        )
    }

    @Test
    fun olderCompleteLocalVersionWithoutNewPartialIsUpdateAvailable() {
        val source = FakeContentStatusSource(
            activeVersion = "v1",
            completeVersions = setOf("v1")
        )

        assertEquals(
            ContentStatus.UpdateAvailable(localVersion = "v1", serverVersion = "v2"),
            resolveContentStatus(game(contentVersion = "v2"), source)
        )
    }

    @Test
    fun olderCompleteLocalVersionWithMatchingNewPartialIsUpdatePaused() {
        val localVersion = "v1"
        val serverVersion = "v2"
        val paused = paused(version = serverVersion)
        val source = FakeContentStatusSource(
            activeVersion = localVersion,
            completeVersions = setOf(localVersion),
            paused = paused
        )

        assertEquals(
            ContentStatus.UpdatePaused(
                localVersion = localVersion,
                serverVersion = serverVersion,
                progress = paused
            ),
            resolveContentStatus(game(contentVersion = serverVersion), source)
        )
    }

    @Test
    fun activePointerWithoutCompleteMarkerIsTreatedAsNotInstalled() {
        val source = FakeContentStatusSource(activeVersion = "v1")

        assertEquals(
            ContentStatus.NotDownloaded,
            resolveContentStatus(game(contentVersion = "v2"), source)
        )
    }

    @Test
    fun partialFromDifferentVersionIsNotPausedForFreshInstall() {
        val source = FakeContentStatusSource(paused = paused(version = "v1"))

        assertEquals(
            ContentStatus.NotDownloaded,
            resolveContentStatus(game(contentVersion = "v2"), source)
        )
    }

    @Test
    fun partialFromDifferentVersionIsNotUpdatePaused() {
        val source = FakeContentStatusSource(
            activeVersion = "v1",
            completeVersions = setOf("v1"),
            paused = paused(version = "v1")
        )

        assertEquals(
            ContentStatus.UpdateAvailable(localVersion = "v1", serverVersion = "v2"),
            resolveContentStatus(game(contentVersion = "v2"), source)
        )
    }

    private fun game(contentVersion: String?): GameCatalogEntry =
        GameCatalogEntry(
            id = GAME_ID,
            released = true,
            nameZh = "测试游戏",
            nameEn = "Test Game",
            aliases = emptyList(),
            searchKeys = emptyList(),
            minPlayers = 1,
            maxPlayers = 4,
            tutorialTrack = "full",
            contentVersion = contentVersion
        )

    private fun paused(version: String): PausedContent =
        PausedContent(
            game = GAME_ID,
            version = version,
            completedFiles = 1,
            totalFiles = 2,
            bytesCompleted = 10L,
            totalBytes = 20L,
            updatedAt = 1L
        )

    private class FakeContentStatusSource(
        private val activeVersion: String? = null,
        private val completeVersions: Set<String> = emptySet(),
        private val paused: PausedContent? = null
    ) : ContentStatusSource {
        override fun readActiveValid(gameId: String): ActiveContent? {
            val version = activeVersion ?: return null
            if (version !in completeVersions) return null
            return ActiveContent(
                game = gameId,
                version = version,
                root = File("/fake/$version/$gameId")
            )
        }

        override fun readPaused(gameId: String): PausedContent? =
            paused?.takeIf { it.game == gameId }

        override fun isVersionComplete(version: String, gameId: String?): Boolean =
            version in completeVersions

        override fun gameRoot(version: String, gameId: String): File =
            File("/fake/$version/$gameId")
    }

    private companion object {
        const val GAME_ID = "test-game"
    }
}
