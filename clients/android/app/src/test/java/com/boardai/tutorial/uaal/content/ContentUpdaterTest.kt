package com.boardai.tutorial.uaal.content

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder
import java.io.File
import java.io.IOException
import java.security.MessageDigest

class ContentUpdaterTest {

    @get:Rule
    val temp = TemporaryFolder()

    private lateinit var store: ContentStore

    private val game = "splendor"
    private val oldVersion = "aaaaaaaa"
    private val newVersion = "bbbbbbbb"

    @Before
    fun setUp() {
        store = ContentStore.forTesting(temp.newFolder("content-root"))
    }

    @Test
    fun freshDownloadInstallsAndActivates() {
        val contents = linkedMapOf(
            "tutorial.bin" to "alpha".toByteArray(),
            "nested/second.bin" to ByteArray(1024) { (it % 251).toByte() }
        )
        val fetcher = FakeContentFetcher(manifest(newVersion, contents), contents)

        val result = updater(fetcher).update(game, {})

        assertEquals(ContentUpdateStatus.Updated(newVersion), result.status)
        assertTrue(result.changed)
        assertFalse(store.partialDir(newVersion).exists())
        assertTrue(store.completeMarker(newVersion).isFile)
        assertTrue(store.gameRoot(newVersion, game).isDirectory)
        assertEquals(newVersion, store.readActive(game)?.version)
        assertEquals(listOf(game), fetcher.fetchedManifests)
        assertEquals(contents.keys.toList(), fetcher.downloadedPaths)
    }

    @Test
    fun alreadyUpToDateDoesNotDownloadAgain() {
        val contents = linkedMapOf(
            "tutorial.bin" to "alpha".toByteArray()
        )
        val fetcher = FakeContentFetcher(manifest(newVersion, contents), contents)
        val updater = updater(fetcher)

        assertEquals(ContentUpdateStatus.Updated(newVersion), updater.update(game, {}).status)
        val downloadsAfterFirstRun = fetcher.downloadedPaths.toList()

        val second = updater.update(game, {})

        assertEquals(ContentUpdateStatus.UpToDate(newVersion), second.status)
        assertEquals(downloadsAfterFirstRun, fetcher.downloadedPaths)
    }

    @Test
    fun pausedDownloadKeepsPartialFileAndActivePointer() {
        val contents = linkedMapOf(
            "first.bin" to "first".toByteArray(),
            "second.bin" to ByteArray(100) { it.toByte() }
        )
        val fetcher = FakeContentFetcher(manifest(newVersion, contents), contents).apply {
            pauseOnPath = "second.bin"
        }

        val result = updater(fetcher).update(game, {})

        val paused = result.status as ContentUpdateStatus.Paused
        assertEquals(newVersion, paused.version)
        assertEquals(1, paused.completedFiles)
        assertEquals(2, paused.totalFiles)
        assertTrue(store.partialDir(newVersion).isDirectory)
        val part = File(store.partialGameDir(newVersion, game), "second.bin.part")
        assertTrue(part.isFile)
        assertEquals(50L, part.length())
        assertNull(store.readActive(game))
    }

    @Test
    fun resumeSkipsAlreadyCompletedFiles() {
        val contents = linkedMapOf(
            "completed.bin" to ByteArray(128) { (it + 1).toByte() },
            "pending.bin" to ByteArray(64) { (it + 2).toByte() }
        )
        val manifest = manifest(newVersion, contents)
        val completedTarget = File(store.partialGameDir(newVersion, game), "completed.bin")
        completedTarget.parentFile?.mkdirs()
        completedTarget.writeBytes(contents.getValue("completed.bin"))

        val fetcher = FakeContentFetcher(manifest, contents)
        val result = updater(fetcher).update(game, {})

        assertEquals(ContentUpdateStatus.Updated(newVersion), result.status)
        assertEquals(listOf("pending.bin"), fetcher.downloadedPaths)
    }

    @Test
    fun staleOldVersionPartialIsRemoved() {
        val stalePartial = store.partialGameDir(oldVersion, game)
        File(stalePartial, "stale.bin").apply {
            parentFile?.mkdirs()
            writeBytes(byteArrayOf(1, 2, 3))
        }
        val contents = linkedMapOf(
            "fresh.bin" to "fresh".toByteArray()
        )
        val fetcher = FakeContentFetcher(manifest(newVersion, contents), contents)

        val result = updater(fetcher).update(game, {})

        assertEquals(ContentUpdateStatus.Updated(newVersion), result.status)
        assertFalse(store.partialDir(oldVersion).exists())
    }

