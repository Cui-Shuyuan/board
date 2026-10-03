package com.boardai.tutorial.uaal.content

import android.util.Log
import java.io.File
import java.io.FileOutputStream
import java.io.IOException
import java.net.HttpURLConnection
import java.net.URL
import java.net.URLEncoder

/**
 * HTTP transport for board content.  Local file writes are delegated to
 * [ContentFileUtils].
 */
class HttpContentFetcher(baseUrl: String) : ContentFetcher {
    private val baseUrl: String = baseUrl.trimEnd('/')

    override fun fetchManifest(game: String): ContentManifest {
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
    override fun downloadFile(
        file: ContentFile,
        target: File,
        control: DownloadControl,
        onBytesWritten: (Long) -> Unit
    ): ContentFileDownloadOutcome {
        val part = downloadPartFile(target)
        part.parentFile?.mkdirs()
        var offset = if (part.isFile) part.length() else 0L
        if (offset > 0L) {
            Log.i(TAG, "resume offset=$offset for ${file.path}")
        }

        if (offset == file.size) {
            if (contentMatches(file, part)) {
                // The caller performs verification and the atomic move so that
                // a fully-written `.part` is handled exactly once.
                onBytesWritten(file.size)
                return ContentFileDownloadOutcome.COMPLETED
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
                                return ContentFileDownloadOutcome.PAUSED
                            }

                            val read = input.read(buffer)
                            if (read < 0) break
                            output.write(buffer, 0, read)
                            written += read
                            onBytesWritten(written)

                            if (control.pauseRequested) {
                                output.flush()
                                return ContentFileDownloadOutcome.PAUSED
                            }
                        }
                    }
                }

                return ContentFileDownloadOutcome.COMPLETED
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

    private fun parseContentRangeStart(value: String?): Long? {
        if (value.isNullOrBlank()) return null
        val match = CONTENT_RANGE_RE.find(value.trim()) ?: return null
        return match.groupValues[1].toLongOrNull()
    }

    private companion object {
        const val TAG = "BoardAI-Content"
        val CONTENT_RANGE_RE = Regex(
            "^bytes\\s+(\\d+)-(\\d+)/(\\d+|\\*)$",
            RegexOption.IGNORE_CASE
        )
    }
}
