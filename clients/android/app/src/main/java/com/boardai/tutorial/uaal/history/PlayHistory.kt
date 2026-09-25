package com.boardai.tutorial.uaal.history

import org.json.JSONArray
import org.json.JSONObject

data class PlayHistoryEntry(
    val gameId: String,
    val playCount: Int,
    val lastPlayedAt: Long
) {
    fun toJson(): JSONObject = JSONObject()
        .put("gameId", gameId)
        .put("playCount", playCount)
        .put("lastPlayedAt", lastPlayedAt)

    companion object {
        fun fromJson(json: JSONObject): PlayHistoryEntry? {
            val gameId = json.optString("gameId", "").trim()
            if (gameId.isBlank()) return null
            return PlayHistoryEntry(
                gameId = gameId,
                playCount = json.optInt("playCount", 0).coerceAtLeast(0),
                lastPlayedAt = json.optLong("lastPlayedAt", 0L).coerceAtLeast(0L)
            )
        }
    }
}

/** A history row successfully joined to a currently visible catalog entry. */
data class RecentGame(
    val game: com.boardai.tutorial.uaal.catalog.GameCatalogEntry,
    val playCount: Int,
    val lastPlayedAt: Long
)

object PlayHistoryCodec {
    const val SCHEMA = "board-play-history/v1"

    fun decode(raw: String): List<PlayHistoryEntry> {
        val root = JSONObject(raw)
        val games = root.optJSONArray("games") ?: return emptyList()
        val result = ArrayList<PlayHistoryEntry>(games.length())
        for (i in 0 until games.length()) {
            val item = games.optJSONObject(i) ?: continue
            PlayHistoryEntry.fromJson(item)?.let(result::add)
        }
        return result
    }

    fun encode(games: List<PlayHistoryEntry>): String {
        val array = JSONArray()
        for (game in games) array.put(game.toJson())
        return JSONObject()
            .put("schema", SCHEMA)
            .put("games", array)
            .toString(2)
    }
}
