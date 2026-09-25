package com.boardai.tutorial.uaal.content

import android.content.Context
import org.json.JSONArray
import org.json.JSONObject
import java.io.File
import java.io.IOException
import java.nio.file.AtomicMoveNotSupportedException
import java.nio.file.Files
import java.nio.file.StandardCopyOption

data class ActiveContent(
    val game: String,
    val version: String,
    val root: File
)

/**
 * Local content repository rooted at:
 *
 *   context.getExternalFilesDir(null)/board-content/
 *
 * Layout:
 *   active.json
 *   versions/{version}/complete.json
 *   versions/{version}/{game}/...
 */
class ContentStore(context: Context) {
    private val externalFilesDir: File = context.getExternalFilesDir(null)
        ?: context.filesDir

    val contentRoot: File = File(externalFilesDir, "board-content")
    val versionsDir: File = File(contentRoot, "versions")
    val activeFile: File = File(contentRoot, "active.json")

    init {
        contentRoot.mkdirs()
        versionsDir.mkdirs()
    }

    fun versionDir(version: String): File = File(versionsDir, version)

    fun partialDir(version: String): File = File(versionsDir, "$version.partial")

    fun gameRoot(version: String, game: String): File = File(versionDir(version), game)

    fun completeMarker(version: String): File = File(versionDir(version), COMPLETE_MARKER)

    fun isVersionComplete(version: String, game: String? = null): Boolean {
        val directory = versionDir(version)
        if (!directory.isDirectory) return false

        val marker = completeMarker(version)
        if (!marker.isFile) return false

        return try {
            val json = JSONObject(marker.readText(Charsets.UTF_8))
            if (json.optString("version", "") != version) return false
            if (game != null) {
                if (json.optString("game", "") != game) return false
                if (!File(directory, game).isDirectory) return false
            }
            true
        } catch (_: Exception) {
            false
        }
    }

    fun writeCompleteMarker(
        manifest: ContentManifest,
        game: String,
        directory: File = versionDir(manifest.version)
    ) {
        if (!directory.isDirectory && !directory.mkdirs()) {
            throw IOException("cannot create version directory: ${directory.absolutePath}")
        }

        val files = JSONArray()
        for (file in manifest.files) {
            files.put(
                JSONObject()
                    .put("path", file.path)
                    .put("size", file.size)
                    .put("sha256", file.sha256)
            )
        }

        val marker = File(directory, COMPLETE_MARKER)
        marker.writeText(
            JSONObject()
                .put("schema", ContentManifest.SCHEMA)
                .put("game", game)
                .put("version", manifest.version)
                .put("completedAt", System.currentTimeMillis())
                .put("files", files)
                .toString(2),
            Charsets.UTF_8
        )
    }

    fun readActive(game: String): ActiveContent? {
        if (!activeFile.isFile) return null

        return try {
            val json = JSONObject(activeFile.readText(Charsets.UTF_8))
            val games = json.optJSONArray("games") ?: return null
            for (i in 0 until games.length()) {
                val entry = games.optJSONObject(i) ?: continue
                if (entry.optString("game", "") != game) continue

                val version = entry.optString("version", "")
                val root = entry.optString("root", "")
                if (version.isBlank() || root.isBlank()) continue
                return ActiveContent(game = game, version = version, root = File(root))
            }
            null
        } catch (_: Exception) {
            null
        }
    }

    fun activate(game: String, version: String): ActiveContent {
        val root = gameRoot(version, game)
        writeActive(game, version, root)
        return ActiveContent(game = game, version = version, root = root)
    }

