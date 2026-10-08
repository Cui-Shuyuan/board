package com.boardai.tutorial.uaal.content

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder
import java.io.File
import java.security.MessageDigest

class ContentStoreCleanupTest {

    @get:Rule
    val temp = TemporaryFolder()

    private lateinit var store: ContentStore

    private val gameA = "game-a"
    private val gameB = "game-b"
    private val versionA = "aaaaaaaa"
    private val versionB = "bbbbbbbb"

    @Test
    fun cleanupForGameADoesNotDeleteLegacyGameBVersion() {
        store = ContentStore.forTesting(temp.newFolder("content-root"))
        writeLegacyComplete(gameA, versionA, "alpha.bin", "alpha")
        writeLegacyComplete(gameB, versionB, "bravo.bin", "bravo")
        assertNotNull(store.readActiveValid(gameA))
        assertNotNull(store.readActiveValid(gameB))

        store.cleanupOldVersions(gameA, keep = 2)

        assertNotNull(store.readActiveValid(gameB))
        assertTrue(store.isVersionComplete(versionB, gameB))
        assertTrue(store.versionDir(versionB).isDirectory)
        assertEquals(
            "bravo",
            File(store.gameRoot(versionB, gameB), "bravo.bin").readText()
        )
    }

    @Test
    fun cleanupForGameAKeepsActiveAndMostRecentOwnVersion() {
        store = ContentStore.forTesting(temp.newFolder("content-root"))
        val oldest = "11111111"
        val previous = "22222222"
        val active = "33333333"

        writeScopedComplete(gameA, oldest, "oldest.bin", "oldest")
        writeScopedComplete(gameA, previous, "previous.bin", "previous")
        writeScopedComplete(gameA, active, "active.bin", "active")
        store.activate(gameA, active)
        store.versionDir(oldest, gameA).setLastModified(1_000L)
        store.versionDir(previous, gameA).setLastModified(2_000L)
        store.versionDir(active, gameA).setLastModified(3_000L)

        store.cleanupOldVersions(gameA, keep = 2)

        assertFalse(store.versionDir(oldest, gameA).exists())
        assertTrue(store.versionDir(previous, gameA).isDirectory)
        assertTrue(store.versionDir(active, gameA).isDirectory)
        assertEquals(active, store.readActiveValid(gameA)?.version)
    }

    @Test
    fun cleanupForGameADoesNotDeleteGameBCompletePartialOrActive() {
        store = ContentStore.forTesting(temp.newFolder("content-root"))
        val bPausedVersion = "cccccccc"

        writeScopedComplete(gameA, versionA, "alpha.bin", "alpha")
        store.activate(gameA, versionA)
        writeScopedComplete(gameB, versionB, "bravo.bin", "bravo")
        store.activate(gameB, versionB)
        writeScopedPartial(gameB, bPausedVersion, "pending.bin", "partial")

        store.cleanupOldVersions(gameA, keep = 2)

        assertNotNull(store.readActiveValid(gameB))
        assertTrue(store.isVersionComplete(versionB, gameB))
        assertEquals(
            "bravo",
            File(store.gameRoot(versionB, gameB), "bravo.bin").readText()
        )
        assertTrue(store.partialDir(bPausedVersion, gameB).isDirectory)
        assertTrue(
            File(store.partialGameDir(bPausedVersion, gameB), "pending.bin.part").isFile
        )
    }

    @Test
    fun deleteStalePartialsForGameADoesNotDeleteGameBPartial() {
        store = ContentStore.forTesting(temp.newFolder("content-root"))
        val bPausedVersion = "cccccccc"

        writeScopedComplete(gameA, versionA, "alpha.bin", "alpha")
        store.activate(gameA, versionA)
        writeScopedPartial(gameB, bPausedVersion, "pending.bin", "partial")

        store.deleteStalePartials(gameA, currentVersion = "dddddddd")

        assertTrue(store.partialDir(bPausedVersion, gameB).isDirectory)
        assertTrue(
            File(store.partialGameDir(bPausedVersion, gameB), "pending.bin.part").isFile
        )
    }

    @Test
    fun activatingGameADoesNotAffectGameBActivePointer() {
        store = ContentStore.forTesting(temp.newFolder("content-root"))
        val newerA = "cccccccc"

        writeScopedComplete(gameA, versionA, "alpha.bin", "alpha")
        store.activate(gameA, versionA)
        writeScopedComplete(gameB, versionB, "bravo.bin", "bravo")
        store.activate(gameB, versionB)
        writeScopedComplete(gameA, newerA, "newer.bin", "newer")

        store.activate(gameA, newerA)

        assertEquals(newerA, store.readActive(gameA)?.version)
        assertNotNull(store.readActiveValid(gameB))
        assertEquals(versionB, store.readActiveValid(gameB)?.version)
        assertEquals(
            "bravo",
            File(store.gameRoot(versionB, gameB), "bravo.bin").readText()
        )
    }

