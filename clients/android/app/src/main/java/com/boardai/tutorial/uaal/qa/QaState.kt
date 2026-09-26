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
