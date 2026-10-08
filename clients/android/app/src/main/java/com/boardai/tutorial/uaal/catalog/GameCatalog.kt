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
    val tutorialTrack: String,
    val contentVersion: String? = null,
    val contentSizeBytes: Long? = null,
    val contentFileCount: Int? = null,
    val rulesReady: Boolean = true,
    val tutorialReady: Boolean = tutorialTrack.isNotBlank(),
    val tutorialTracks: List<String> =
        if (tutorialTrack.isNotBlank()) listOf(tutorialTrack) else emptyList()
) {
    fun toJson(): JSONObject {
        val json = JSONObject()
            .put("id", id)
            .put("released", released)
            .put("name_zh", nameZh)
            .put("name_en", nameEn)
            .put("aliases", JSONArray(aliases))
            .put("search_keys", JSONArray(searchKeys))
            .put("min_players", minPlayers)
            .put("max_players", maxPlayers)
            .put("rules_ready", rulesReady)
            .put("tutorial_ready", tutorialReady)
            .put("tutorial_tracks", JSONArray(tutorialTracks))
        if (tutorialReady && tutorialTrack.isNotBlank()) {
            json.put("tutorial_track", tutorialTrack)
        }
        contentVersion?.let { json.put("content_version", it) }
        contentSizeBytes?.let { json.put("content_size_bytes", it) }
        contentFileCount?.let { json.put("content_file_count", it) }
        return json
    }

    companion object {
        fun fromJson(json: JSONObject): GameCatalogEntry? {
            val id = json.optString("id", "").trim()
            if (id.isBlank()) return null

            val aliases = json.optJSONArray("aliases").toStringList()
            val searchKeys = json.optJSONArray("search_keys").toStringList()

            val hasExplicitTracks = json.has("tutorial_tracks")
            val explicitTracks = if (hasExplicitTracks) {
                json.optJSONArray("tutorial_tracks").toStringList()
            } else {
                emptyList()
            }
            val legacyTrack = json.optString("tutorial_track", "").trim()
            val rawTracks = if (hasExplicitTracks) {
                explicitTracks
            } else if (legacyTrack.isNotBlank()) {
                listOf(legacyTrack)
            } else {
                emptyList()
            }
            val tutorialReady = if (json.has("tutorial_ready")) {
                json.optBoolean("tutorial_ready", false)
            } else {
                rawTracks.isNotEmpty()
            }
            val tutorialTracks = if (tutorialReady) rawTracks else emptyList()
            val rulesReady = if (json.has("rules_ready")) {
                json.optBoolean("rules_ready", true)
            } else {
                true
            }

            val contentVersion = if (json.has("content_version") && !json.isNull("content_version")) {
                json.optString("content_version", "").trim().takeIf { it.isNotBlank() }
            } else {
                null
            }
            val contentSizeBytes = if (json.has("content_size_bytes") && !json.isNull("content_size_bytes")) {
                json.optLong("content_size_bytes", -1L).takeIf { it >= 0L }
            } else {
                null
            }
            val contentFileCount = if (json.has("content_file_count") && !json.isNull("content_file_count")) {
                json.optInt("content_file_count", -1).takeIf { it >= 0 }
            } else {
                null
            }

            return GameCatalogEntry(
                id = id,
                released = json.optBoolean("released", false),
                nameZh = json.optString("name_zh", "").trim().ifBlank { id },
                nameEn = json.optString("name_en", "").trim(),
                aliases = aliases,
                searchKeys = searchKeys,
                minPlayers = json.optInt("min_players", 0).coerceAtLeast(0),
                maxPlayers = json.optInt("max_players", 0).coerceAtLeast(0),
                tutorialTrack = tutorialTracks.firstOrNull().orEmpty(),
                contentVersion = contentVersion,
                contentSizeBytes = contentSizeBytes,
                contentFileCount = contentFileCount,
                rulesReady = rulesReady,
                tutorialReady = tutorialReady && tutorialTracks.isNotEmpty(),
                tutorialTracks = tutorialTracks
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
