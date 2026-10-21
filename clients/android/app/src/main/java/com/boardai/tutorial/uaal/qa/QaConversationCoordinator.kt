package com.boardai.tutorial.uaal.qa

import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Job
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch

/** Minimal chat transport boundary; QaRepository is the production adapter. */
interface QaChatGateway {
    suspend fun send(
        gameId: String,
        messages: List<QaMessage>,
        context: QaContext
    ): Result<QaChatResult>

    fun cancelActiveRequests()
}

/**
 * Text/voice turn coordination for the QA panel.
 *
 * Owns request lifecycle, session generation checks, reply appending and
 * cancellation. Compose remains responsible for rendering, scroll position,
 * keyboard focus and permission UI.
 */
class QaConversationCoordinator(
    private val scope: CoroutineScope,
    private val chatGateway: QaChatGateway,
    private val onAssistantReply: (QaMessage) -> Unit,
    private val elapsedMillis: () -> Long = { System.nanoTime() / 1_000_000L },
    private val log: (String) -> Unit = {}
) {
    private val _sendState = MutableStateFlow<QaSendState>(QaSendState.Idle)
    val sendState: StateFlow<QaSendState> = _sendState.asStateFlow()

    private var nextRequestId = 0L
    private var activeRequestId: Long? = null
    private var chatJob: Job? = null

    /**
     * Appends the user message and starts a request. Returns false only when the
     * input is blank, a send is already active, or the session changed while
     * composing. The caller decides whether to clear a keyboard draft.
     */
    fun sendQuestion(question: String, context: QaContext): Boolean {
        val trimmed = question.trim()
        if (trimmed.isBlank() || _sendState.value is QaSendState.Sending) return false

        val activeSession = QaSessionHolder.ensureSession(context)
        val sessionGeneration = activeSession.generation
        val userMessage = QaMessage(
            role = QaMessage.ROLE_USER,
            content = trimmed
        )
        if (!QaSessionHolder.appendMessage(userMessage, context, sessionGeneration)) {
            log("chat send ignored: session changed while composing")
            return false
        }

        val history = QaSessionHolder.session
            ?.takeIf { it.generation == sessionGeneration }
            ?.messages
            .orEmpty()
        val requestId = ++nextRequestId
        activeRequestId = requestId
        _sendState.value = QaSendState.Sending
        val startedAtMs = elapsedMillis()

        chatJob = scope.launch {
            val result = try {
                chatGateway.send(
                    gameId = activeSession.gameId.ifBlank { context.gameId },
                    messages = history,
                    context = context
                )
            } catch (cancelled: CancellationException) {
                return@launch
            } catch (failure: Throwable) {
                Result.failure(failure)
            }

            if (activeRequestId != requestId) {
                log("stale chat result ignored: request replaced")
                return@launch
            }
            if (!QaSessionHolder.isCurrent(sessionGeneration)) {
                log("stale chat result ignored: session changed")
                finishRequest(requestId)
                _sendState.value = QaSendState.Idle
                return@launch
            }

            result.onSuccess { chat ->
                val replyText = chat.reply.trim().ifBlank { "（未收到回答）" }
                val debugSummary = qaAnswerDebugSummary(
                    chat,
                    elapsedMillis() - startedAtMs
                )
                log(debugSummary)
                val replyMessage = QaMessage(
                    role = QaMessage.ROLE_ASSISTANT,
                    content = replyText,
                    evidence = chat.evidence,
                    debugSummary = debugSummary
                )
                if (!QaSessionHolder.appendMessage(replyMessage, context, sessionGeneration)) {
                    log("stale chat reply ignored: session changed")
                    finishRequest(requestId)
                    _sendState.value = QaSendState.Idle
                    return@onSuccess
                }

                finishRequest(requestId)
                _sendState.value = QaSendState.Idle
                onAssistantReply(replyMessage)
            }.onFailure { error ->
                if (error is CancellationException) return@onFailure

                val errorMessage = QaMessage(
                    role = QaMessage.ROLE_ASSISTANT,
                    content = QA_NETWORK_ERROR_MESSAGE
                )
                if (!QaSessionHolder.appendMessage(errorMessage, context, sessionGeneration)) {
                    log("stale chat failure ignored: session changed")
                    finishRequest(requestId)
                    _sendState.value = QaSendState.Idle
                    return@onFailure
                }

                finishRequest(requestId)
                _sendState.value = QaSendState.NetworkError
            }
        }
        return true
    }

    /** Cancels the previous turn, then clears messages under a new generation. */
    fun startNewSession(context: QaContext) {
        cancelActiveRequest()
        QaSessionHolder.startNewSession(context)
        _sendState.value = QaSendState.Idle
        log("new QA chat session started")
    }

    /** Cancel transport and invalidate any late callback without clearing history. */
    fun cancelActiveRequest() {
        activeRequestId = null
        chatJob?.cancel()
        chatJob = null
        chatGateway.cancelActiveRequests()
        _sendState.value = QaSendState.Idle
    }

    /** Closing the panel cancels the request and releases transport resources. */
    fun close() {
        cancelActiveRequest()
    }

    private fun finishRequest(requestId: Long) {
        if (activeRequestId != requestId) return
        activeRequestId = null
        chatJob = null
    }
}
