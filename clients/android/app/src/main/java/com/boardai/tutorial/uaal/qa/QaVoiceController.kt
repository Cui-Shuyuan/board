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
        log("PTT down: recording=${_state.value.recording}")
        if (_state.value.recording == QaRecordingState.RECORDING) {
            return QaPttStartResult.ALREADY_RECORDING
        }
        stopPlayback()
        cancelAsrRequest()
        cancelTtsRequest()
        clearVoiceNotice()

        if (!hasRecordPermission()) {
            log("PTT down without RECORD_AUDIO permission")
            setVoiceNotice(QaVoiceNotice.PERMISSION_REQUIRED)
            return QaPttStartResult.NEED_PERMISSION
        }

        log("PTT down with RECORD_AUDIO permission")
        return beginRecording()
    }

    fun onPermissionResult(granted: Boolean) {
        setVoiceNotice(
            if (granted) {
                QaVoiceNotice.PERMISSION_GRANTED
            } else {
                QaVoiceNotice.PERMISSION_DENIED
            }
        )
    }

    fun onPttUp() {
        log("PTT up: recording=${_state.value.recording}")
        if (_state.value.recording != QaRecordingState.RECORDING) return
        log("PTT recording stopping")
        setRecordingState(QaRecordingState.IDLE)
        recordingToken += 1
        handleRecorded(recorder.stop())
    }

    /**
     * The guest released the PTT button inside the upstream cancel target.
     * Discard the recording before [handleRecorded] can start an ASR request.
     */
    fun cancelRecording() {
        log("PTT cancelled before recognition: recording=${_state.value.recording}")
        if (_state.value.recording != QaRecordingState.RECORDING) return
        recordingToken += 1
        recorder.cancel()
        setRecordingState(QaRecordingState.IDLE)
        setVoiceNotice(QaVoiceNotice.RECORDING_CANCELLED)
    }

    /**
     * Cancel an already released recording that is waiting for ASR. The
     * generation bump guarantees a late transcription cannot auto-send.
     */
    fun cancelRecognition() {
        log("ASR recognition cancelled")
        cancelAsrRequest()
        setVoiceNotice(QaVoiceNotice.RECORDING_CANCELLED)
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
        setRecordingState(QaRecordingState.IDLE)
        stopPlayback()
        clearAnswerAudioFiles()
        clearVoiceNotice()
        log("new QA voice session started")
    }

    fun stopPlayback() {
        player.stop()
        setPlaybackState(QaPlaybackState.Idle)
    }

    fun clearVoiceNotice(expected: QaVoiceNotice? = null) {
        if (expected == null || _state.value.notice == expected) {
            setVoiceNotice(null)
        }
    }

    fun close() {
        cancelAsrRequest()
        cancelTtsRequest()
        recorder.cancel()
        setRecordingState(QaRecordingState.IDLE)
        stopPlayback()
        clearAnswerAudioFiles()
        clearVoiceNotice()
    }

    private fun cancelAsrRequest() {
        asrGeneration += 1
        asrJob?.cancel()
        asrJob = null
        asr.cancelActiveRequests()
        setAsrState(QaAsrState.IDLE)
    }

    private fun cancelTtsRequest() {
        ttsGeneration += 1
        ttsJob?.cancel()
        ttsJob = null
        activeTtsRequestId = null
        ttsMessageInFlight = null
        tts.cancelActiveRequests()
        setTtsState(QaTtsState.IDLE)
        if (isTtsNotice(_state.value.notice)) {
            setVoiceNotice(null)
        }
    }

    private fun clearAnswerAudioFiles() {
        answerAudioFiles.values.forEach { file -> runCatching { file.delete() } }
        answerAudioFiles = emptyMap()
    }

    private fun beginRecording(): QaPttStartResult {
        log("beginRecording requested, recording=${_state.value.recording}")
        if (_state.value.recording == QaRecordingState.RECORDING) {
            return QaPttStartResult.ALREADY_RECORDING
        }
        stopPlayback()

        val token = recordingToken + 1
        recordingToken = token
        val started = recorder.start { file ->
            if (recordingToken == token) {
                setRecordingState(QaRecordingState.IDLE)
                recordingToken += 1
                handleRecorded(file)
            }
        }
        log("audioRecorder.start returned started=$started")

        return if (started) {
            setRecordingState(QaRecordingState.RECORDING)
            setVoiceNotice(null)
            QaPttStartResult.STARTED
        } else {
            setVoiceNotice(QaVoiceNotice.RECORDING_START_FAILED)
            QaPttStartResult.START_FAILED
        }
    }

    private fun handleRecorded(file: File?) {
        if (file == null) {
            log("recording dropped: too short or invalid")
            setVoiceNotice(QaVoiceNotice.RECORDING_TOO_SHORT)
            return
        }

        log("ASR request starting, wavBytes=${file.length()}")
        setAsrState(QaAsrState.RECOGNIZING)
        setVoiceNotice(null)
        val requestGeneration = asrGeneration
        asrJob = scope.launch {
            try {
                asr.transcribe(file).onSuccess { recognized ->
                    if (requestGeneration != asrGeneration) {
                        log("stale ASR result ignored: new recording/session")
                        return@onSuccess
                    }

                    setAsrState(QaAsrState.IDLE)
                    val text = recognized.trim()
                    if (text.isBlank()) {
                        log("ASR success but text blank")
                        setVoiceNotice(QaVoiceNotice.RECOGNITION_EMPTY)
                    } else {
                        log("ASR success: textLength=${text.length}")
                        onRecognizedText(text)
                    }
                }.onFailure { error ->
                    if (requestGeneration != asrGeneration) {
                        log("stale ASR failure ignored: new recording/session")
                        return@onFailure
                    }

                    setAsrState(QaAsrState.IDLE)
                    log("ASR failed: ${error.javaClass.simpleName}: ${error.message}")
                    setVoiceNotice(QaVoiceNotice.RECOGNITION_FAILED)
                }
            } finally {
                val deleted = file.delete()
                log("ASR temp wav deleted=$deleted")
            }
        }
    }

    private fun playAnswerFile(message: QaMessage, file: File) {
        stopPlayback()
        setPlaybackState(QaPlaybackState.Playing(message.timestamp))
        player.play(
            file = file,
            onCompletion = {
                if (_state.value.playback == QaPlaybackState.Playing(message.timestamp)) {
                    setPlaybackState(QaPlaybackState.Idle)
                }
            },
            onError = {
                if (_state.value.playback == QaPlaybackState.Playing(message.timestamp)) {
                    setPlaybackState(QaPlaybackState.Idle)
                    setVoiceNotice(QaVoiceNotice.SPEECH_PLAYBACK_FAILED)
                } else {
                    log("stale playback error ignored: messageId=${message.timestamp}")
                }
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
        setTtsState(QaTtsState.SYNTHESIZING)
        setVoiceNotice(null)
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
                    ttsJob = null
                    setTtsState(QaTtsState.IDLE)
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
                        "stale TTS synthesis ignored: autoPlay=$autoPlay owner=$isOwner " +
                            "generationCurrent=$generationCurrent deleted=$deleted"
                    )
                }
            }.onFailure {
                val isOwner = activeTtsRequestId == requestId

                if (isOwner) {
                    activeTtsRequestId = null
                    ttsMessageInFlight = null
                    ttsJob = null
                    setTtsState(QaTtsState.IDLE)
                }

                if (isOwner && requestGeneration == ttsGeneration) {
                    setVoiceNotice(QaVoiceNotice.SPEECH_SYNTHESIS_FAILED)
                } else {
                    log(
                        "stale TTS failure ignored: owner=$isOwner " +
                            "generationCurrent=${requestGeneration == ttsGeneration}"
                    )
                }
            }
        }
    }

    private fun isTtsNotice(notice: QaVoiceNotice?): Boolean =
        notice == QaVoiceNotice.SPEECH_SYNTHESIS_FAILED ||
            notice == QaVoiceNotice.SPEECH_PLAYBACK_FAILED

    private fun setVoiceNotice(value: QaVoiceNotice?) {
        _state.value = _state.value.copy(notice = value)
    }

    private fun setRecordingState(value: QaRecordingState) {
        _state.value = _state.value.copy(recording = value)
    }

    private fun setAsrState(value: QaAsrState) {
        _state.value = _state.value.copy(asr = value)
    }

    private fun setTtsState(value: QaTtsState) {
        _state.value = _state.value.copy(tts = value)
    }

    private fun setPlaybackState(value: QaPlaybackState) {
        _state.value = _state.value.copy(playback = value)
    }
}
