package com.boardai.tutorial.uaal.voice

import android.annotation.SuppressLint
import android.content.Context
import android.media.AudioFormat
import android.media.AudioRecord
import android.media.MediaRecorder
import android.os.Handler
import android.os.Looper
import android.util.Log
import java.io.File
import java.io.FileOutputStream
import kotlin.math.max

/**
 * Small blocking AudioRecord wrapper.
 *
 * It records 16 kHz / 16-bit / mono PCM into a temp file, enforces a hard
 * 20-second limit, rejects clips shorter than 300 ms, and wraps the final PCM
 * with a standard WAV header in cacheDir/voice.
 */
class AudioRecorder(private val context: Context) : QaAudioRecorder {

    private val mainHandler = Handler(Looper.getMainLooper())
    private val lock = Any()

    @Volatile
    private var recording = false

    @Volatile
    private var stopRequested = false

    private var audioRecord: AudioRecord? = null
    private var worker: Thread? = null
    private var pcmFile: File? = null
    private var wavFile: File? = null
    private var onAutoStop: ((File?) -> Unit)? = null

    private val autoStopRunnable = Runnable {
        finishRecording(notifyCallback = true)
    }

    /** Returns false when a recording is already active or AudioRecord fails. */
    @SuppressLint("MissingPermission")
    override fun start(onAutoStop: (File?) -> Unit): Boolean {
        synchronized(lock) {
            if (recording) {
                Log.d(TAG, "start ignored: already recording")
                return false
            }

            Log.d(TAG, "start requested")
            val voiceDir = File(context.cacheDir, "voice").apply { mkdirs() }
            val stamp = System.currentTimeMillis()
            val pcm = File(voiceDir, "rec-$stamp.pcm")
            val wav = File(voiceDir, "rec-$stamp.wav")
            val choice = createAudioRecord() ?: run {
                Log.w(TAG, "start failed: no usable AudioRecord source")
                pcm.delete()
                wav.delete()
                return false
            }
            val record = choice.record
            val sourceName = audioSourceName(choice.source)
            Log.d(TAG, "start selected AudioSource=$sourceName")

            try {
                record.startRecording()
                if (record.recordingState != AudioRecord.RECORDSTATE_RECORDING) {
                    Log.w(
                        TAG,
                        "start failed: source=$sourceName recordingState=${record.recordingState}"
                    )
                    record.release()
                    pcm.delete()
                    wav.delete()
                    return false
                }
            } catch (t: Throwable) {
                Log.w(TAG, "start failed: source=$sourceName", t)
                try {
                    record.release()
                } catch (_: Throwable) {
                }
                pcm.delete()
                wav.delete()
                return false
            }

            audioRecord = record
            pcmFile = pcm
            wavFile = wav
            this.onAutoStop = onAutoStop
            stopRequested = false
            recording = true

            val thread = Thread {
                recordLoop(record, pcm)
            }.apply {
                isDaemon = true
                name = "BoardAI-AudioRecorder"
            }
            worker = thread
            thread.start()

            mainHandler.postDelayed(autoStopRunnable, MAX_DURATION_MS)
            Log.i(TAG, "start succeeded: source=$sourceName pcm=${pcm.name}")
            return true
        }
    }

    /** Stops the current recording and returns a valid WAV, or null if it was too short. */
    override fun stop(): File? {
        Log.d(TAG, "stop requested")
        return finishRecording(notifyCallback = false)
    }

    /** Stops and deletes the partial recording. */
    override fun cancel() {
        finishRecording(notifyCallback = false)?.delete()
    }

