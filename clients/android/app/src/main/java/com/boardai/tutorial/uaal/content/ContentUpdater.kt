package com.boardai.tutorial.uaal.content

import android.util.Log
import java.io.File
import java.io.IOException
import java.nio.file.Files
import kotlin.math.abs

/**
 * Downloads content manifests/files with SHA-256 verification and switches the
 * active version atomically.
 *
 * Calls are blocking; MainActivity runs [update] on a small background
 * executor so playback never blocks.  Downloads are resumable through
 * same-directory `.part` files and cooperative [DownloadControl] pausing.
 */
class ContentUpdater(
    baseUrl: String,
    private val store: ContentStore,
    private val fetcher: ContentFetcher = HttpContentFetcher(baseUrl)
) {
    private data class ResumeState(
        val completedPaths: Set<String>,
        val completedFiles: Int,
        val bytesCompleted: Long,
        val currentPath: String
    )

    private val nowMillis: () -> Long = { System.nanoTime() / 1_000_000 }

    fun update(
        game: String,
        onStatus: (ContentUpdateStatus) -> Unit,
        control: DownloadControl = DownloadControl()
    ): ContentUpdateResult {
        var version = ""
        var totalFiles = 0
        var totalBytes = 0L
        var completedFiles = 0
        var bytesCompleted = 0L
        var currentPath = ""
        var lastProgressAt = 0L
        var lastProgressBytes = -1L

        fun persistProgress(force: Boolean): Boolean {
            if (version.isBlank()) return false
            val now = nowMillis()
            if (!force &&
                now - lastProgressAt < PROGRESS_WRITE_INTERVAL_MS &&
                abs(bytesCompleted - lastProgressBytes) < PROGRESS_WRITE_BYTES
            ) {
                return false
            }

            store.writePausedProgress(
                version = version,
                game = game,
                completedFiles = completedFiles,
                totalFiles = totalFiles,
                bytesCompleted = bytesCompleted.coerceAtMost(totalBytes),
                totalBytes = totalBytes,
                currentPath = currentPath
            )
            lastProgressAt = now
            lastProgressBytes = bytesCompleted
            return true
        }

        fun emitDownloading() {
            onStatus(
                ContentUpdateStatus.Downloading(
                    version = version,
                    completedFiles = completedFiles,
                    totalFiles = totalFiles,
                    bytesCompleted = bytesCompleted.coerceAtMost(totalBytes),
                    totalBytes = totalBytes,
                    currentPath = currentPath
                )
            )
        }

        fun pausedResult(): ContentUpdateResult {
            if (version.isNotBlank()) persistProgress(force = true)
            val status = ContentUpdateStatus.Paused(
                version = version,
                completedFiles = completedFiles,
                totalFiles = totalFiles,
                bytesCompleted = bytesCompleted.coerceAtMost(totalBytes),
                totalBytes = totalBytes,
                currentPath = currentPath
            )
            onStatus(status)
            return ContentUpdateResult(
                status = status,
                versionRoot = version.takeIf { it.isNotBlank() }?.let(store::versionDir),
                gameRoot = version.takeIf { it.isNotBlank() }?.let { store.gameRoot(it, game) },
                changed = false
            )
        }

        return try {
            Log.i(TAG, "checking content for game=$game")
            onStatus(ContentUpdateStatus.Checking)

            val manifest = fetcher.fetchManifest(game)
            require(manifest.game == game) {
                "manifest game mismatch: expected '$game', got '${manifest.game}'"
            }

            version = manifest.version
            totalFiles = manifest.files.size
            totalBytes = manifest.files.sumOf { it.size }
            val versionDirectory = store.versionDir(version)
            val active = store.readActiveValid(game)

            // Already complete and active: nothing to do.
            if (active?.version == version &&
                store.isVersionComplete(version, game)
            ) {
                val status = ContentUpdateStatus.UpToDate(version)
                onStatus(status)
                return ContentUpdateResult(
                    status = status,
                    versionRoot = versionDirectory,
                    gameRoot = store.gameRoot(version, game),
                    changed = false
                )
            }

            // Complete version already on disk but active pointer is missing or
            // stale.  Only the pointer needs to switch.
            if (store.isVersionComplete(version, game)) {
                val activated = store.activate(game, version)
                cleanupQuietly(game)
                val status = ContentUpdateStatus.Updated(version)
                onStatus(status)
                return ContentUpdateResult(
                    status = status,
                    versionRoot = versionDirectory,
                    gameRoot = activated.root,
                    changed = true
                )
            }

            if (control.pauseRequested) return pausedResult()

            // A partial for an older server version is stale and must not be
            // mixed with the current manifest.
            store.deleteStalePartials(game, version)

            val partialDirectory = store.partialDir(version)
            val gamePartial = store.partialGameDir(version, game)
            if (!gamePartial.isDirectory && !gamePartial.mkdirs()) {
                throw IOException("cannot create partial game directory: ${gamePartial.absolutePath}")
            }

            val resume = scanResumeState(manifest, gamePartial)
            completedFiles = resume.completedFiles
            bytesCompleted = resume.bytesCompleted
            currentPath = resume.currentPath

            persistProgress(force = true)
            emitDownloading()
            if (control.pauseRequested) return pausedResult()

            val reusableIndex = store.buildReusableIndex(version, game)

            for (file in manifest.files) {
                if (control.pauseRequested) return pausedResult()
                if (file.path in resume.completedPaths) continue

                val target = File(gamePartial, file.path)
                target.parentFile?.mkdirs()
                if (target.exists() && !target.delete()) {
                    throw IOException("cannot replace stale file: ${target.absolutePath}")
                }

                val part = downloadPartFile(target)
                val partSizeBefore = if (part.isFile) {
                    part.length().coerceIn(0L, file.size)
                } else {
                    0L
                }
                val otherBytes = (bytesCompleted - partSizeBefore).coerceAtLeast(0L)

                currentPath = file.path
                emitDownloading()
                onStatus(
                    ContentUpdateStatus.Downloading(
                        version = version,
                        completedFiles = completedFiles,
                        totalFiles = totalFiles,
                        bytesCompleted = bytesCompleted.coerceAtMost(totalBytes),
                        totalBytes = totalBytes,
                        currentPath = currentPath
                    )
                )

                var installedFromOldVersion = false
                val reusable = reusableIndex.find(file)
                if (reusable != null) {
                    installedFromOldVersion = reuseFile(reusable, target) &&
                        contentMatches(file, target)
                    if (installedFromOldVersion) {
                        part.delete()
                    } else {
                        target.delete()
                    }
                }

                if (installedFromOldVersion) {
                    completedFiles += 1
                    bytesCompleted = (otherBytes + file.size).coerceAtMost(totalBytes)
                    persistProgress(force = true)
                    emitDownloading()
                    continue
                }

                var retriedAfterMismatch = false
                while (true) {
                    val outcome = fetcher.downloadFile(file, target, control) { written ->
                        bytesCompleted = (otherBytes + written).coerceAtMost(totalBytes)
                        if (persistProgress(force = false)) {
                            emitDownloading()
                        }
                    }

                    if (outcome == ContentFileDownloadOutcome.PAUSED) {
                        bytesCompleted = (
                            otherBytes + part.length().coerceIn(0L, file.size)
                            ).coerceAtMost(totalBytes)
                        return pausedResult()
                    }

                    if (contentMatches(file, part)) {
                        if (!moveAtomically(part, target)) {
                            throw IOException("cannot move downloaded file into place: ${file.path}")
                        }
                        break
                    }

                    part.delete()
                    if (retriedAfterMismatch) {
                        throw IOException("sha256 mismatch for ${file.path}")
                    }
                    retriedAfterMismatch = true
                }

                completedFiles += 1
                bytesCompleted = (otherBytes + file.size).coerceAtMost(totalBytes)
                persistProgress(force = true)
                emitDownloading()
            }

            onStatus(
                ContentUpdateStatus.Verifying(
                    version = version,
                    totalFiles = totalFiles,
                    completedFiles = completedFiles,
                    bytesCompleted = bytesCompleted,
                    totalBytes = totalBytes,
                    currentPath = currentPath
                )
            )
            store.writeCompleteMarker(manifest, game, partialDirectory)

            // The partial marker must never end up in the active version
            // directory.  Resume state is only relevant while downloading.
            store.progressFile(version).delete()
            File(partialDirectory, "progress.json.tmp").delete()

            onStatus(
                ContentUpdateStatus.Switching(
                    version = version,
                    completedFiles = completedFiles,
                    bytesCompleted = bytesCompleted,
                    totalBytes = totalBytes,
                    currentPath = currentPath
                )
            )
            val finalDirectory = store.versionDir(version)
            if (finalDirectory.exists() && !finalDirectory.deleteRecursively()) {
                throw IOException("cannot clear incomplete version directory: ${finalDirectory.absolutePath}")
            }
            if (!moveAtomically(partialDirectory, finalDirectory)) {
                throw IOException("cannot atomically rename ${partialDirectory.name} to ${finalDirectory.name}")
            }

            val activated = store.activate(game, version)
            cleanupQuietly(game)
            Log.i(
                TAG,
                "active version=$version root=${activated.root.absolutePath} files=${manifest.files.size}"
            )

            val status = ContentUpdateStatus.Updated(version)
            onStatus(status)
            ContentUpdateResult(
                status = status,
                versionRoot = finalDirectory,
                gameRoot = activated.root,
                changed = true
            )
        } catch (t: Throwable) {
            // A pause request must never surface as Failed.  The current `.part`
            // files stay in place either way.
            if (control.pauseRequested) {
                return pausedResult()
            }

            persistProgress(force = true)
            val message = t.message?.takeIf { it.isNotBlank() }
                ?: t.javaClass.simpleName
            val status = ContentUpdateStatus.Failed(message)
            onStatus(status)
            ContentUpdateResult(status = status, error = t)
        }
    }

    private fun scanResumeState(
        manifest: ContentManifest,
        gamePartial: File
    ): ResumeState {
        val completedPaths = HashSet<String>()
        var completedFiles = 0
        var bytesCompleted = 0L
        var currentPath = ""

        for (file in manifest.files) {
            val target = File(gamePartial, file.path)
            if (contentMatches(file, target)) {
                completedPaths += file.path
                completedFiles += 1
                bytesCompleted += file.size
                continue
            }

            if (target.exists()) target.delete()

            val part = downloadPartFile(target)
            if (part.isFile) {
                bytesCompleted += part.length().coerceIn(0L, file.size)
            }
            if (currentPath.isBlank()) currentPath = file.path
        }

        return ResumeState(
            completedPaths = completedPaths,
            completedFiles = completedFiles,
            bytesCompleted = bytesCompleted,
            currentPath = currentPath
        )
    }

    private fun cleanupQuietly(game: String) {
        try {
            store.cleanupOldVersions(game, keep = 2)
        } catch (_: Throwable) {
            // Cleanup is best-effort; a failure must never break playback.
        }
    }

    private fun reuseFile(source: File, target: File): Boolean {
        target.parentFile?.mkdirs()

        // Prefer a hard link: unchanged files are not copied byte-for-byte.
        try {
            Files.createLink(target.toPath(), source.toPath())
            return true
        } catch (_: Throwable) {
            // Fall through to a regular copy.
        }

        return try {
            source.copyTo(target, overwrite = true)
            true
        } catch (_: Throwable) {
            false
        }
    }

    private companion object {
        const val TAG = "BoardAI-Content"
        const val PROGRESS_WRITE_INTERVAL_MS = 500L
        const val PROGRESS_WRITE_BYTES = 1_500_000L
    }
}
