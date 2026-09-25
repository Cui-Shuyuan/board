package com.boardai.tutorial.uaal.catalog

import android.content.Context
import android.util.Log
import com.boardai.tutorial.uaal.BuildConfig
import org.json.JSONException
import java.io.File
import java.io.IOException
import java.net.HttpURLConnection
import java.net.URL
import java.nio.file.AtomicMoveNotSupportedException
import java.nio.file.Files
import java.nio.file.StandardCopyOption

/**
 * File-backed game catalog repository.
 *
 * Read path: context.filesDir/catalog/games.json
 * Write path: same file, written through games.json.tmp + rename.
 *
 * The cache always contains the complete backend catalog.  Build-type filtering
 * happens in [visibleGames] and is deliberately never written back to disk.
 */
class GameCatalogRepository(
    context: Context,
    baseUrl: String
) {
    private val baseUrl = baseUrl.trimEnd('/')
    private val catalogDir = File(context.filesDir, "catalog")
    private val cacheFile = File(catalogDir, "games.json")

    /** Reads the last successful catalog cache.  Network failures never clear it. */
    fun readCache(): List<GameCatalogEntry> {
        if (!cacheFile.isFile) return emptyList()
        return try {
            GameCatalogCodec.decode(cacheFile.readText(Charsets.UTF_8))
        } catch (t: Throwable) {
            Log.w(TAG, "failed to read catalog cache: ${cacheFile.absolutePath}", t)
            emptyList()
        }
    }

    /**
     * Fetches GET /api/catalog/games and atomically replaces the local cache.
     * Throws on network/parse failure so the caller can keep showing old data.
     */
    fun fetchCatalog(): List<GameCatalogEntry> {
        val connection = (URL("$baseUrl/api/catalog/games").openConnection()
            as HttpURLConnection).apply {
            requestMethod = "GET"
            connectTimeout = 15_000
            readTimeout = 30_000
            instanceFollowRedirects = true
            setRequestProperty("Accept", "application/json")
            setRequestProperty("Cache-Control", "no-cache")
        }

        try {
            val code = connection.responseCode
            if (code !in 200..299) {
                val detail = connection.errorStream
                    ?.bufferedReader(Charsets.UTF_8)
                    ?.use { it.readText() }
                    .orEmpty()
                throw IOException("HTTP $code from catalog endpoint: $detail")
            }

            val body = connection.inputStream
                .bufferedReader(Charsets.UTF_8)
                .use { it.readText() }
            val games = GameCatalogCodec.decode(body)
            writeCache(games)
            return games
        } finally {
            connection.disconnect()
        }
    }

    /**
     * Applies the build-type visibility rule.
     *
     * BuildConfig.DEBUG == true  -> all catalog entries (released true + false)
     * BuildConfig.DEBUG == false -> released entries only
     */
    fun visibleGames(allGames: List<GameCatalogEntry>): List<GameCatalogEntry> =
        if (BuildConfig.DEBUG) allGames else allGames.filter { it.released }

    private fun writeCache(games: List<GameCatalogEntry>) {
        if (!catalogDir.isDirectory && !catalogDir.mkdirs()) {
            throw IOException("cannot create catalog directory: ${catalogDir.absolutePath}")
        }

        val temp = File(catalogDir, "$CACHE_NAME.tmp")
        temp.writeText(GameCatalogCodec.encode(games), Charsets.UTF_8)
        if (!moveAtomically(temp, cacheFile)) {
            temp.delete()
            throw IOException("failed to atomically replace catalog cache")
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

    private companion object {
        const val TAG = "BoardAI-Catalog"
        const val CACHE_NAME = "games.json"
    }
}