    private fun finishRecording(notifyCallback: Boolean): File? {
        var callback: ((File?) -> Unit)? = null
        var pcm: File? = null
        var wav: File? = null
        var record: AudioRecord? = null
        var thread: Thread? = null

        synchronized(lock) {
            if (!recording) return null

            Log.d(TAG, "finishRecording started notifyCallback=$notifyCallback")
            recording = false
            stopRequested = true
            mainHandler.removeCallbacks(autoStopRunnable)

            callback = onAutoStop
            onAutoStop = null

            record = audioRecord
            audioRecord = null
            thread = worker
            worker = null
            pcm = pcmFile
            pcmFile = null
            wav = wavFile
            wavFile = null
        }

        try {
            record?.stop()
        } catch (_: Throwable) {
        }

        try {
            thread?.join(1_500)
        } catch (_: InterruptedException) {
            Thread.currentThread().interrupt()
        }

        try {
            record?.release()
        } catch (_: Throwable) {
        }

        var result: File? = null
        if (pcm != null && wav != null) {
            val pcmBytes = pcm.length()
            val longEnough = pcmBytes >= MIN_PCM_BYTES
            if (longEnough && WavWriter.pcmToWav(pcm, wav, SAMPLE_RATE, CHANNELS, BITS_PER_SAMPLE)) {
                result = wav
                Log.i(TAG, "finishRecording: pcmBytes=$pcmBytes validWav=true wav=${wav.name}")
            } else {
                wav.delete()
                val reason = if (!longEnough) "too short" else "wav write failed"
                Log.i(TAG, "finishRecording: pcmBytes=$pcmBytes validWav=false reason=$reason")
            }
        } else {
            Log.w(TAG, "finishRecording: missing temp files pcm=${pcm != null} wav=${wav != null}")
        }
        pcm?.delete()

        if (notifyCallback) {
            callback?.invoke(result)
        }
        return result
    }

    private fun recordLoop(record: AudioRecord, pcm: File) {
        val buffer = ByteArray(3_200)
        var output: FileOutputStream? = null
        try {
            output = FileOutputStream(pcm)
            while (!stopRequested) {
                val read = record.read(buffer, 0, buffer.size)
                if (read > 0) {
                    output.write(buffer, 0, read)
                } else if (read < 0) {
                    if (!stopRequested) {
                        Log.w(TAG, "recordLoop read error code=$read")
                    }
                    break
                }
            }
        } catch (t: Throwable) {
            Log.w(TAG, "recordLoop failed to read/write PCM", t)
        } finally {
            try {
                output?.flush()
                output?.close()
            } catch (_: Throwable) {
            }
        }
    }

    @SuppressLint("MissingPermission")
    private fun createAudioRecord(): AudioRecordChoice? {
        val minBuffer = AudioRecord.getMinBufferSize(
            SAMPLE_RATE,
            CHANNEL_CONFIG,
            AUDIO_FORMAT
        )
        if (minBuffer <= 0) {
            Log.w(TAG, "createAudioRecord failed: minBuffer=$minBuffer")
            return null
        }

        val bufferSize = max(minBuffer, SAMPLE_RATE * BITS_PER_SAMPLE / 8 * CHANNELS)
        val sources = intArrayOf(
            MediaRecorder.AudioSource.VOICE_RECOGNITION,
            MediaRecorder.AudioSource.MIC
        )

        for (source in sources) {
            val sourceName = audioSourceName(source)
            try {
                val record = AudioRecord(
                    source,
                    SAMPLE_RATE,
                    CHANNEL_CONFIG,
                    AUDIO_FORMAT,
                    bufferSize
                )
                if (record.state == AudioRecord.STATE_INITIALIZED) {
                    Log.d(TAG, "createAudioRecord: selected source=$sourceName")
                    return AudioRecordChoice(source, record)
                }
                Log.w(TAG, "createAudioRecord: source=$sourceName state=${record.state}")
                record.release()
            } catch (t: Throwable) {
                Log.w(TAG, "createAudioRecord: source=$sourceName failed", t)
            }
        }
        Log.w(TAG, "createAudioRecord: no source initialized")
        return null
    }

    private fun audioSourceName(source: Int): String = when (source) {
        MediaRecorder.AudioSource.MIC -> "MIC"
        MediaRecorder.AudioSource.VOICE_RECOGNITION -> "VOICE_RECOGNITION"
        else -> "UNKNOWN($source)"
    }

    private data class AudioRecordChoice(
        val source: Int,
        val record: AudioRecord
    )

    companion object {
        const val TAG = "BoardAI-AudioRecorder"
        const val SAMPLE_RATE = 16_000
        const val CHANNELS = 1
        const val BITS_PER_SAMPLE = 16
        private const val CHANNEL_CONFIG = AudioFormat.CHANNEL_IN_MONO
        private const val AUDIO_FORMAT = AudioFormat.ENCODING_PCM_16BIT
        private const val MAX_DURATION_MS = 20_000L
        private const val MIN_PCM_BYTES = 16_000 * 2 * 300 / 1_000 // 300 ms
    }
}
