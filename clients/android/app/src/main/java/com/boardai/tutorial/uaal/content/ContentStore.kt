package com.boardai.tutorial.uaal.content

import android.content.Context
import android.util.Log
import org.json.JSONArray
import org.json.JSONObject
import java.io.File
import java.io.IOException
import java.util.Locale
import java.nio.file.AtomicMoveNotSupportedException
import java.nio.file.Files
import java.nio.file.StandardCopyOption

data class ActiveContent(
    val game: String,
    val version: String,
    val root: File
)

data class PausedContent(
    val game: String,
    val version: String,
    val completedFiles: Int = 0,
    val totalFiles: Int = 0,
    val bytesCompleted: Long = 0L,
    val totalBytes: Long = 0L,
    val currentPath: String = "",
    val updatedAt: Long = 0L
) {
    val percent: Int
        get() {
            if (totalBytes > 0L) {
                return ((bytesCompleted * 100L) / totalBytes)
                    .toInt()
                    .coerceIn(0, 100)
            }
            if (totalFiles > 0) {
                return ((completedFiles * 100L) / totalFiles)
                    .toInt()
                    .coerceIn(0, 100)
            }
            return 0
        }
}

/**
 * Local content repository rooted at:
 *
 *   context.filesDir/board-content/
 *
 * Layout:
 *   active.json
 *   versions/{version}/complete.json
 *   versions/{version}/{game}/...
 *   versions/{version}.partial/{game}/...
 *   versions/{version}.partial/progress.json
 *
 * The external app-specific directory used by older builds is deleted once on
 * construction.  Nothing in this class reads from or writes to external storage.
 */
