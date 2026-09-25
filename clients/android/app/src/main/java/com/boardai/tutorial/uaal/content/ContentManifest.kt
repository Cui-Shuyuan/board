package com.boardai.tutorial.uaal.content

import org.json.JSONArray
import org.json.JSONObject
import java.util.Locale

/**
 * One file entry from content/manifests/{game}.json.
 *
 * All paths are relative to the game directory and always use forward slashes.
 */
data class ContentFile(
    val path: String,
    val size: Long,
    val sha256: String,
    val url: String
)

/**
 * Parsed board-content/v1 manifest.  org.json is used intentionally so the
 * Android client does not need an extra JSON dependency.
 */
data class ContentManifest(
    val schema: String,
    val game: String,
    val version: String,
    val files: List<ContentFile>
) {
    companion object {
        const val SCHEMA = "board-content/v1"
        private val VERSION_RE = Regex("^[0-9a-fA-F]{8,64}$")
        private val SHA256_RE = Regex("^[0-9a-fA-F]{64}$")

        fun parse(json: String): ContentManifest {
            val root = JSONObject(json)
            val schema = root.optString("schema", "")
            require(schema == SCHEMA) { "unsupported manifest schema: '$schema'" }

            val game = root.optString("game", "")
            require(game.isNotBlank()) { "manifest game is empty" }

            val version = root.optString("version", "")
            require(VERSION_RE.matches(version)) { "invalid manifest version: '$version'" }

            val array = root.optJSONArray("files")
            require(array != null) { "manifest files array is missing" }

            val files = ArrayList<ContentFile>(array.length())
            for (i in 0 until array.length()) {
                val item = array.getJSONObject(i)
                val path = item.optString("path", "")
                require(isSafeRelativePath(path)) { "unsafe manifest file path: '$path'" }

                val size = item.optLong("size", -1L)
                require(size >= 0L) { "invalid size for '$path'" }

                val sha = item.optString("sha256", "").lowercase(Locale.ROOT)
                require(SHA256_RE.matches(sha)) { "invalid sha256 for '$path'" }

                val url = item.optString("url", "")
                require(url.isNotBlank()) { "missing download url for '$path'" }

                files += ContentFile(path = path, size = size, sha256 = sha, url = url)
            }

            return ContentManifest(
                schema = schema,
                game = game,
                version = version.lowercase(Locale.ROOT),
                files = files
            )
        }

        /**
         * Manifest paths are trusted only after this check.  The server also
         * validates them, but the client must never be tricked into writing
         * outside board-content/versions/{version}/{game}/.
         */
        fun isSafeRelativePath(path: String): Boolean {
            if (path.isBlank()) return false
            if (path.startsWith('/')) return false
            if (path.contains('\\')) return false
            if (path.indexOf('\u0000') >= 0) return false
            return path.split('/').all { segment ->
                segment.isNotEmpty() && segment != "." && segment != ".."
            }
        }
    }
}

internal fun JSONArray.toStringList(): List<String> {
    val result = ArrayList<String>(length())
    for (i in 0 until length()) result += optString(i, "")
    return result
}
