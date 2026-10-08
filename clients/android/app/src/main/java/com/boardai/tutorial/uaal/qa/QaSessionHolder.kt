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
    private var nextGeneration = 0L

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
            return newSession(context)
        }

        val updated = current.copy(context = context)
        mutableSession.value = updated
        return updated
    }

    /** Clears messages and starts a new generation for the same context. */
    fun startNewSession(context: QaContext): QaSession = newSession(context)

    /** True only when [generation] still identifies the active session. */
    fun isCurrent(generation: Long): Boolean =
        mutableSession.value?.generation == generation

    /**
     * Appends a message only when [generation] still identifies the active
     * session.  Stale callbacks must not leak into a new session.
     */
    fun appendMessage(
        message: QaMessage,
        context: QaContext?,
        generation: Long
    ): Boolean {
        val current = mutableSession.value
        if (current == null || current.generation != generation) return false
        mutableSession.value = current.copy(
            messages = current.messages + message,
            context = context ?: current.context
        )
        return true
    }

    private fun newSession(context: QaContext): QaSession {
        nextGeneration += 1
        return QaSession(
            gameId = context.gameId,
            generation = nextGeneration,
            context = context
        ).also { mutableSession.value = it }
    }

    fun updateContext(context: QaContext) {
        val current = mutableSession.value ?: return
        mutableSession.value = current.copy(context = context)
    }

    fun clear() {
        mutableSession.value = null
    }

}
