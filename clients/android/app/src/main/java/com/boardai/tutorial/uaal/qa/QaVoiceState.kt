package com.boardai.tutorial.uaal.qa

/** Recording lifecycle. It is independent from ASR/TTS/playback lifecycles. */
enum class QaRecordingState {
    IDLE,
    RECORDING
}

/** A released recording can be transcribed while other audio state is idle. */
enum class QaAsrState {
    IDLE,
    RECOGNIZING
}

/** Synthesis can overlap with recognition only when the caller explicitly permits it. */
enum class QaTtsState {
    IDLE,
    SYNTHESIZING
}

/** Playback is tied to one answer message, not to the recording/synthesis state. */
sealed interface QaPlaybackState {
    data object Idle : QaPlaybackState
    data class Playing(val messageId: Long) : QaPlaybackState
}

/** Transient user-facing notices; text is mapped in the UI layer. */
enum class QaVoiceNotice {
    PERMISSION_REQUIRED,
    PERMISSION_GRANTED,
    PERMISSION_DENIED,
    RECORDING_CANCELLED,
    RECORDING_TOO_SHORT,
    RECORDING_START_FAILED,
    RECOGNITION_EMPTY,
    RECOGNITION_FAILED,
    SPEECH_SYNTHESIS_FAILED,
    SPEECH_PLAYBACK_FAILED
}

data class QaVoiceState(
    val recording: QaRecordingState = QaRecordingState.IDLE,
    val asr: QaAsrState = QaAsrState.IDLE,
    val tts: QaTtsState = QaTtsState.IDLE,
    val playback: QaPlaybackState = QaPlaybackState.Idle,
    val autoTtsEnabled: Boolean = true,
    val notice: QaVoiceNotice? = null
)

/** Presentation-only mapping from typed business state to Chinese UI copy. */
internal fun qaVoiceNoticeText(notice: QaVoiceNotice?): String? = when (notice) {
    QaVoiceNotice.PERMISSION_REQUIRED -> "需要麦克风权限"
    QaVoiceNotice.PERMISSION_GRANTED -> "权限已授予，请再次按住说话"
    QaVoiceNotice.PERMISSION_DENIED -> "未授予麦克风权限，可键盘输入"
    QaVoiceNotice.RECORDING_CANCELLED -> "已取消"
    QaVoiceNotice.RECORDING_TOO_SHORT -> "录音太短，请按住多说一会儿"
    QaVoiceNotice.RECORDING_START_FAILED -> "录音启动失败，请重试"
    QaVoiceNotice.RECOGNITION_EMPTY -> "没有识别到内容，请重试"
    QaVoiceNotice.RECOGNITION_FAILED -> "识别失败，可键盘输入"
    QaVoiceNotice.SPEECH_SYNTHESIS_FAILED -> "语音合成失败，已保留文字回答"
    QaVoiceNotice.SPEECH_PLAYBACK_FAILED -> "语音播放失败，已保留文字回答"
    null -> null
}
