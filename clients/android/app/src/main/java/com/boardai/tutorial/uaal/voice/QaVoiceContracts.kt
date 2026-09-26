package com.boardai.tutorial.uaal.voice

import java.io.File

interface QaAudioRecorder {
    fun start(onAutoStop: (File?) -> Unit): Boolean
    fun stop(): File?
    fun cancel()
}

interface QaAnswerPlayer {
    fun play(
        file: File,
        onCompletion: () -> Unit,
        onError: (Throwable) -> Unit
    )
    fun stop()
}

interface QaAsrEngine {
    suspend fun transcribe(wavFile: File): Result<String>
}

interface QaTtsEngine {
    suspend fun synthesize(
        text: String,
        voice: String,
        speed: Double
    ): Result<File>
}