    @Test
    fun deleteLocalContentForGameADoesNotDeleteGameBContent() {
        store = ContentStore.forTesting(temp.newFolder("content-root"))
        val bPausedVersion = "cccccccc"

        writeScopedComplete(gameA, versionA, "alpha.bin", "alpha")
        store.activate(gameA, versionA)
        writeScopedComplete(gameB, versionB, "bravo.bin", "bravo")
        store.activate(gameB, versionB)
        writeScopedPartial(gameB, bPausedVersion, "pending.bin", "partial")

        store.deleteLocalContent(gameA)

        assertNull(store.readActive(gameA))
        assertNotNull(store.readActiveValid(gameB))
        assertEquals(versionB, store.readActiveValid(gameB)?.version)
        assertTrue(store.isVersionComplete(versionB, gameB))
        assertEquals(
            "bravo",
            File(store.gameRoot(versionB, gameB), "bravo.bin").readText()
        )
        assertTrue(store.partialDir(bPausedVersion, gameB).isDirectory)
    }

    @Test
    fun cleanupForGameADoesNotDeleteSameVersionIdOwnedByGameB() {
        store = ContentStore.forTesting(temp.newFolder("content-root"))
        val sharedVersion = "abababab"
        val previousA = "cdcdcdcd"
        val activeA = "efefefef"

        writeScopedComplete(gameA, sharedVersion, "old.bin", "old-a")
        writeScopedComplete(gameA, previousA, "previous.bin", "previous-a")
        writeScopedComplete(gameA, activeA, "active.bin", "active-a")
        store.activate(gameA, activeA)
        store.versionDir(sharedVersion, gameA).setLastModified(1_000L)
        store.versionDir(previousA, gameA).setLastModified(2_000L)
        store.versionDir(activeA, gameA).setLastModified(3_000L)

        writeScopedComplete(gameB, sharedVersion, "bravo.bin", "bravo-b")
        store.activate(gameB, sharedVersion)

        store.cleanupOldVersions(gameA, keep = 2)

        assertFalse(store.versionDir(sharedVersion, gameA).exists())
        assertTrue(store.versionDir(previousA, gameA).isDirectory)
        assertTrue(store.versionDir(activeA, gameA).isDirectory)
        assertNotNull(store.readActiveValid(gameB))
        assertEquals(sharedVersion, store.readActiveValid(gameB)?.version)
        assertEquals(
            "bravo-b",
            File(store.gameRoot(sharedVersion, gameB), "bravo.bin").readText()
        )
    }

    @Test
    fun cleanupForGameASkipsSharedLegacyVersionDirectory() {
        store = ContentStore.forTesting(temp.newFolder("content-root"))
        val sharedVersion = "dddddddd"
        val legacyRoot = store.versionDir(sharedVersion)
        writeLegacyComplete(gameA, sharedVersion, "alpha.bin", "alpha")
        File(legacyRoot, gameB).mkdirs()
        File(File(legacyRoot, gameB), "bravo.bin").writeText("bravo")
        store.writeActive(gameB, sharedVersion, File(legacyRoot, gameB))

        store.cleanupOldVersions(gameA, keep = 2)

        assertTrue(legacyRoot.isDirectory)
        assertTrue(File(File(legacyRoot, gameA), "alpha.bin").isFile)
        assertTrue(File(File(legacyRoot, gameB), "bravo.bin").isFile)
        assertEquals(sharedVersion, store.readActive(gameB)?.version)
    }

    private fun writeScopedComplete(
        game: String,
        version: String,
        path: String,
        content: String
    ) {
        val versionDirectory = store.versionDir(version, game)
        val gameDirectory = File(versionDirectory, game)
        gameDirectory.mkdirs()
        File(gameDirectory, path).writeText(content)

        store.writeCompleteMarker(manifest(game, version, path, content), game, versionDirectory)
        store.writeActive(game, version, gameDirectory)
    }

    private fun writeScopedPartial(
        game: String,
        version: String,
        path: String,
        content: String
    ) {
        store.writePausedProgress(
            version = version,
            game = game,
            completedFiles = 0,
            totalFiles = 1,
            bytesCompleted = content.toByteArray().size.toLong(),
            totalBytes = content.toByteArray().size.toLong(),
            currentPath = path
        )
        val gamePartial = store.partialGameDir(version, game)
        gamePartial.mkdirs()
        File(gamePartial, "$path.part").writeText(content)
    }

    private fun writeLegacyComplete(
        game: String,
        version: String,
        path: String,
        content: String
    ) {
        val versionDirectory = store.versionDir(version)
        val gameDirectory = File(versionDirectory, game)
        gameDirectory.mkdirs()
        File(gameDirectory, path).writeText(content)

        store.writeCompleteMarker(manifest(game, version, path, content), game, versionDirectory)
        store.writeActive(game, version, gameDirectory)
    }

    private fun manifest(
        game: String,
        version: String,
        path: String,
        content: String
    ): ContentManifest =
        ContentManifest(
            schema = ContentManifest.SCHEMA,
            game = game,
            version = version,
            files = listOf(
                ContentFile(
                    path = path,
                    size = content.toByteArray().size.toLong(),
                    sha256 = sha256Hex(content.toByteArray()),
                    url = "files/$path"
                )
            )
        )

    private fun sha256Hex(bytes: ByteArray): String =
        MessageDigest.getInstance("SHA-256")
            .digest(bytes)
            .joinToString(separator = "") { byte -> "%02x".format(byte.toInt() and 0xff) }
}
