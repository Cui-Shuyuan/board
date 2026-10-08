package com.boardai.tutorial.uaal.qa

import com.boardai.tutorial.uaal.voice.QaAnswerPlayer
import com.boardai.tutorial.uaal.voice.QaAsrEngine
import com.boardai.tutorial.uaal.voice.QaAudioRecorder
import com.boardai.tutorial.uaal.voice.QaTtsEngine
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.test.advanceUntilIdle
import kotlinx.coroutines.test.runCurrent
import kotlinx.coroutines.test.runTest
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.File
import java.io.IOException

@OptIn(ExperimentalCoroutinesApi::class)
class QaVoiceControllerTest {

    @Test
    fun defaultAutoTtsEnabledComesFromStore() = runTest {
        val controller = createController(
            scope = this,
            store = FakeAutoTtsStore(initial = true)
        )

        assertTrue(controller.state.value.autoTtsEnabled)
    }

    @Test
    fun togglePersistsAndStopsCurrentPlayback() = runTest {
        val ttsFile = tempFile("cached")
        val store = FakeAutoTtsStore(initial = true)
        val tts = FakeTtsEngine().apply { nextResult = Result.success(ttsFile) }
        val player = FakeAnswerPlayer()
        val controller = createController(
            scope = this,
            tts = tts,
            player = player,
            store = store
        )

        controller.replayAnswer(message())
        advanceUntilIdle()
        assertEquals(message().timestamp, controller.state.value.playingMessageId)
        val stopCallsBeforeToggle = player.stopCalls

        controller.setAutoTtsEnabled(false)

        assertFalse(store.enabledValue)
        assertEquals(1, store.setCalls)
        assertFalse(controller.state.value.autoTtsEnabled)
        assertEquals(stopCallsBeforeToggle + 1, player.stopCalls)
        assertNull(controller.state.value.playingMessageId)
    }

    @Test
    fun autoTtsOnSynthesizesAndPlaysOnce() = runTest {
        val ttsFile = tempFile("auto-on")
        val tts = FakeTtsEngine().apply { nextResult = Result.success(ttsFile) }
        val player = FakeAnswerPlayer()
        val controller = createController(scope = this, tts = tts, player = player)

        controller.onAssistantReply(message())
        advanceUntilIdle()

        assertEquals(1, tts.callCount)
        assertEquals(1, player.playCalls)
        assertEquals(ttsFile, player.playedFiles.single())
        assertFalse(controller.state.value.ttsBusy)
        assertNull(controller.state.value.voiceStatus)
    }

    @Test
    fun autoTtsOffSkipsSynthesisAndPlayback() = runTest {
        val tts = FakeTtsEngine()
        val player = FakeAnswerPlayer()
        val controller = createController(scope = this, tts = tts, player = player)

        controller.setAutoTtsEnabled(false)
        controller.onAssistantReply(message())
        advanceUntilIdle()

        assertEquals(0, tts.callCount)
        assertEquals(0, player.playCalls)
    }

    @Test
    fun manualReplayWorksWhenAutoTtsIsOff() = runTest {
        val ttsFile = tempFile("manual-replay")
        val tts = FakeTtsEngine().apply { nextResult = Result.success(ttsFile) }
        val player = FakeAnswerPlayer()
        val controller = createController(scope = this, tts = tts, player = player)

        controller.setAutoTtsEnabled(false)

        controller.replayAnswer(message())
        advanceUntilIdle()
        assertEquals(1, tts.callCount)
        assertEquals(1, player.playCalls)

        controller.replayAnswer(message())
        advanceUntilIdle()
        assertEquals(1, tts.callCount)
        assertEquals(2, player.playCalls)
    }

    @Test
    fun startNewSessionInterruptsInFlightAutoTts() = runTest {
        val ttsFile = tempFile("new-session")
        val deferred = CompletableDeferred<Result<File>>()
        val tts = FakeTtsEngine().apply { suspendCall = deferred }
        val player = FakeAnswerPlayer()
        val controller = createController(scope = this, tts = tts, player = player)

        controller.onAssistantReply(message())
        runCurrent()
        assertEquals(1, tts.callCount)
        assertEquals("正在合成语音…", controller.state.value.voiceStatus)

        controller.startNewSession()
        deferred.complete(Result.success(ttsFile))
        advanceUntilIdle()

        assertEquals(0, player.playCalls)
        assertNull(controller.state.value.voiceStatus)
        assertFalse(controller.state.value.ttsBusy)
    }

