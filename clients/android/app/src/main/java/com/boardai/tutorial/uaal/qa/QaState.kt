package com.boardai.tutorial.uaal.qa

/**
 * Local request state for the QA composer.
 *
 * UI text is intentionally Chinese: it is shown directly in the input area.
 */
sealed interface QaSendState {
    data object Idle : QaSendState
    data object Sending : QaSendState
    data class Error(val message: String) : QaSendState
}

const val QA_NETWORK_ERROR_MESSAGE = "网络连接失败，请稍后重试。"

/** Text keyboard input vs. push-to-talk voice input. */
enum class QaInputMode {
    TEXT,
    VOICE
}

/** Animated listening label shown inside the PTT button. */
internal fun qaListeningLabel(dotCount: Int): String =
    "正在聆听" + ".".repeat(dotCount.coerceIn(1, 3))

/** True between PTT release and ASR completion/failure. */
internal fun isAsrRecognizing(status: String?): Boolean = status == "识别中…"