class ContentStore private constructor(
    val contentRoot: File
) : HomeContentStore {
    val versionsDir: File = File(contentRoot, "versions")
    val activeFile: File = File(contentRoot, "active.json")

    constructor(context: Context) : this(
        File(context.filesDir, "board-content")
    ) {
        deleteStaleExternalRoot(context)
    }

    fun versionDir(version: String): File = File(versionsDir, version)

    fun partialDir(version: String): File = File(versionsDir, "$version.partial")

    fun partialGameDir(version: String, game: String): File = File(partialDir(version), game)

    fun progressFile(version: String): File = File(partialDir(version), PROGRESS_MARKER)

    override fun gameRoot(version: String, gameId: String): File = File(versionDir(version), gameId)

    fun completeMarker(version: String): File = File(versionDir(version), COMPLETE_MARKER)

    override fun isVersionComplete(version: String, gameId: String?): Boolean {
        val directory = versionDir(version)
        if (!directory.isDirectory) return false

        val marker = completeMarker(version)
        if (!marker.isFile) return false

        return try {
            val json = JSONObject(marker.readText(Charsets.UTF_8))
            if (json.optString("version", "") != version) return false
            if (gameId != null) {
                if (json.optString("game", "") != gameId) return false
                if (!File(directory, gameId).isDirectory) return false
            }
            true
        } catch (_: Exception) {
            false
        }
    }

    /** Active pointer plus the complete marker and real game directory. */
    override fun readActiveValid(gameId: String): ActiveContent? {
        val active = readActive(gameId) ?: return null
        if (!isVersionComplete(active.version, gameId)) return null
        val root = gameRoot(active.version, gameId)
        if (!root.isDirectory) return null
        return ActiveContent(game = gameId, version = active.version, root = root)
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

    fun clearActive(game: String) {
        if (!activeFile.isFile) return

        val document = try {
            JSONObject(activeFile.readText(Charsets.UTF_8))
        } catch (_: Exception) {
            return
        }
        val games = document.optJSONArray("games") ?: return
        val kept = JSONArray()
        for (i in 0 until games.length()) {
            val entry = games.optJSONObject(i) ?: continue
            if (entry.optString("game", "") == game) continue
            kept.put(entry)
        }
        document.put("games", kept)

        val temp = File(contentRoot, "active.json.tmp")
        temp.writeText(document.toString(2), Charsets.UTF_8)
        if (!moveAtomically(temp, activeFile)) {
            temp.delete()
            throw IOException("failed to atomically replace active.json")
        }
    }

    /**
     * Reads the newest progress marker for [game].  A corrupt marker is ignored;
     * if a partial game directory still exists, a zero-progress marker is
     * returned so the UI still reports PAUSED and the `.part` files can resume.
     */
    override fun readPaused(gameId: String): PausedContent? {
        val directories = versionsDir.listFiles()
            ?.filter { it.isDirectory && it.name.endsWith(PARTIAL_SUFFIX) }
            ?: return null

        var best: PausedContent? = null
        for (directory in directories) {
            val version = directory.name.removeSuffix(PARTIAL_SUFFIX)
            if (version.isBlank()) continue

            val marker = readProgressMarker(directory)
            val candidate = when {
                marker?.game == gameId -> marker.copy(version = version)
                File(directory, gameId).isDirectory -> PausedContent(
                    game = gameId,
                    version = version,
                    updatedAt = directory.lastModified()
                )
                else -> null
            }

            if (candidate != null && (best == null || candidate.updatedAt >= best.updatedAt)) {
                best = candidate
            }
        }
        return best
    }

    /**
     * Writes progress.json via progress.json.tmp + atomic rename.  This is
     * intentionally best-effort: the real `.part` files remain the source of
     * truth for resume offsets.
     */
    fun writePausedProgress(
        version: String,
        game: String,
        completedFiles: Int,
        totalFiles: Int,
        bytesCompleted: Long,
        totalBytes: Long,
        currentPath: String
    ): PausedContent? {
        val partial = partialDir(version)
        if (!partial.isDirectory && !partial.mkdirs()) return null

        val updatedAt = System.currentTimeMillis()
        val payload = JSONObject()
            .put("schema", PROGRESS_SCHEMA)
            .put("game", game)
            .put("version", version)
            .put("completedFiles", completedFiles.coerceAtLeast(0))
            .put("totalFiles", totalFiles.coerceAtLeast(0))
            .put("bytesCompleted", bytesCompleted.coerceAtLeast(0L))
            .put("totalBytes", totalBytes.coerceAtLeast(0L))
            .put("currentPath", currentPath)
            .put("updatedAt", updatedAt)

        return try {
            val temp = File(partial, "$PROGRESS_MARKER.tmp")
            temp.writeText(payload.toString(), Charsets.UTF_8)
            if (!moveAtomically(temp, progressFile(version))) {
                temp.delete()
                return null
            }
            PausedContent(
                game = game,
                version = version,
                completedFiles = completedFiles.coerceAtLeast(0),
                totalFiles = totalFiles.coerceAtLeast(0),
                bytesCompleted = bytesCompleted.coerceAtLeast(0L),
                totalBytes = totalBytes.coerceAtLeast(0L),
                currentPath = currentPath,
                updatedAt = updatedAt
            )
        } catch (_: Exception) {
            null
        }
    }

    /** Deletes partial directories/markers belonging to [game]. */
    override fun deletePaused(gameId: String) {
        val directories = versionsDir.listFiles()
            ?.filter { it.isDirectory && it.name.endsWith(PARTIAL_SUFFIX) }
            ?: return

        for (directory in directories) {
            val marker = readProgressMarker(directory)
            val gameDir = File(directory, gameId)
            if (marker?.game != gameId && !gameDir.isDirectory) continue

            if (gameDir.isDirectory) gameDir.deleteRecursively()
            if (marker?.game == gameId) {
                File(directory, PROGRESS_MARKER).delete()
            }
            File(directory, "$PROGRESS_MARKER.tmp").delete()
            if (directory.listFiles().isNullOrEmpty()) directory.delete()
        }
    }

    /** Removes partials for [game] whose version is not [currentVersion]. */
    fun deleteStalePartials(game: String, currentVersion: String) {
        val directories = versionsDir.listFiles()
            ?.filter { it.isDirectory && it.name.endsWith(PARTIAL_SUFFIX) }
            ?: return

        val keepName = "$currentVersion$PARTIAL_SUFFIX"
        for (directory in directories) {
            if (directory.name == keepName) continue
            val marker = readProgressMarker(directory)
            val gameDir = File(directory, game)
            if (marker?.game != game && !gameDir.isDirectory) continue

            if (gameDir.isDirectory) gameDir.deleteRecursively()
            if (marker?.game == game) File(directory, PROGRESS_MARKER).delete()
            File(directory, "$PROGRESS_MARKER.tmp").delete()
            if (directory.listFiles().isNullOrEmpty()) directory.delete()
        }
    }

    /**
     * Deletes every complete version for [game], all of its partials and its
     * active pointer.  No other game's files are touched.
     */
    override fun deleteLocalContent(gameId: String) {
        val versionDirectories = versionsDir.listFiles()
            ?.filter { it.isDirectory && !it.name.endsWith(PARTIAL_SUFFIX) }
            ?: emptyList()

        for (directory in versionDirectories) {
            val markerGame = readCompleteMarkerGame(directory)
            if (markerGame != gameId && !File(directory, gameId).isDirectory) continue
            directory.deleteRecursively()
        }

        deletePaused(gameId)
        clearActive(gameId)
    }

    /**
     * Builds the reuse lookup once per update run.  Each old version's
     * complete.json is read and parsed exactly once, instead of once per
     * manifest file.
     *
     * Only complete version directories for [game] are considered.  The target
     * version and any *.partial directory are skipped.  A recorded reuse source
     * is kept only when it still exists and its size matches complete.json.
     */
    fun buildReusableIndex(targetVersion: String, game: String): ReusableContentIndex {
        val byPath = HashMap<String, MutableMap<String, File>>()

        val candidates = versionsDir.listFiles()
            ?.asSequence()
            ?.filter {
                it.isDirectory &&
                    !it.name.endsWith(PARTIAL_SUFFIX) &&
                    it.name != targetVersion
            }
            ?.sortedByDescending { it.lastModified() }
            ?.toList()
            ?: emptyList()

        for (versionDirectory in candidates) {
            val marker = File(versionDirectory, COMPLETE_MARKER)
            if (!marker.isFile) continue

            // Each old version is parsed at most once during the whole update.
            val markerJson = try {
                JSONObject(marker.readText(Charsets.UTF_8))
            } catch (_: Exception) {
                continue
            }

            if (markerJson.optString("version", "") != versionDirectory.name) continue
            if (markerJson.optString("game", "") != game) continue

            val recordedFiles = markerJson.optJSONArray("files") ?: continue
            for (i in 0 until recordedFiles.length()) {
                val recorded = recordedFiles.optJSONObject(i) ?: continue
                val path = recorded.optString("path", "")
                if (!ContentManifest.isSafeRelativePath(path)) continue

                val sha256 = recorded.optString("sha256", "")
                if (sha256.isBlank()) continue

                val recordedSize = recorded.optLong("size", -1L)
                if (recordedSize < 0L) continue

                val source = File(File(versionDirectory, game), path)
                if (!source.isFile || source.length() != recordedSize) continue

                val normalizedSha = sha256.lowercase(Locale.ROOT)
                val byHash = byPath.getOrPut(path) { HashMap() }
                // Candidates are newest-first, so putIfAbsent keeps the newest
                // available copy for a given path + sha256 pair.
                byHash.putIfAbsent(normalizedSha, source)
            }
        }

        val immutable = byPath.mapValues { (_, byHash) -> byHash.toMap() }
        return ReusableContentIndex(immutable)
    }

    /**
     * Retains the active version plus the most recent previous complete
     * version.  Versions older than that are removed to avoid unbounded disk
     * growth.  Failure to clean up is intentionally non-fatal.
     */
    fun cleanupOldVersions(game: String, keep: Int = 2) {
        if (keep < 1) return
        val activeVersion = readActiveValid(game)?.version
        val all = versionsDir.listFiles()
            ?.filter { it.isDirectory && !it.name.endsWith(PARTIAL_SUFFIX) }
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

    private fun readProgressMarker(partialDirectory: File): PausedContent? {
        val marker = File(partialDirectory, PROGRESS_MARKER)
        if (!marker.isFile) return null

        return try {
            val json = JSONObject(marker.readText(Charsets.UTF_8))
            val game = json.optString("game", "")
            if (game.isBlank()) return null

            PausedContent(
                game = game,
                version = json.optString("version", "").ifBlank {
                    partialDirectory.name.removeSuffix(PARTIAL_SUFFIX)
                },
                completedFiles = json.optInt("completedFiles", 0).coerceAtLeast(0),
                totalFiles = json.optInt("totalFiles", 0).coerceAtLeast(0),
                bytesCompleted = json.optLong("bytesCompleted", 0L).coerceAtLeast(0L),
                totalBytes = json.optLong("totalBytes", 0L).coerceAtLeast(0L),
                currentPath = json.optString("currentPath", ""),
                updatedAt = json.optLong("updatedAt", 0L).coerceAtLeast(0L)
            )
        } catch (_: Exception) {
            null
        }
    }

    private fun readCompleteMarkerGame(versionDirectory: File): String? {
        val marker = File(versionDirectory, COMPLETE_MARKER)
        if (!marker.isFile) return null
        return try {
            JSONObject(marker.readText(Charsets.UTF_8))
                .optString("game", "")
                .takeIf { it.isNotBlank() }
        } catch (_: Exception) {
            null
        }
    }

    private fun deleteStaleExternalRoot(context: Context) {
        try {
            val externalRoot = context.getExternalFilesDir(null) ?: return
            val staleRoot = File(externalRoot, "board-content")
            if (!staleRoot.exists()) return
            if (!staleRoot.deleteRecursively()) {
                Log.w(TAG, "failed to delete stale external content root: ${staleRoot.absolutePath}")
            } else {
                Log.i(TAG, "deleted stale external content root: ${staleRoot.absolutePath}")
            }
        } catch (t: Throwable) {
            Log.w(TAG, "failed to clean stale external content root", t)
        }
    }

    companion object {
        private const val TAG = "BoardAI-ContentStore"
        private const val COMPLETE_MARKER = "complete.json"
        private const val PROGRESS_MARKER = "progress.json"
        private const val PROGRESS_SCHEMA = "board-content-progress/v1"
        private const val PARTIAL_SUFFIX = ".partial"

        internal fun forTesting(root: File): ContentStore = ContentStore(root)
    }
}

/**
 * Same-directory rename is the atomic-switch primitive used for both version
 * directories and active.json.  Internal storage is normally a POSIX
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