    @Test
    fun disablingAutoTtsWhileRequestInFlightSuppressesStaleFailure() = runTest {
        val deferred = CompletableDeferred<Result<File>>()
        val tts = FakeTtsEngine().apply { suspendCall = deferred }
        val player = FakeAnswerPlayer()
        val controller = createController(scope = this, tts = tts, player = player)

        controller.onAssistantReply(message())
        runCurrent()
        controller.setAutoTtsEnabled(false)
        deferred.complete(Result.failure(IOException("stale-tap")))
        advanceUntilIdle()

        assertNotEquals("语音合成失败，已保留文字回答", controller.state.value.voiceStatus)
        assertFalse(controller.state.value.ttsBusy)
    }

    @Test
    fun newSessionDropsInFlightAsrResultAndDeletesTempWav() = runTest {
        val recording = tempFile("stale-asr")
        val recorder = FakeAudioRecorder().apply { stopResult = recording }
        val deferred = CompletableDeferred<Result<String>>()
        val asr = FakeAsrEngine().apply { suspendCall = deferred }
        val recognized = mutableListOf<String>()
        val controller = createController(
            scope = this,
            recorder = recorder,
            asr = asr,
            hasPermission = { true },
            onRecognizedText = { recognized += it }
        )

        controller.onPttDown()
        controller.onPttUp()
        runCurrent()
        assertEquals(1, asr.files.size)

        controller.startNewSession()
        deferred.complete(Result.success("stale answer"))
        advanceUntilIdle()

        assertTrue(recognized.isEmpty())
        assertFalse(recording.exists())
    }

    @Test
    fun newRecordingDropsPreviousAsrResult() = runTest {
        val recording = tempFile("asr-interleaved")
        val recorder = FakeAudioRecorder().apply { stopResult = recording }
        val deferred = CompletableDeferred<Result<String>>()
        val asr = FakeAsrEngine().apply { suspendCall = deferred }
        val recognized = mutableListOf<String>()
        val controller = createController(
            scope = this,
            recorder = recorder,
            asr = asr,
            hasPermission = { true },
            onRecognizedText = { recognized += it }
        )

        controller.onPttDown()
        controller.onPttUp()
        runCurrent()

        controller.onPttDown()
        deferred.complete(Result.success("previous recording"))
        advanceUntilIdle()

        assertTrue(recognized.isEmpty())
        assertTrue(controller.state.value.isRecording)
    }

    @Test
    fun startingRecordingCancelsInFlightAutoTtsPlayback() = runTest {
        val ttsFile = tempFile("late-auto-tts")
        val deferred = CompletableDeferred<Result<File>>()
        val tts = FakeTtsEngine().apply { suspendCall = deferred }
        val player = FakeAnswerPlayer()
        val controller = createController(
            scope = this,
            tts = tts,
            player = player,
            hasPermission = { true }
        )

        controller.onAssistantReply(message())
        runCurrent()
        assertEquals("正在合成语音…", controller.state.value.voiceStatus)

        controller.onPttDown()
        deferred.complete(Result.success(ttsFile))
        advanceUntilIdle()

        assertEquals(0, player.playCalls)
        assertFalse(controller.state.value.ttsBusy)
        assertTrue(controller.state.value.isRecording)
    }

    @Test
    fun startNewSessionDeletesCachedAnswerAudio() = runTest {
        val ttsFile = tempFile("cached-answer")
        val tts = FakeTtsEngine().apply { nextResult = Result.success(ttsFile) }
        val controller = createController(scope = this, tts = tts, player = FakeAnswerPlayer())

        controller.replayAnswer(message())
        advanceUntilIdle()
        assertTrue(ttsFile.exists())

        controller.startNewSession()

        assertFalse(ttsFile.exists())
    }

    @Test
    fun pttWithoutPermissionRequestsPermissionAndUpdatesStatus() = runTest {
        val recorder = FakeAudioRecorder()
        val controller = createController(
            scope = this,
            recorder = recorder,
            hasPermission = { false }
        )

        assertEquals(QaPttStartResult.NEED_PERMISSION, controller.onPttDown())
        assertEquals(0, recorder.startCalls)
        assertEquals("需要麦克风权限", controller.state.value.voiceStatus)

        controller.onPermissionResult(true)
        assertEquals("权限已授予，请再次按住说话", controller.state.value.voiceStatus)

        controller.onPermissionResult(false)
        assertEquals("未授予麦克风权限，可键盘输入", controller.state.value.voiceStatus)
    }

