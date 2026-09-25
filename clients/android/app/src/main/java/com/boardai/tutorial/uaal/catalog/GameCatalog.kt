package com.boardai.tutorial.uaal.catalog

import org.json.JSONArray
import org.json.JSONObject

/**
 * One parsed content/catalog JSON entry.
 *
 * The local cache stores every entry returned by the backend, including
 * released=false.  Build-type visibility is applied at read time in
 * GameCatalogRepository, never while writing the cache.
 */
data class GameCatalogEntry(
    val id: String,
    val released: Boolean,
    val nameZh: String,
    val nameEn: String,
    val aliases: List<String>,
    val searchKeys: List<String>,
    val minPlayers: Int,
    val maxPlayers: Int,
    val tutorialTrack: String
) {
    fun toJson(): JSONObject = JSONObject()
        .put("id", id)
        .put("released", released)
        .put("name_zh", nameZh)
        .put("name_en", nameEn)
        .put("aliases", JSONArray(aliases))
        .put("search_keys", JSONArray(searchKeys))
        .put("min_players", minPlayers)
        .put("max_players", maxPlayers)
        .put("tutorial_track", tutorialTrack)

    companion object {
        fun fromJson(json: JSONObject): GameCatalogEntry? {
            val id = json.optString("id", "").trim()
            if (id.isBlank()) return null

            val aliases = json.optJSONArray("aliases").toStringList()
            val searchKeys = json.optJSONArray("search_keys").toStringList()

            return GameCatalogEntry(
                id = id,
                released = json.optBoolean("released", false),
                nameZh = json.optString("name_zh", "").trim().ifBlank { id },
                nameEn = json.optString("name_en", "").trim(),
                aliases = aliases,
                searchKeys = searchKeys,
                minPlayers = json.optInt("min_players", 0).coerceAtLeast(0),
                maxPlayers = json.optInt("max_players", 0).coerceAtLeast(0),
                tutorialTrack = json.optString("tutorial_track", "full")
                    .trim()
                    .ifBlank { "full" }
            )
        }
    }
}

/**
 * Small org.json codec shared by the local cache reader/writer and the network
 * refresh path.  It intentionally does not depend on any third-party JSON
 * library.
 */
object GameCatalogCodec {
    fun decode(raw: String): List<GameCatalogEntry> {
        val root = JSONArray(raw)
        val result = ArrayList<GameCatalogEntry>(root.length())
        for (i in 0 until root.length()) {
            val item = root.optJSONObject(i) ?: continue
            GameCatalogEntry.fromJson(item)?.let(result::add)
        }
        return result
    }

    fun encode(games: List<GameCatalogEntry>): String {
        val array = JSONArray()
        for (game in games) array.put(game.toJson())
        return array.toString(2)
    }
}

private fun JSONArray?.toStringList(): List<String> {
    if (this == null) return emptyList()
    val result = ArrayList<String>(length())
    for (i in 0 until length()) {
        val value = optString(i, "").trim()
        if (value.isNotBlank()) result += value
    }
    return result
}
