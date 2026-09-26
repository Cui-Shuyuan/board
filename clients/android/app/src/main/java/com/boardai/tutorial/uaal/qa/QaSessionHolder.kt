package com.boardai.tutorial.uaal.qa

import androidx.compose.runtime.State
import androidx.compose.runtime.mutableStateOf

/**
 * Activity-scoped, process-local holder for the current tutorial QA session.
 *
 * It is deliberately not persisted.  MainActivity clears it when the tutorial
 * player leaves the foreground (return home / switch game / UnloadGame).
 */
object QaSessionHolder {
    private val mutableSession = mutableStateOf<QaSession?>(null)

    /**
     * Exposed as a real [State] object so Compose readers always observe
     * session changes even though this holder lives outside the composable.
     */
    val sessionState: State<QaSession?> get() = mutableSession

    val session: QaSession? get() = mutableSession.value

    /** Returns the current session, creating one for [context] if needed. */
    fun ensureSession(context: QaContext): QaSession {
        val current = mutableSession.value
        if (current == null || current.gameId != context.gameId) {
            return QaSession(
                gameId = context.gameId,
                context = context
            ).also { mutableSession.value = it }
        }

        val updated = current.copy(context = context)
        mutableSession.value = updated
        return updated
    }

    /** Clears messages but keeps the same current playback context. */
    fun startNewSession(context: QaContext): QaSession {
        val next = QaSession(
            gameId = context.gameId,
            context = context
        )
        mutableSession.value = next
        return next
    }

    fun appendMessage(
        message: QaMessage,
        context: QaContext? = null
    ) {
        val current = mutableSession.value ?: return
        mutableSession.value = current.copy(
            messages = current.messages + message,
            context = context ?: current.context
        )
    }

    fun updateContext(context: QaContext) {
        val current = mutableSession.value ?: return
        mutableSession.value = current.copy(context = context)
    }

    fun clear() {
        mutableSession.value = null
    }

}
