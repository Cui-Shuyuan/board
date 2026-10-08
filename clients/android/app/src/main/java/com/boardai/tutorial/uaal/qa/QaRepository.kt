package com.boardai.tutorial.uaal.qa

import android.util.Log
import com.boardai.tutorial.uaal.BuildConfig
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.isActive
import kotlinx.coroutines.withContext
import org.json.JSONArray
import org.json.JSONObject
import java.io.IOException
import java.net.HttpURLConnection
import java.net.URL
import java.util.concurrent.ConcurrentHashMap

/**
 * Small /api/chat client used by the text QA panel.
 *
 * It uses HttpURLConnection + org.json on purpose so the Android module does
 * not need an extra networking or serialization dependency.
 */
class QaRepository(
    baseUrl: String = BuildConfig.BOARD_API_BASE_URL
) {
    private val baseUrl = baseUrl.trimEnd('/')
    private val activeConnections = ConcurrentHashMap.newKeySet<HttpURLConnection>()

    @Volatile
    private var cancelGeneration = 0L

    /** Disconnect any in-flight request so new sessions cannot receive stale replies. */
    fun cancelActiveRequests() {
        cancelGeneration += 1
        activeConnections.toList().forEach { connection ->
            runCatching { connection.disconnect() }
        }
    }

    /**
     * Sends the complete conversation and the latest playback context.
     *
     * This is a suspend function and performs I/O on [Dispatchers.IO], so callers
     * can invoke it from a main-dispatcher Compose coroutine and update UI in the
     * returned continuation on the main thread.
     */
    suspend fun send(
        gameId: String,
        messages: List<QaMessage>,
        context: QaContext
    ): Result<QaChatResult> = withContext(Dispatchers.IO) {
        val requestGeneration = cancelGeneration
        try {
            val requestBody = buildRequestBody(gameId, messages, context)
            val connection = (URL("$baseUrl/api/chat").openConnection() as HttpURLConnection).apply {
                requestMethod = "POST"
                connectTimeout = 20_000
                readTimeout = 60_000
                doOutput = true
                instanceFollowRedirects = true
                setRequestProperty("Content-Type", "application/json; charset=utf-8")
                setRequestProperty("Accept", "application/json")
                setRequestProperty("Cache-Control", "no-cache")
            }
            activeConnections += connection

            try {
                if (requestGeneration != cancelGeneration) {
                    throw kotlinx.coroutines.CancellationException("qa chat request cancelled before start")
                }

                connection.outputStream.use { output ->
                    output.write(requestBody.toByteArray(Charsets.UTF_8))
                }

                val code = connection.responseCode
                if (code !in 200..299) {
                    val detail = connection.errorStream
                        ?.bufferedReader(Charsets.UTF_8)
                        ?.use { it.readText() }
                        .orEmpty()
                    throw IOException("HTTP $code from chat endpoint: $detail")
                }

                val body = connection.inputStream
                    .bufferedReader(Charsets.UTF_8)
                    .use { it.readText() }
                // Evidence is optional metadata. A malformed evidence object must never
                // turn a valid reply into a chat failure; parseQaChatResponse isolates
                // evidence parsing and falls back to a reply-only result.
                Result.success(runCatching { parseQaChatResponse(body) }
                    .getOrElse { QaChatResult(reply = JSONObject(body).optString("reply", "")) })
            } finally {
                activeConnections -= connection
                connection.disconnect()
            }
        } catch (t: Throwable) {
            if (t is kotlinx.coroutines.CancellationException) throw t
            if (!currentCoroutineContext().isActive || requestGeneration != cancelGeneration) {
                throw kotlinx.coroutines.CancellationException("qa chat request cancelled")
            }
            Log.w(TAG, "chat request failed", t)
            Result.failure(t)
        }
    }

    private fun buildRequestBody(
        gameId: String,
        messages: List<QaMessage>,
        context: QaContext
    ): String {
        val messageArray = JSONArray()
        messages.forEach { message ->
            messageArray.put(
                JSONObject()
                    .put("role", message.role)
                    .put("content", message.content)
            )
        }

        val recentCues = JSONArray()
        context.recentCues.forEach { cue ->
            recentCues.put(
                JSONObject()
                    .put("id", cue.id)
                    .put("index", cue.index)
                    .put("text", cue.text)
                    .put("group_path", JSONArray(cue.groupPath))
                    .put("refs", JSONArray(cue.refs))
                    .put("actions", JSONArray(cue.actions))
                    .put("start", cue.start.toDouble())
                    .put("duration", cue.duration.toDouble())
                    .put("is_current", cue.isCurrent)
            )
        }

        val contextObject = JSONObject()
            .put("game_name", context.gameName)
            .put("cue_id", context.cueId)
            .put("cue_index", context.cueIndex)
            .put("cue_text", context.cueText)
            .put("group_path", JSONArray(context.groupPath))
            .put("position", context.positionInCue.toDouble())
            .put("recent_cues", recentCues)

        return JSONObject()
            .put("game_id", gameId)
            .put("messages", messageArray)
            .put("context", contextObject)
            .toString()
    }

    private companion object {
        const val TAG = "BoardAI-Qa"
    }
}
