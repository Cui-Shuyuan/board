package com.boardai.tutorial.uaal.qa

import android.os.SystemClock

/**
 * One message in the temporary QA session.
 *
 * The backend only receives role/content, but the timestamp is retained so the
 * local UI can render a stable list and future versions can add history.
 */
data class QaMessage(
    val role: String,
    val content: String,
    val timestamp: Long = System.currentTimeMillis()
) {
    companion object {
        const val ROLE_USER = "user"
        const val ROLE_ASSISTANT = "assistant"
    }
}

 /**
  * In-memory QA session.  It is never written to disk or database.
  */
data class QaSession(
    val gameId: String,
    val createdAt: Long = SystemClock.elapsedRealtime(),
    val messages: List<QaMessage> = emptyList(),
    val context: QaContext
)
