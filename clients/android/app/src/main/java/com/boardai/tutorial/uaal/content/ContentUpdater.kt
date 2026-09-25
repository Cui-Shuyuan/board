package com.boardai.tutorial.uaal.content

import android.content.Context
import android.util.Log
import java.io.File
import java.io.FileOutputStream
import java.io.IOException
import java.net.HttpURLConnection
import java.net.URL
import java.net.URLEncoder
import java.nio.file.Files
import java.security.MessageDigest

/**
 * Downloads content manifests/files with SHA-256 verification and switches the
 * active version atomically.  Calls are blocking; MainActivity runs [update] on
 * a small background executor so playback never blocks.
 */
class ContentUpdater(
    context: Context,
    baseUrl: String,
    private val store: ContentStore
) {
    private val baseUrl: String = baseUrl.trimEnd('/')

    fun update(
        game: String,
        onStatus: (ContentUpdateStatus) -> Unit
    ): ContentUpdateResult {
        return try {
            Log.i(TAG, "检查更新")
            onStatus(ContentUpdateStatus.Checking)

            val manifest = fetchManifest(game)
            require(manifest.game == game) {
                "manifest game mismatch: expected '$game', got '${manifest.game}'"
            }

            val versionDirectory = store.versionDir(manifest.version)
            val active = store.readActive(game)

            // Already complete and active: nothing to do.
            if (active?.version == manifest.version &&
                store.isVersionComplete(manifest.version, game)
            ) {
                val status = ContentUpdateStatus.UpToDate(manifest.version)
                onStatus(status)
                return ContentUpdateResult(
                    status = status,
                    versionRoot = versionDirectory,
                    gameRoot = store.gameRoot(manifest.version, game),
                    changed = false
                )
            }

            // Complete version already on disk but active pointer is missing or
            // stale.  Only the pointer needs to switch.
            if (store.isVersionComplete(manifest.version, game)) {
                val activated = store.activate(game, manifest.version)
                cleanupQuietly(game)
                val status = ContentUpdateStatus.Updated(manifest.version)
                onStatus(status)
                return ContentUpdateResult(
                    status = status,
                    versionRoot = versionDirectory,
                    gameRoot = activated.root,
                    changed = true
                )
            }

            val partial = store.partialDir(manifest.version)
            if (partial.exists() && !partial.deleteRecursively()) {
                throw IOException("cannot clear partial directory: ${partial.absolutePath}")
            }
            if (!partial.mkdirs()) {
                throw IOException("cannot create partial directory: ${partial.absolutePath}")
            }

            val gamePartial = File(partial, game)
            if (!gamePartial.mkdirs() && !gamePartial.isDirectory) {
                throw IOException("cannot create game partial directory: ${gamePartial.absolutePath}")
            }

            val total = manifest.files.size
            val reusableIndex = store.buildReusableIndex(manifest.version, game)
            var completed = 0
            var reused = 0
            var downloaded = 0

            onStatus(ContentUpdateStatus.Downloading(manifest.version, completed, total, ""))

            for (file in manifest.files) {
                onStatus(
                    ContentUpdateStatus.Downloading(
                        manifest.version,
                        completed,
                        total,
                        file.path
                    )
                )

                val target = File(gamePartial, file.path)
                target.parentFile?.mkdirs()

                if (matches(file, target)) {
                    completed += 1
                    onStatus(
                        ContentUpdateStatus.Downloading(
                            manifest.version,
                            completed,
                            total,
                            file.path
                        )
                    )
                    continue
                }

                if (target.exists() && !target.delete()) {
                    throw IOException("cannot replace stale file: ${target.absolutePath}")
                }

                var installed = false
                val reusable = reusableIndex.find(file)
                if (reusable != null) {
                    installed = reuseFile(reusable, target) && matches(file, target)
                    if (installed) {
                        reused += 1
                    } else {
                        target.delete()
                    }
                }

                if (!installed) {
                    Log.i(TAG, "download ${file.path}")
                    val part = File(target.parentFile, "${target.name}.part")
                    if (part.exists()) part.delete()
                    try {
                        downloadFile(file, part)
                        if (!matches(file, part)) {
                            throw IOException("sha256 mismatch for ${file.path}")
                        }
                        if (!moveAtomically(part, target)) {
                            throw IOException("cannot move downloaded file into place: ${file.path}")
                        }
                    } catch (t: Throwable) {
                        part.delete()
                        throw t
                    }
                    downloaded += 1
                }

                completed += 1
                onStatus(
                    ContentUpdateStatus.Downloading(
                        manifest.version,
                        completed,
                        total,
                        file.path
                    )
                )
            }

            Log.i(TAG, "reused=$reused downloaded=$downloaded total=$total")

            store.writeCompleteMarker(manifest, game, partial)

            val finalDirectory = store.versionDir(manifest.version)
            if (finalDirectory.exists() && !finalDirectory.deleteRecursively()) {
                throw IOException("cannot clear incomplete version directory: ${finalDirectory.absolutePath}")
            }
            if (!moveAtomically(partial, finalDirectory)) {
                throw IOException("cannot atomically rename ${partial.name} to ${finalDirectory.name}")
            }

            val activated = store.activate(game, manifest.version)
            cleanupQuietly(game)
            Log.i(
                TAG,
                "active version=${manifest.version} root=${activated.root.absolutePath} " +
                    "files=${manifest.files.size}"
            )

            val status = ContentUpdateStatus.Updated(manifest.version)
            onStatus(status)
            ContentUpdateResult(
                status = status,
                versionRoot = finalDirectory,
                gameRoot = activated.root,
                changed = true
            )
        } catch (t: Throwable) {
            val message = t.message?.takeIf { it.isNotBlank() }
                ?: t.javaClass.simpleName
            val status = ContentUpdateStatus.Failed(message)
            onStatus(status)
            ContentUpdateResult(status = status, error = t)
        }
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

    private fun downloadFile(file: ContentFile, destination: File) {
        val connection = (resolveUrl(file.url).openConnection() as HttpURLConnection).apply {
            requestMethod = "GET"
            connectTimeout = 15_000
            readTimeout = 60_000
            instanceFollowRedirects = true
            setRequestProperty("Accept-Encoding", "identity")
        }

        try {
            val code = connection.responseCode
            if (code !in 200..299) {
                throw IOException("HTTP $code downloading ${file.path}")
            }

            destination.parentFile?.mkdirs()
            val digest = MessageDigest.getInstance("SHA-256")

            connection.inputStream.use { input ->
                FileOutputStream(destination).use { output ->
                    val buffer = ByteArray(64 * 1024)
                    while (true) {
                        val read = input.read(buffer)
                        if (read < 0) break
                        digest.update(buffer, 0, read)
                        output.write(buffer, 0, read)
                    }
                }
            }

            val actual = digest.digest().toHex()
            if (!actual.equals(file.sha256, ignoreCase = true)) {
                destination.delete()
                throw IOException("sha256 mismatch for ${file.path}")
            }
        } finally {
            connection.disconnect()
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

    private companion object {
        const val TAG = "BoardAI-Content"
    }
}
