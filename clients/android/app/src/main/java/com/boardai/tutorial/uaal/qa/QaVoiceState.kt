package com.boardai.tutorial.uaal.qa

data class QaVoiceState(
    val voiceStatus: String? = null,
    val isRecording: Boolean = false,
    val autoTtsEnabled: Boolean = true,
    val ttsBusy: Boolean = false,
    val playingMessageId: Long? = null
)
