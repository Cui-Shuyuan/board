package com.boardai.tutorial.uaal.qa

/**
 * Local request state for the QA composer.
 *
 * Error details are typed so business logic never compares display copy.
 */
sealed interface QaSendState {
    data object Idle : QaSendState
    data object Sending : QaSendState
    data object NetworkError : QaSendState
}

const val QA_NETWORK_ERROR_MESSAGE = "网络连接失败，请稍后重试。"

/** Presentation-only mapping for a typed send failure. */
internal fun qaSendErrorMessage(state: QaSendState): String? = when (state) {
    QaSendState.NetworkError -> QA_NETWORK_ERROR_MESSAGE
    QaSendState.Idle, QaSendState.Sending -> null
}

/** Text keyboard input vs. push-to-talk voice input. */
enum class QaInputMode {
    TEXT,
    VOICE
}

/** Animated listening label shown inside the PTT button. */
internal fun qaListeningLabel(dotCount: Int): String =
    "正在聆听" + ".".repeat(dotCount.coerceIn(1, 3))