    @Test
    fun failureKeepsExistingActiveVersion() {
        val oldContents = linkedMapOf(
            "old.bin" to "old-content".toByteArray()
        )
        val fetcher = FakeContentFetcher(manifest(oldVersion, oldContents), oldContents)
        val updater = updater(fetcher)
        assertEquals(ContentUpdateStatus.Updated(oldVersion), updater.update(game, {}).status)
        val oldRoot = store.gameRoot(oldVersion, game)
        assertTrue(oldRoot.isDirectory)

        val newContents = linkedMapOf(
            "new.bin" to "new-content".toByteArray()
        )
        fetcher.manifest = manifest(newVersion, newContents)
        fetcher.failWith = IOException("network down")

        val result = updater.update(game, {})

        assertEquals(ContentUpdateStatus.Failed("network down"), result.status)
        assertEquals(oldVersion, store.readActive(game)?.version)
        assertTrue(oldRoot.isDirectory)
        assertFalse(store.gameRoot(newVersion, game).exists())
    }

    @Test
    fun pauseRequestedBeforeFileLoopReturnsPausedWithoutDownloading() {
        val oldContents = linkedMapOf(
            "old.bin" to "old-content".toByteArray()
        )
        val fetcher = FakeContentFetcher(manifest(oldVersion, oldContents), oldContents)
        val updater = updater(fetcher)
        assertEquals(ContentUpdateStatus.Updated(oldVersion), updater.update(game, {}).status)
        val downloadsBeforePause = fetcher.downloadedPaths.toList()

        val newContents = linkedMapOf(
            "new.bin" to "new-content".toByteArray()
        )
        fetcher.manifest = manifest(newVersion, newContents)
        val control = DownloadControl().apply { requestPause() }

        val result = updater.update(game, {}, control)

        val paused = result.status as ContentUpdateStatus.Paused
        assertEquals(newVersion, paused.version)
        assertEquals(downloadsBeforePause, fetcher.downloadedPaths)
        assertFalse(store.partialGameDir(newVersion, game).exists())
        assertEquals(oldVersion, store.readActive(game)?.version)
    }

    private fun updater(fetcher: ContentFetcher): ContentUpdater =
        ContentUpdater("http://unused.invalid", store, fetcher)

    private fun manifest(
        version: String,
        contents: Map<String, ByteArray>
    ): ContentManifest {
        val files = contents.map { (path, bytes) ->
            ContentFile(
                path = path,
                size = bytes.size.toLong(),
                sha256 = sha256Hex(bytes),
                url = "files/$path"
            )
        }
        return ContentManifest(
            schema = ContentManifest.SCHEMA,
            game = game,
            version = version,
            files = files
        )
    }

    private fun sha256Hex(bytes: ByteArray): String =
        MessageDigest.getInstance("SHA-256")
            .digest(bytes)
            .joinToString(separator = "") { byte -> "%02x".format(byte.toInt() and 0xff) }

    private class FakeContentFetcher(
        var manifest: ContentManifest,
        private val contents: Map<String, ByteArray>
    ) : ContentFetcher {
        val fetchedManifests = mutableListOf<String>()
        val downloadedPaths = mutableListOf<String>()
        var failWith: Throwable? = null
        var pauseOnPath: String? = null

        override fun fetchManifest(game: String): ContentManifest {
            fetchedManifests += game
            return manifest
        }

        override fun downloadFile(
            file: ContentFile,
            target: File,
            control: DownloadControl,
            onBytesWritten: (Long) -> Unit
        ): ContentFileDownloadOutcome {
            failWith?.let { throw it }
            downloadedPaths += file.path

            val bytes = contents.getValue(file.path)
            val part = downloadPartFile(target)
            part.parentFile?.mkdirs()

            if (file.path == pauseOnPath) {
                val partialLength = if (bytes.isEmpty()) {
                    0
                } else {
                    (bytes.size / 2).coerceAtLeast(1)
                }
                part.writeBytes(bytes.copyOf(partialLength))
                onBytesWritten(partialLength.toLong())
                return ContentFileDownloadOutcome.PAUSED
            }

            part.writeBytes(bytes)
            onBytesWritten(bytes.size.toLong())
            return ContentFileDownloadOutcome.COMPLETED
        }
    }
}
