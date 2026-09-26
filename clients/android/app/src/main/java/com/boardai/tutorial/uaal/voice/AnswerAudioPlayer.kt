package com.boardai.tutorial.uaal.voice

import android.media.MediaPlayer
import java.io.File
import java.io.IOException

/**
 * Tiny MediaPlayer wrapper for one answer mp3 at a time.
 *
 * Callbacks are invoked on the main thread by MediaPlayer.  stop() is safe to
 * call repeatedly and is used when the QA panel closes or "继续播放" is tapped.
 */
class AnswerAudioPlayer : QaAnswerPlayer {
    private var player: MediaPlayer? = null

    val isPlaying: Boolean
        get() = try {
            player?.isPlaying == true
        } catch (_: Throwable) {
            false
        }

    override fun play(
        file: File,
        onCompletion: () -> Unit,
        onError: (Throwable) -> Unit
    ) {
        stop()
        try {
            val mediaPlayer = MediaPlayer()
            mediaPlayer.setDataSource(file.absolutePath)
            mediaPlayer.setOnPreparedListener { prepared ->
                prepared.start()
            }
            mediaPlayer.setOnCompletionListener {
                releaseCurrent()
                onCompletion()
            }
            mediaPlayer.setOnErrorListener { _, what, extra ->
                releaseCurrent()
                onError(IOException("MediaPlayer error $what/$extra"))
                true
            }
            mediaPlayer.prepareAsync()
            player = mediaPlayer
        } catch (t: Throwable) {
            stop()
            onError(t)
        }
    }

    override fun stop() {
        val current = player
        player = null
        try {
            current?.stop()
        } catch (_: Throwable) {
        }
        try {
            current?.release()
        } catch (_: Throwable) {
        }
    }

    private fun releaseCurrent() {
        val current = player
        player = null
        try {
            current?.release()
        } catch (_: Throwable) {
        }
    }
}
