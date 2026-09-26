package com.boardai.tutorial.uaal.content

import android.content.Context
import android.os.SystemClock
import android.util.Log
import java.io.File
import java.io.FileOutputStream
import java.io.IOException
import java.net.HttpURLConnection
import java.net.URL
import java.net.URLEncoder
import java.nio.file.Files
import java.security.MessageDigest
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
    @Suppress("UNUSED_PARAMETER") context: Context,
    baseUrl: String,
    private val store: ContentStore
) {
    private val baseUrl: String = baseUrl.trimEnd('/')

    private enum class FileOutcome {
        COMPLETED,
        PAUSED
    }

    private data class ResumeState(
        val completedPaths: Set<String>,
        val completedFiles: Int,
        val bytesCompleted: Long,
        val currentPath: String
    )

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
            val now = SystemClock.elapsedRealtime()
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

            val manifest = fetchManifest(game)
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

                val part = partFile(target)
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
                    installedFromOldVersion = reuseFile(reusable, target) && matches(file, target)
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
                    val outcome = downloadFile(file, target, control) { written ->
                        bytesCompleted = (otherBytes + written).coerceAtMost(totalBytes)
                        if (persistProgress(force = false)) {
                            emitDownloading()
                        }
                    }

                    if (outcome == FileOutcome.PAUSED) {
                        bytesCompleted = (
                            otherBytes + part.length().coerceIn(0L, file.size)
                            ).coerceAtMost(totalBytes)
                        return pausedResult()
                    }

                    if (matches(file, part)) {
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
            if (matches(file, target)) {
                completedPaths += file.path
                completedFiles += 1
                bytesCompleted += file.size
                continue
            }

            if (target.exists()) target.delete()

            val part = partFile(target)
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

    private fun fetchManifest(game: String): ContentManifest {
        val encodedGame = URLEncoder.encode(game, Charsets.UTF_8.name())
        val connection = (URL("$baseUrl/api/content/games/$encodedGame/manifest").openConnection()
            as HttpURLConnection).apply {
            requestMethod = "GET"
            connectTimeout = 15_000
            readTimeout = 30_000
            instanceFollowRedirects = true
            setRequestProperty("Accept", "application/json")
        }

        try {
            val code = connection.responseCode
            if (code !in 200..299) {
                val detail = connection.errorStream
                    ?.bufferedReader(Charsets.UTF_8)
                    ?.use { it.readText() }
                    .orEmpty()
                throw IOException("HTTP $code from manifest endpoint: $detail")
            }

            val body = connection.inputStream
                .bufferedReader(Charsets.UTF_8)
                .use { it.readText() }
            return ContentManifest.parse(body)
        } finally {
            connection.disconnect()
        }
    }

    /**
     * Downloads one file into `target.name + ".part"`.  Existing partial data is
     * reused through HTTP Range.  The file is NOT renamed to `target` here; the
     * caller verifies SHA-256 first.
     */
    private fun downloadFile(
        file: ContentFile,
        target: File,
        control: DownloadControl,
        onBytesWritten: (Long) -> Unit
    ): FileOutcome {
        val part = partFile(target)
        part.parentFile?.mkdirs()
        var offset = if (part.isFile) part.length() else 0L
        if (offset > 0L) {
            Log.i(TAG, "resume offset=$offset for ${file.path}")
        }

        if (offset == file.size) {
            if (matches(file, part)) {
                // The caller performs verification and the atomic move so that
                // a fully-written `.part` is handled exactly once.
                onBytesWritten(file.size)
                return FileOutcome.COMPLETED
            }
            part.delete()
            offset = 0L
        } else if (offset > file.size) {
            part.delete()
            offset = 0L
        }

        var retriedAfter416 = false
        while (true) {
            val connection = (resolveUrl(file.url).openConnection() as HttpURLConnection).apply {
                requestMethod = "GET"
                connectTimeout = 15_000
                readTimeout = 60_000
                instanceFollowRedirects = true
                setRequestProperty("Accept-Encoding", "identity")
                if (offset > 0L) {
                    setRequestProperty("Range", "bytes=$offset-")
                }
            }

            try {
                val code = connection.responseCode
                when {
                    code == 416 -> {
                        connection.disconnect()
                        if (retriedAfter416) {
                            throw IOException("HTTP 416 downloading ${file.path}")
                        }
                        retriedAfter416 = true
                        part.delete()
                        offset = 0L
                        continue
                    }
                    code == 200 && offset > 0L -> {
                        // Server ignored Range or content changed.  Do not append
                        // to an unknown partial: start this file over once.
                        connection.disconnect()
                        part.delete()
                        offset = 0L
                        continue
                    }
                    code !in 200..299 -> {
                        throw IOException("HTTP $code downloading ${file.path}")
                    }
                    code == 206 -> {
                        if (offset <= 0L) {
                            throw IOException("unexpected HTTP 206 without range for ${file.path}")
                        }
                        val contentRange = connection.getHeaderField("Content-Range")
                        val start = parseContentRangeStart(contentRange)
                            ?: throw IOException("missing/invalid Content-Range for ${file.path}: $contentRange")
                        if (start != offset) {
                            throw IOException(
                                "Content-Range start mismatch for ${file.path}: " +
                                    "expected $offset, got $start"
                            )
                        }
                    }
                }

                val append = code == 206 && offset > 0L
                var written = offset
                connection.inputStream.use { input ->
                    FileOutputStream(part, append).use { output ->
                        val buffer = ByteArray(64 * 1024)
                        while (true) {
                            if (control.pauseRequested) {
                                output.flush()
                                return FileOutcome.PAUSED
                            }

                            val read = input.read(buffer)
                            if (read < 0) break
                            output.write(buffer, 0, read)
                            written += read
                            onBytesWritten(written)

                            if (control.pauseRequested) {
                                output.flush()
                                return FileOutcome.PAUSED
                            }
                        }
                    }
                }

                return FileOutcome.COMPLETED
            } finally {
                connection.disconnect()
            }
        }
    }

    private fun resolveUrl(raw: String): URL {
        val value = raw.trim()
        return if (value.startsWith("http://", ignoreCase = true) ||
            value.startsWith("https://", ignoreCase = true)
        ) {
            URL(value)
        } else {
            URL("$baseUrl/${value.trimStart('/')}")
        }
    }

    private fun matches(file: ContentFile, path: File): Boolean {
        if (!path.isFile || path.length() != file.size) return false
        return sha256(path).equals(file.sha256, ignoreCase = true)
    }

    private fun sha256(path: File): String {
        val digest = MessageDigest.getInstance("SHA-256")
        path.inputStream().use { input ->
            val buffer = ByteArray(64 * 1024)
            while (true) {
                val read = input.read(buffer)
                if (read < 0) break
                digest.update(buffer, 0, read)
            }
        }
        return digest.digest().toHex()
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

    private fun ByteArray.toHex(): String =
        joinToString(separator = "") { byte -> "%02x".format(byte.toInt() and 0xff) }

    private fun parseContentRangeStart(value: String?): Long? {
        if (value.isNullOrBlank()) return null
        val match = CONTENT_RANGE_RE.find(value.trim()) ?: return null
        return match.groupValues[1].toLongOrNull()
    }

    private fun partFile(target: File): File =
        File(target.parentFile, "${target.name}.part")

    private companion object {
        const val TAG = "BoardAI-Content"
        const val PROGRESS_WRITE_INTERVAL_MS = 500L
        const val PROGRESS_WRITE_BYTES = 1_500_000L
        val CONTENT_RANGE_RE = Regex(
            "^bytes\\s+(\\d+)-(\\d+)/(\\d+|\\*)$",
            RegexOption.IGNORE_CASE
        )
    }
}