    /**
     * Writes active.json via active.json.tmp + atomic rename.  If the process
     * dies before the rename, the previous active.json is untouched.
     */
    fun writeActive(game: String, version: String, root: File) {
        contentRoot.mkdirs()
        val document = if (activeFile.isFile) {
            try {
                JSONObject(activeFile.readText(Charsets.UTF_8))
            } catch (_: Exception) {
                JSONObject()
            }
        } else {
            JSONObject()
        }

        val games = document.optJSONArray("games") ?: JSONArray()
        val updated = JSONObject()
            .put("game", game)
            .put("version", version)
            .put("root", root.absolutePath)

        var replaced = false
        for (i in 0 until games.length()) {
            val existing = games.optJSONObject(i) ?: continue
            if (existing.optString("game", "") == game) {
                games.put(i, updated)
                replaced = true
                break
            }
        }
        if (!replaced) games.put(updated)
        document.put("games", games)

        val temp = File(contentRoot, "active.json.tmp")
        temp.writeText(document.toString(2), Charsets.UTF_8)
        if (!moveAtomically(temp, activeFile)) {
            temp.delete()
            throw IOException("failed to atomically replace active.json")
        }
    }

    /**
     * Finds an existing complete version that already contains a byte-identical
     * copy of [file].  This is what makes incremental updates possible: only
     * changed/new files need to be fetched from the server.
     */
    fun findReusableSource(targetVersion: String, game: String, file: ContentFile): File? {
        val candidates = versionsDir.listFiles()
            ?.filter {
                it.isDirectory &&
                    !it.name.endsWith(".partial") &&
                    it.name != targetVersion
            }
            ?.sortedByDescending { it.lastModified() }
            ?: return null

        for (versionDirectory in candidates) {
            val marker = File(versionDirectory, COMPLETE_MARKER)
            if (!marker.isFile) continue

            val markerJson = try {
                JSONObject(marker.readText(Charsets.UTF_8))
            } catch (_: Exception) {
                continue
            }

            if (markerJson.optString("version", "") != versionDirectory.name) continue
            if (markerJson.optString("game", "") != game) continue

            val recordedFiles = markerJson.optJSONArray("files") ?: continue
            var hashMatched = false
            for (i in 0 until recordedFiles.length()) {
                val recorded = recordedFiles.optJSONObject(i) ?: continue
                if (recorded.optString("path", "") == file.path &&
                    recorded.optString("sha256", "").equals(file.sha256, ignoreCase = true)
                ) {
                    hashMatched = true
                    break
                }
            }
            if (!hashMatched) continue

            val source = File(File(versionDirectory, game), file.path)
            if (source.isFile && source.length() == file.size) return source
        }

        return null
    }

    /**
     * Retains the active version plus the most recent previous complete
     * version.  Versions older than that are removed to avoid unbounded disk
     * growth.  Failure to clean up is intentionally non-fatal.
     */
    fun cleanupOldVersions(game: String, keep: Int = 2) {
        if (keep < 1) return
        val activeVersion = readActive(game)?.version
        val all = versionsDir.listFiles()
            ?.filter { it.isDirectory && !it.name.endsWith(".partial") }
            ?.sortedByDescending { it.lastModified() }
            ?: return

        val keepVersions = LinkedHashSet<String>()
        if (activeVersion != null) keepVersions += activeVersion
        for (directory in all) {
            if (keepVersions.size >= keep) break
            if (isVersionComplete(directory.name, game)) keepVersions += directory.name
        }

        for (directory in all) {
            if (directory.name !in keepVersions) {
                directory.deleteRecursively()
            }
        }
    }

    private companion object {
        const val COMPLETE_MARKER = "complete.json"
    }
}

/**
 * Same-directory rename is the atomic-switch primitive used for both version
 * directories and active.json.  Android external storage is normally a POSIX
 * filesystem, so rename either fully succeeds or leaves the original in place.
 */
internal fun moveAtomically(source: File, target: File): Boolean {
    if (!source.exists()) return false

    return try {
        Files.move(
            source.toPath(),
            target.toPath(),
            StandardCopyOption.ATOMIC_MOVE,
            StandardCopyOption.REPLACE_EXISTING
        )
        true
    } catch (_: AtomicMoveNotSupportedException) {
        try {
            Files.move(source.toPath(), target.toPath(), StandardCopyOption.REPLACE_EXISTING)
            true
        } catch (_: Exception) {
            false
        }
    } catch (_: Exception) {
        try {
            Files.move(source.toPath(), target.toPath(), StandardCopyOption.REPLACE_EXISTING)
            true
        } catch (_: Exception) {
            false
        }
    }
}
