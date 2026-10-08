package com.boardai.tutorial.uaal.voice

import android.content.Context
import android.util.Log
import com.boardai.tutorial.uaal.BuildConfig
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.isActive
import kotlinx.coroutines.withContext
import org.json.JSONObject
import java.io.File
import java.io.IOException
import java.net.HttpURLConnection
import java.net.URL
import java.security.MessageDigest
import java.util.concurrent.ConcurrentHashMap

const val DEFAULT_TTS_VOICE = "BV700_streaming"

/**
 * Requests mp3 bytes from BoardAI.Api /api/tts and stores the result in
 * cacheDir/tts/answer-<hash>.mp3.
 */
class TtsRepository(
    private val context: Context,
    baseUrl: String = BuildConfig.BOARD_API_BASE_URL
) : QaTtsEngine {
    private val baseUrl = baseUrl.trimEnd('/')
    private val activeConnections = ConcurrentHashMap.newKeySet<HttpURLConnection>()

    @Volatile
    private var cancelGeneration = 0L

    override fun cancelActiveRequests() {
        cancelGeneration += 1
        activeConnections.toList().forEach { connection ->
            runCatching { connection.disconnect() }
        }
    }

    override suspend fun synthesize(
        text: String,
        voice: String,
        speed: Double
    ): Result<File> = withContext(Dispatchers.IO) {
        val requestGeneration = cancelGeneration
        try {
            val normalizedText = text.trim()
            if (normalizedText.isBlank()) {
                throw IOException("tts text is empty")
            }

            val requestBody = JSONObject()
                .put("text", normalizedText)
                .put("voice", voice)
                .put("speed", speed)
                .toString()

            val connection = (URL("$baseUrl/api/tts").openConnection() as HttpURLConnection).apply {
                requestMethod = "POST"
                connectTimeout = 20_000
                readTimeout = 120_000
                doOutput = true
                instanceFollowRedirects = true
                setRequestProperty("Content-Type", "application/json; charset=utf-8")
                setRequestProperty("Accept", "audio/mpeg")
                setRequestProperty("Cache-Control", "no-cache")
            }
            activeConnections += connection

            try {
                if (requestGeneration != cancelGeneration) {
                    throw kotlinx.coroutines.CancellationException("tts request cancelled before start")
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
                    throw IOException("HTTP $code from tts endpoint: $detail")
                }

                val bytes = connection.inputStream.use { it.readBytes() }
                if (bytes.isEmpty()) {
                    throw IOException("TTS response was empty")
                }

                val ttsDir = File(context.cacheDir, "tts").apply { mkdirs() }
                val file = File(ttsDir, "answer-${hash(voice, speed, normalizedText)}.mp3")
                file.writeBytes(bytes)
                Result.success(file)
            } finally {
                activeConnections -= connection
                connection.disconnect()
            }
        } catch (t: Throwable) {
            if (t is kotlinx.coroutines.CancellationException) throw t
            if (!currentCoroutineContext().isActive || requestGeneration != cancelGeneration) {
                throw kotlinx.coroutines.CancellationException("tts request cancelled")
            }
            Log.w(TAG, "TTS request failed", t)
            Result.failure(t)
        }
    }

    private fun hash(voice: String, speed: Double, text: String): String {
        val digest = MessageDigest.getInstance("SHA-256")
        val input = "$voice|$speed|$text".toByteArray(Charsets.UTF_8)
        return digest.digest(input).joinToString(separator = "") { byte ->
            "%02x".format(byte.toInt() and 0xFF)
        }.take(16)
    }

    private companion object {
        const val TAG = "BoardAI-Tts"
    }
}
