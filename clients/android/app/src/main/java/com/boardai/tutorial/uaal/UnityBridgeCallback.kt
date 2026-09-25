package com.boardai.tutorial.uaal

import android.os.Handler
import android.os.Looper
import androidx.annotation.Keep
import androidx.compose.runtime.mutableStateOf
import org.json.JSONObject

/**
 * Receives status JSON from Unity.
 *
 * Unity calls this through AndroidJavaClass:
 *   com.boardai.tutorial.uaal.UnityBridgeCallback.postStatus(String)
 *
 * The object is kept because R8 cannot see JNI reflection. Updates are posted
 * to the Android main thread before they are written to Compose state.
 */
@Keep
object UnityBridgeCallback {
    private val mainHandler = Handler(Looper.getMainLooper())

    @JvmStatic
    @Keep
    fun postStatus(json: String) {
        mainHandler.post {
            UnityStatusHolder.updateFromJson(json)
        }
    }
}

data class UnityStatus(
    val unityReady: Boolean = false,
    val isPlaying: Boolean = false,
    val isPaused: Boolean = false,
    val volume: Float = 1f,
    val cueId: String = "",
    val cueText: String = "",
    val cueIndex: Int = -1,
    val cueTotal: Int = 0,
    val position: Float = 0f,
    val duration: Float = 0f,
    val touchControlsEnabled: Boolean = false
)

object UnityStatusHolder {
    val status = mutableStateOf<UnityStatus?>(null)

    fun updateFromJson(json: String) {
        try {
            val obj = JSONObject(json)
            status.value = UnityStatus(
                unityReady = obj.optBoolean("unityReady", false),
                isPlaying = obj.optBoolean("isPlaying", false),
                isPaused = obj.optBoolean("isPaused", false),
                volume = obj.optDouble("volume", 1.0).toFloat().coerceIn(0f, 1f),
                cueId = obj.optString("cueId", ""),
                cueText = obj.optString("cueText", ""),
                cueIndex = obj.optInt("cueIndex", -1),
                cueTotal = obj.optInt("cueTotal", 0),
                position = obj.optDouble("position", 0.0).toFloat(),
                duration = obj.optDouble("duration", 0.0).toFloat(),
                touchControlsEnabled = obj.optBoolean("touchControlsEnabled", false)
            )
        } catch (_: Exception) {
            // Keep the last known state if Unity sends malformed JSON.
        }
    }
}
