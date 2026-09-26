package com.boardai.tutorial.uaal.voice

import java.io.File
import java.io.FileInputStream
import java.io.FileOutputStream
import java.io.OutputStream

/**
 * Writes a standard 44-byte PCM WAV header in front of raw 16-bit little-endian
 * samples.  The recorder writes a temporary PCM file first so the header sizes
 * are always final and the resulting WAV is readable immediately after stop().
 */
object WavWriter {
    fun pcmToWav(
        pcmFile: File,
        wavFile: File,
        sampleRate: Int = 16_000,
        channels: Int = 1,
        bitsPerSample: Int = 16
    ): Boolean {
        return try {
            val dataLength = pcmFile.length().toInt()
            FileOutputStream(wavFile).use { output ->
                writeHeader(output, dataLength, sampleRate, channels, bitsPerSample)
                FileInputStream(pcmFile).use { input ->
                    input.copyTo(output)
                }
            }
            true
        } catch (_: Throwable) {
            wavFile.delete()
            false
        }
    }

    private fun writeHeader(
        output: OutputStream,
        dataLength: Int,
        sampleRate: Int,
        channels: Int,
        bitsPerSample: Int
    ) {
        val blockAlign = channels * bitsPerSample / 8
        val byteRate = sampleRate * blockAlign

        output.write("RIFF".toByteArray(Charsets.US_ASCII))
        writeIntLe(output, 36 + dataLength)
        output.write("WAVE".toByteArray(Charsets.US_ASCII))

        output.write("fmt ".toByteArray(Charsets.US_ASCII))
        writeIntLe(output, 16)
        writeShortLe(output, 1) // PCM
        writeShortLe(output, channels)
        writeIntLe(output, sampleRate)
        writeIntLe(output, byteRate)
        writeShortLe(output, blockAlign)
        writeShortLe(output, bitsPerSample)

        output.write("data".toByteArray(Charsets.US_ASCII))
        writeIntLe(output, dataLength)
    }

    private fun writeIntLe(output: OutputStream, value: Int) {
        output.write(value and 0xFF)
        output.write((value shr 8) and 0xFF)
        output.write((value shr 16) and 0xFF)
        output.write((value shr 24) and 0xFF)
    }

    private fun writeShortLe(output: OutputStream, value: Int) {
        output.write(value and 0xFF)
        output.write((value shr 8) and 0xFF)
    }
}
