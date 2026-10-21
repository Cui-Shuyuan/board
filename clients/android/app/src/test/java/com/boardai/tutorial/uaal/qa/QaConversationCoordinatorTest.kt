package com.boardai.tutorial.uaal.qa

import com.boardai.tutorial.uaal.voice.QaAnswerPlayer
import com.boardai.tutorial.uaal.voice.QaAsrEngine
import com.boardai.tutorial.uaal.voice.QaAudioRecorder
import com.boardai.tutorial.uaal.voice.QaTtsEngine
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.test.advanceUntilIdle
import kotlinx.coroutines.test.runCurrent
import kotlinx.coroutines.test.runTest
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotEquals
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test
import java.io.File
import java.io.IOException

@OptIn(ExperimentalCoroutinesApi::class)
class QaConversationCoordinatorTest {

    @Before
    fun setUp() {
        QaSessionHolder.clear()
    }

    @After
    fun tearDown() {
        QaSessionHolder.clear()
    }

    @Test
    fun normalTextQuestionAppendsUserAndAssistantTurns() = runTest {
        val gateway = FakeChatGateway(
            result = Result.success(QaChatResult(reply = " 正常回答 "))
        )
        var deliveredReply: QaMessage? = null
        val coordinator = QaConversationCoordinator(
            scope = this,
            chatGateway = gateway,
            onAssistantReply = { deliveredReply = it }
        )

        assertTrue(coordinator.sendQuestion("  正常问题  ", context()))
        advanceUntilIdle()

        val messages = QaSessionHolder.session?.messages.orEmpty()
        assertEquals(2, messages.size)
        assertEquals(QaMessage.ROLE_USER, messages[0].role)
        assertEquals("正常问题", messages[0].content)
        assertEquals(QaMessage.ROLE_ASSISTANT, messages[1].role)
        assertEquals("正常回答", messages[1].content)
        assertEquals(messages[1], deliveredReply)
        assertEquals(QaSendState.Idle, coordinator.sendState.value)
        assertEquals("正常问题", gateway.sent.single().messages.single().content)
    }

    @Test
    fun recognizedVoiceTextAutoSendsThroughTheSameCoordinatorPath() = runTest {
        val gateway = FakeChatGateway(
            result = Result.success(QaChatResult(reply = "语音回答"))
        )
        lateinit var coordinator: QaConversationCoordinator
        coordinator = QaConversationCoordinator(
            scope = this,
            chatGateway = gateway,
            onAssistantReply = {}
        )
        val recording = File.createTempFile("qa-voice-auto", ".tmp").apply {
            writeText("wav")
            deleteOnExit()
        }
        val recorder = FixedRecorder(recording)
        val voiceController = QaVoiceController(
            scope = this,
            recorder = recorder,
            player = NoopAnswerPlayer(),
            asr = FixedAsr(Result.success("语音问题")),
            tts = NoopTtsEngine(),
            autoTtsStore = FixedAutoTtsStore(false),
            hasRecordPermission = { true },
            onRecognizedText = { recognized ->
                coordinator.sendQuestion(recognized, context())
            }
        )

        assertEquals(QaPttStartResult.STARTED, voiceController.onPttDown())
        voiceController.onPttUp()
        advanceUntilIdle()

        val messages = QaSessionHolder.session?.messages.orEmpty()
        assertEquals(2, messages.size)
        assertEquals("语音问题", messages[0].content)
        assertEquals("语音回答", messages[1].content)
        assertEquals(1, gateway.sent.size)
    }

    @Test
    fun switchingSessionBeforeChatReplyDropsTheOldReply() = runTest {
        val deferred = CompletableDeferred<Result<QaChatResult>>()
        val gateway = FakeChatGateway(suspendResult = deferred)
        val coordinator = QaConversationCoordinator(
            scope = this,
            chatGateway = gateway,
            onAssistantReply = {}
        )

        assertTrue(coordinator.sendQuestion("旧问题", context(cueId = "old")))
        runCurrent()
        val oldGeneration = QaSessionHolder.session!!.generation
        assertEquals(QaSendState.Sending, coordinator.sendState.value)

        coordinator.startNewSession(context(cueId = "new"))
        assertNotEquals(oldGeneration, QaSessionHolder.session!!.generation)
        deferred.complete(Result.success(QaChatResult(reply = "旧回答")))
        advanceUntilIdle()

        assertTrue(QaSessionHolder.session!!.messages.isEmpty())
        assertTrue(gateway.cancelCalls > 0)
        assertEquals(QaSendState.Idle, coordinator.sendState.value)
    }

    @Test
    fun closingPanelCancelsPendingChatAndIgnoresItsLateReply() = runTest {
        val deferred = CompletableDeferred<Result<QaChatResult>>()
        val gateway = FakeChatGateway(suspendResult = deferred)
        val coordinator = QaConversationCoordinator(
            scope = this,
            chatGateway = gateway,
            onAssistantReply = {}
        )

        assertTrue(coordinator.sendQuestion("关闭前问题", context()))
        runCurrent()
        coordinator.close()
        deferred.complete(Result.success(QaChatResult(reply = "晚到回答")))
        advanceUntilIdle()

        assertTrue(gateway.cancelCalls > 0)
        val messages = QaSessionHolder.session?.messages.orEmpty()
        assertEquals(1, messages.size)
        assertEquals(QaMessage.ROLE_USER, messages.single().role)
        assertEquals(QaSendState.Idle, coordinator.sendState.value)
    }

    private fun context(cueId: String = "cue.1"): QaContext = QaContext(
        gameId = "testgame",
        gameName = "测试游戏",
        cueId = cueId,
        cueIndex = 0,
        cueText = "",
        groupPath = emptyList(),
        positionInCue = 0f
    )

    private data class SentRequest(
        val gameId: String,
        val messages: List<QaMessage>,
        val context: QaContext
    )

    private class FakeChatGateway(
        private var result: Result<QaChatResult> = Result.failure(IOException("not configured")),
        private val suspendResult: CompletableDeferred<Result<QaChatResult>>? = null
    ) : QaChatGateway {
        val sent = mutableListOf<SentRequest>()
        var cancelCalls = 0

        override suspend fun send(
            gameId: String,
            messages: List<QaMessage>,
            context: QaContext
        ): Result<QaChatResult> {
            sent += SentRequest(gameId, messages, context)
            return suspendResult?.await() ?: result
        }

        override fun cancelActiveRequests() {
            cancelCalls += 1
        }
    }

    private class FixedRecorder(private val file: File) : QaAudioRecorder {
        override fun start(onAutoStop: (File?) -> Unit): Boolean = true
        override fun stop(): File = file
        override fun cancel() = Unit
    }

    private class FixedAsr(private val result: Result<String>) : QaAsrEngine {
        override suspend fun transcribe(wavFile: File): Result<String> = result
        override fun cancelActiveRequests() = Unit
    }

    private class NoopTtsEngine : QaTtsEngine {
        override suspend fun synthesize(
            text: String,
            voice: String,
            speed: Double
        ): Result<File> = Result.failure(IOException("unused"))

        override fun cancelActiveRequests() = Unit
    }

    private class NoopAnswerPlayer : QaAnswerPlayer {
        override fun play(
            file: File,
            onCompletion: () -> Unit,
            onError: (Throwable) -> Unit
        ) = Unit

        override fun stop() = Unit
    }

    private class FixedAutoTtsStore(initial: Boolean) : QaAutoTtsStore {
        private var enabled = initial
        override fun isEnabled(): Boolean = enabled
        override fun setEnabled(enabled: Boolean) {
            this.enabled = enabled
        }
    }
}
