package com.boardai.tutorial.uaal.qa

import com.boardai.tutorial.uaal.voice.DEFAULT_TTS_VOICE
import com.boardai.tutorial.uaal.voice.QaAnswerPlayer
import com.boardai.tutorial.uaal.voice.QaAsrEngine
import com.boardai.tutorial.uaal.voice.QaAudioRecorder
import com.boardai.tutorial.uaal.voice.QaTtsEngine
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Job
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch
import java.io.File

enum class QaPttStartResult {
    STARTED,
    ALREADY_RECORDING,
    NEED_PERMISSION,
    START_FAILED
}

class QaVoiceController(
    private val scope: CoroutineScope,
    private val recorder: QaAudioRecorder,
    private val player: QaAnswerPlayer,
    private val asr: QaAsrEngine,
    private val tts: QaTtsEngine,
    private val autoTtsStore: QaAutoTtsStore,
    private val hasRecordPermission: () -> Boolean,
    private val onRecognizedText: (String) -> Unit,
    private val log: (String) -> Unit = {}
) {
    private val _state = MutableStateFlow(
        QaVoiceState(autoTtsEnabled = autoTtsStore.isEnabled())
    )
    val state: StateFlow<QaVoiceState> = _state.asStateFlow()

    private var ttsGeneration = 0
    private var ttsRequestSeq = 0L
    private var activeTtsRequestId: Long? = null
    private var ttsMessageInFlight: Long? = null
    private var answerAudioFiles: Map<Long, File> = emptyMap()
    private var recordingToken = 0
    private var asrGeneration = 0L
    private var asrJob: Job? = null
    private var ttsJob: Job? = null

    fun onPttDown(): QaPttStartResult {
        log("PTT down: isRecording=${_state.value.isRecording}")
        if (_state.value.isRecording) return QaPttStartResult.ALREADY_RECORDING
        stopPlayback()
        cancelAsrRequest()
        cancelTtsRequest()
        setVoiceStatus(null)

        if (!hasRecordPermission()) {
            log("PTT down without RECORD_AUDIO permission")
            setVoiceStatus("需要麦克风权限")
            return QaPttStartResult.NEED_PERMISSION
        }

        log("PTT down with RECORD_AUDIO permission")
        return beginRecording()
    }

    fun onPermissionResult(granted: Boolean) {
        setVoiceStatus(
            if (granted) {
                "权限已授予，请再次按住说话"
            } else {
                "未授予麦克风权限，可键盘输入"
            }
        )
    }

    fun onPttUp() {
        log("PTT up: isRecording=${_state.value.isRecording}")
        if (!_state.value.isRecording) return
        log("PTT recording stopping")
        setRecording(false)
        recordingToken += 1
        handleRecorded(recorder.stop())
    }

    fun setAutoTtsEnabled(enabled: Boolean) {
        _state.value = _state.value.copy(autoTtsEnabled = enabled)
        autoTtsStore.setEnabled(enabled)
        cancelTtsRequest()

        if (!enabled) {
            stopPlayback()
        }

        log("auto TTS enabled=$enabled")
    }

    fun onAssistantReply(message: QaMessage) {
        if (message.content == "（未收到回答）") return

        if (_state.value.autoTtsEnabled) {
            synthesizeAndPlay(message, autoPlay = true)
        } else {
            log("auto TTS disabled; skip auto playback")
        }
    }

    fun replayAnswer(message: QaMessage) {
        val existing = answerAudioFiles[message.timestamp]
        if (existing != null && existing.exists()) {
            playAnswerFile(message, existing)
        } else {
            synthesizeAndPlay(message, autoPlay = false)
        }
    }

    fun startNewSession() {
        cancelAsrRequest()
        cancelTtsRequest()
        recorder.cancel()
        setRecording(false)
        stopPlayback()
        clearAnswerAudioFiles()
        setVoiceStatus(null)
        log("new QA voice session started")
    }

    fun stopPlayback() {
        player.stop()
        setPlayingMessageId(null)
    }

    fun close() {
        cancelAsrRequest()
        cancelTtsRequest()
        recorder.cancel()
        setRecording(false)
        stopPlayback()
        clearAnswerAudioFiles()
    }

    private fun cancelAsrRequest() {
        asrGeneration += 1
        asrJob?.cancel()
        asrJob = null
        asr.cancelActiveRequests()
        if (_state.value.voiceStatus == "识别中…") {
            setVoiceStatus(null)
        }
    }

    private fun cancelTtsRequest() {
        ttsGeneration += 1
        ttsJob?.cancel()
        ttsJob = null
        activeTtsRequestId = null
        ttsMessageInFlight = null
        tts.cancelActiveRequests()
        setTtsBusy(false)
        if (isTtsVoiceStatus(_state.value.voiceStatus)) {
            setVoiceStatus(null)
        }
    }

    private fun clearAnswerAudioFiles() {
        answerAudioFiles.values.forEach { file -> runCatching { file.delete() } }
        answerAudioFiles = emptyMap()
    }

    private fun beginRecording(): QaPttStartResult {
        log("beginRecording requested, isRecording=${_state.value.isRecording}")
        if (_state.value.isRecording) return QaPttStartResult.ALREADY_RECORDING
        stopPlayback()

        val token = recordingToken + 1
        recordingToken = token
        val started = recorder.start { file ->
            if (recordingToken == token) {
                setRecording(false)
                recordingToken += 1
                handleRecorded(file)
            }
        }
        log("audioRecorder.start returned started=$started")

        return if (started) {
            setRecording(true)
            setVoiceStatus("正在聆听…")
            QaPttStartResult.STARTED
        } else {
            setVoiceStatus("录音启动失败，请重试")
            QaPttStartResult.START_FAILED
        }
    }

    private fun handleRecorded(file: File?) {
        if (file == null) {
            log("recording dropped: too short or invalid")
            setVoiceStatus("录音太短，请按住多说一会儿")
            return
        }

        log("ASR request starting, wavBytes=${file.length()}")
        setVoiceStatus("识别中…")
        val requestGeneration = asrGeneration
        asrJob = scope.launch {
            try {
                asr.transcribe(file).onSuccess { recognized ->
                    if (requestGeneration != asrGeneration) {
                        log("stale ASR result ignored: new recording/session")
                        return@onSuccess
                    }

                    val text = recognized.trim()
                    if (text.isBlank()) {
                        log("ASR success but text blank")
                        setVoiceStatus("没有识别到内容，请重试")
                    } else {
                        log("ASR success: textLength=${text.length}")
                        onRecognizedText(text)
                        setVoiceStatus(null)
                    }
                }.onFailure { error ->
                    if (requestGeneration != asrGeneration) {
                        log("stale ASR failure ignored: new recording/session")
                        return@onFailure
                    }

                    log("ASR failed: ${error.javaClass.simpleName}: ${error.message}")
                    setVoiceStatus("识别失败，可键盘输入")
                }
            } finally {
                val deleted = file.delete()
                log("ASR temp wav deleted=$deleted")
            }
        }
    }

    private fun playAnswerFile(message: QaMessage, file: File) {
        stopPlayback()
        setPlayingMessageId(message.timestamp)
        player.play(
            file = file,
            onCompletion = {
                if (_state.value.playingMessageId == message.timestamp) {
                    setPlayingMessageId(null)
                }
            },
            onError = {
                if (_state.value.playingMessageId == message.timestamp) {
                    setPlayingMessageId(null)
                }
                setVoiceStatus("语音播放失败，已保留文字回答")
            }
        )
    }

    private fun synthesizeAndPlay(message: QaMessage, autoPlay: Boolean) {
        if (autoPlay && !_state.value.autoTtsEnabled) {
            log("auto TTS disabled; skip auto playback")
            return
        }

        val existing = answerAudioFiles[message.timestamp]
        if (existing != null && existing.exists()) {
            if (!autoPlay || _state.value.autoTtsEnabled) {
                playAnswerFile(message, existing)
            } else {
                log("auto playback skipped: toggle off")
            }
            return
        }

        if (ttsMessageInFlight == message.timestamp) return

        val requestId = ++ttsRequestSeq
        val requestGeneration = ttsGeneration
        activeTtsRequestId = requestId
        ttsMessageInFlight = message.timestamp
        setTtsBusy(true)
        setVoiceStatus("正在合成语音…")
        if (autoPlay) {
            log("auto TTS request starting, textLength=${message.content.length}")
        }

        ttsJob = scope.launch {
            tts.synthesize(
                text = message.content,
                voice = DEFAULT_TTS_VOICE,
                speed = 1.0
            ).onSuccess { file ->
                val isOwner = activeTtsRequestId == requestId
                val generationCurrent = requestGeneration == ttsGeneration

                if (isOwner) {
                    activeTtsRequestId = null
                    ttsMessageInFlight = null
                    setTtsBusy(false)
                    ttsJob = null
                    if (_state.value.voiceStatus == "正在合成语音…") {
                        setVoiceStatus(null)
                    }
                }

                if (isOwner && generationCurrent) {
                    answerAudioFiles = answerAudioFiles + (message.timestamp to file)
                    val canPlay = !autoPlay || _state.value.autoTtsEnabled
                    if (canPlay) {
                        playAnswerFile(message, file)
                    } else {
                        log("auto playback skipped after synthesis: toggle off")
                    }
                } else {
                    val deleted = file.delete()
                    log(
                        "stale TTS synthesis ignored: autoPlay=$autoPlay owner=$isOwner generationCurrent=$generationCurrent deleted=$deleted"
                    )
                }
            }.onFailure {
                val isOwner = activeTtsRequestId == requestId

                if (isOwner) {
                    activeTtsRequestId = null
                    ttsMessageInFlight = null
                    setTtsBusy(false)
                    ttsJob = null
                }

                if (isOwner && requestGeneration == ttsGeneration) {
                    setVoiceStatus("语音合成失败，已保留文字回答")
                } else {
                    log(
                        "stale TTS failure ignored: owner=$isOwner generationCurrent=${requestGeneration == ttsGeneration}"
                    )
                }
            }
        }
    }

    private fun setVoiceStatus(value: String?) {
        _state.value = _state.value.copy(voiceStatus = value)
    }

    private fun setRecording(value: Boolean) {
        _state.value = _state.value.copy(isRecording = value)
    }

    private fun setTtsBusy(value: Boolean) {
        _state.value = _state.value.copy(ttsBusy = value)
    }

    private fun setPlayingMessageId(value: Long?) {
        _state.value = _state.value.copy(playingMessageId = value)
    }

    private fun isTtsVoiceStatus(text: String?): Boolean =
        text == "正在合成语音…" ||
            text == "语音合成失败，已保留文字回答" ||
            text == "语音播放失败，已保留文字回答"
}
