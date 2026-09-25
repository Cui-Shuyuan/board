package com.boardai.tutorial.uaal.history

import android.content.Context
import android.util.Log
import com.boardai.tutorial.uaal.catalog.GameCatalogEntry
import java.io.File
import java.io.IOException
import java.nio.file.AtomicMoveNotSupportedException
import java.nio.file.Files
import java.nio.file.StandardCopyOption

/**
 * File-backed play history.
 *
 * Path: context.filesDir/history/play_history.json
 * Schema: board-play-history/v1
 *
 * Writes use a temporary file plus rename so a process death cannot leave a
 * partially-written document behind.
 */
class PlayHistoryRepository(context: Context) {
    private val historyDir = File(context.filesDir, "history")
    private val historyFile = File(historyDir, "play_history.json")

    fun read(): List<PlayHistoryEntry> {
        if (!historyFile.isFile) return emptyList()
        return try {
            PlayHistoryCodec.decode(historyFile.readText(Charsets.UTF_8))
        } catch (t: Throwable) {
            Log.w(TAG, "failed to read play history: ${historyFile.absolutePath}", t)
            emptyList()
        }
    }

    /**
     * Records the start of an actual tutorial playback session.
     *
     * [visibleGameIds] follow the current build visibility rule, so retention
     * prefers games that the current build can actually show.  Invisible rows
     * may be kept while under the 100-row cap, but are never joined into the UI.
     */
    fun recordPlay(
        gameId: String,
        visibleGameIds: Set<String>,
        now: Long = System.currentTimeMillis(),
        maxEntries: Int = MAX_ENTRIES
    ): List<PlayHistoryEntry> {
        val id = gameId.trim()
        if (id.isEmpty()) return read()

        val current = read()
        val updated = ArrayList<PlayHistoryEntry>(current.size + 1)
        var found = false
        for (entry in current) {
            if (entry.gameId == id) {
                found = true
                updated += entry.copy(
                    playCount = entry.playCount + 1,
                    lastPlayedAt = now
                )
            } else {
                updated += entry
            }
        }
        if (!found) {
            updated += PlayHistoryEntry(gameId = id, playCount = 1, lastPlayedAt = now)
        }

        val retained = retain(updated, visibleGameIds, maxEntries)
        write(retained)
        return retained
    }

    /**
     * Joins history to the current visible catalog and returns "常玩/最近"
     * rows sorted by playCount desc, then lastPlayedAt desc.  The limit is
     * applied after joining so hidden/unreleased rows cannot consume a visible
     * slot.
     */
    fun recentGames(
        visibleGames: List<GameCatalogEntry>,
        history: List<PlayHistoryEntry> = read(),
        limit: Int = RECENT_LIMIT
    ): List<RecentGame> {
        if (limit <= 0 || visibleGames.isEmpty() || history.isEmpty()) return emptyList()

        val byId = visibleGames.associateBy { it.id }
        return history
            .mapNotNull { entry ->
                val game = byId[entry.gameId] ?: return@mapNotNull null
                RecentGame(game = game, playCount = entry.playCount, lastPlayedAt = entry.lastPlayedAt)
            }
            .sortedWith(
                compareByDescending<RecentGame> { it.playCount }
                    .thenByDescending { it.lastPlayedAt }
            )
            .take(limit)
    }

    private fun retain(
        entries: List<PlayHistoryEntry>,
        visibleGameIds: Set<String>,
        maxEntries: Int
    ): List<PlayHistoryEntry> {
        return entries
            .sortedWith(
                compareByDescending<PlayHistoryEntry> { it.gameId in visibleGameIds }
                    .thenByDescending { it.playCount }
                    .thenByDescending { it.lastPlayedAt }
            )
            .take(maxEntries.coerceAtLeast(0))
    }

    private fun write(entries: List<PlayHistoryEntry>) {
        if (!historyDir.isDirectory && !historyDir.mkdirs()) {
            throw IOException("cannot create history directory: ${historyDir.absolutePath}")
        }

        val temp = File(historyDir, "$HISTORY_NAME.tmp")
        temp.writeText(PlayHistoryCodec.encode(entries), Charsets.UTF_8)
        if (!moveAtomically(temp, historyFile)) {
            temp.delete()
            throw IOException("failed to atomically replace play history")
        }
    }

    private fun moveAtomically(source: File, target: File): Boolean {
        return try {
            Files.move(
                source.toPath(),
                target.toPath(),
                StandardCopyOption.ATOMIC_MOVE,
                StandardCopyOption.REPLACE_EXISTING
            )
            true
        } catch (_: AtomicMoveNotSupportedException) {
            Files.move(source.toPath(), target.toPath(), StandardCopyOption.REPLACE_EXISTING)
            true
        } catch (_: Exception) {
            false
        }
    }

    companion object {
        const val MAX_ENTRIES = 100
        const val RECENT_LIMIT = 5
        private const val TAG = "BoardAI-History"
        private const val HISTORY_NAME = "play_history.json"
    }
}
