package com.boardai.tutorial.uaal.voice

import android.util.Log
import com.boardai.tutorial.uaal.BuildConfig
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import org.json.JSONObject
import java.io.File
import java.io.IOException
import java.net.HttpURLConnection
import java.net.URL

/**
 * POSTs a recorded WAV to BoardAI.Api /api/asr/once and parses the returned
 * text.  It deliberately uses HttpURLConnection + org.json to match the
 * existing Android networking style.
 */
class AsrRepository(
    baseUrl: String = BuildConfig.BOARD_API_BASE_URL
) {
    private val baseUrl = baseUrl.trimEnd('/')

    suspend fun transcribe(wavFile: File): Result<String> = withContext(Dispatchers.IO) {
        try {
            val wavBytes = wavFile.readBytes()
            if (wavBytes.isEmpty()) {
                throw IOException("recorded wav is empty")
            }

            val connection = (URL("$baseUrl/api/asr/once").openConnection() as HttpURLConnection).apply {
                requestMethod = "POST"
                connectTimeout = 20_000
                readTimeout = 75_000
                doOutput = true
                instanceFollowRedirects = true
                setFixedLengthStreamingMode(wavBytes.size)
                setRequestProperty("Content-Type", "audio/wav")
                setRequestProperty("Accept", "application/json")
                setRequestProperty("Cache-Control", "no-cache")
            }

            try {
                connection.outputStream.use { output ->
                    output.write(wavBytes)
                }

                val code = connection.responseCode
                if (code !in 200..299) {
                    val detail = connection.errorStream
                        ?.bufferedReader(Charsets.UTF_8)
                        ?.use { it.readText() }
                        .orEmpty()
                    throw IOException("HTTP $code from asr endpoint: $detail")
                }

                val body = connection.inputStream
                    .bufferedReader(Charsets.UTF_8)
                    .use { it.readText() }
                val text = JSONObject(body).optString("text", "").trim()
                if (text.isBlank()) {
                    throw IOException("ASR response did not contain text")
                }

                Result.success(text)
            } finally {
                connection.disconnect()
            }
        } catch (t: Throwable) {
            if (t is kotlinx.coroutines.CancellationException) throw t
            Log.w(TAG, "ASR request failed", t)
            Result.failure(t)
        }
    }

    private companion object {
        const val TAG = "BoardAI-Asr"
    }
}