    @Test
    fun pttNormalPathStopsRecorderAndFillsRecognizedText() = runTest {
        val recording = tempFile("recognized")
        val recorder = FakeAudioRecorder().apply { stopResult = recording }
        val asr = FakeAsrEngine().apply { result = Result.success("hello") }
        val recognized = mutableListOf<String>()
        val controller = createController(
            scope = this,
            recorder = recorder,
            asr = asr,
            hasPermission = { true },
            onRecognizedText = { recognized += it }
        )

        assertEquals(QaPttStartResult.STARTED, controller.onPttDown())
        assertTrue(controller.state.value.isRecording)
        assertEquals("正在聆听…", controller.state.value.voiceStatus)

        controller.onPttUp()
        advanceUntilIdle()

        assertEquals(1, recorder.stopCalls)
        assertEquals(listOf(recording), asr.files)
        assertEquals(listOf("hello"), recognized)
        assertFalse(controller.state.value.isRecording)
        assertNull(controller.state.value.voiceStatus)
    }

    @Test
    fun asrFailureShowsRecognitionError() = runTest {
        val recording = tempFile("asr-failure")
        val recorder = FakeAudioRecorder().apply { stopResult = recording }
        val asr = FakeAsrEngine().apply {
            result = Result.failure(IOException("asr-failed"))
        }
        val controller = createController(
            scope = this,
            recorder = recorder,
            asr = asr,
            hasPermission = { true }
        )

        assertEquals(QaPttStartResult.STARTED, controller.onPttDown())
        controller.onPttUp()
        advanceUntilIdle()

        assertEquals("识别失败，可键盘输入", controller.state.value.voiceStatus)
    }

    private fun createController(
        scope: CoroutineScope,
        recorder: FakeAudioRecorder = FakeAudioRecorder(),
        player: FakeAnswerPlayer = FakeAnswerPlayer(),
        asr: FakeAsrEngine = FakeAsrEngine(),
        tts: FakeTtsEngine = FakeTtsEngine(),
        store: FakeAutoTtsStore = FakeAutoTtsStore(),
        hasPermission: () -> Boolean = { true },
        onRecognizedText: (String) -> Unit = {}
    ): QaVoiceController = QaVoiceController(
        scope = scope,
        recorder = recorder,
        player = player,
        asr = asr,
        tts = tts,
        autoTtsStore = store,
        hasRecordPermission = hasPermission,
        onRecognizedText = onRecognizedText
    )

    private fun message(): QaMessage = QaMessage(
        role = QaMessage.ROLE_ASSISTANT,
        content = "测试回答",
        timestamp = 42L
    )

    private fun tempFile(prefix: String): File =
        File.createTempFile("qa-voice-$prefix", ".tmp").apply {
            writeText("test")
            deleteOnExit()
        }

    private class FakeAudioRecorder : QaAudioRecorder {
        var startCalls = 0
        var stopCalls = 0
        var cancelCalls = 0
        var startResult = true
        var stopResult: File? = null
        private var autoStop: ((File?) -> Unit)? = null

        override fun start(onAutoStop: (File?) -> Unit): Boolean {
            startCalls += 1
            autoStop = onAutoStop
            return startResult
        }

        override fun stop(): File? {
            stopCalls += 1
            return stopResult
        }

        override fun cancel() {
            cancelCalls += 1
        }

        fun triggerAutoStop(file: File?) {
            autoStop?.invoke(file)
        }
    }

    private class FakeAnswerPlayer : QaAnswerPlayer {
        var playCalls = 0
        var stopCalls = 0
        val playedFiles = mutableListOf<File>()

        override fun play(
            file: File,
            onCompletion: () -> Unit,
            onError: (Throwable) -> Unit
        ) {
            playCalls += 1
            playedFiles += file
        }

        override fun stop() {
            stopCalls += 1
        }
    }

    private class FakeAsrEngine : QaAsrEngine {
        val files = mutableListOf<File>()
        var result: Result<String> = Result.success("")
        var suspendCall: CompletableDeferred<Result<String>>? = null
        var cancelCalls = 0

        override suspend fun transcribe(wavFile: File): Result<String> {
            files += wavFile
            return suspendCall?.await() ?: result
        }

        override fun cancelActiveRequests() {
            cancelCalls += 1
        }
    }

    private class FakeTtsEngine : QaTtsEngine {
        var callCount = 0
        var nextResult: Result<File> = Result.failure(IOException("no result configured"))
        var suspendCall: CompletableDeferred<Result<File>>? = null
        var cancelCalls = 0

        override suspend fun synthesize(
            text: String,
            voice: String,
            speed: Double
        ): Result<File> {
            callCount += 1
            return suspendCall?.await() ?: nextResult
        }

        override fun cancelActiveRequests() {
            cancelCalls += 1
        }
    }

    private class FakeAutoTtsStore(initial: Boolean = true) : QaAutoTtsStore {
        var enabledValue = initial
        var setCalls = 0

        override fun isEnabled(): Boolean = enabledValue

        override fun setEnabled(enabled: Boolean) {
            enabledValue = enabled
            setCalls += 1
        }
    }
}
