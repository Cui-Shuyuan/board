package com.boardai.tutorial.uaal.content

import java.io.File
import java.security.MessageDigest

internal fun downloadPartFile(target: File): File =
    File(target.parentFile, "${target.name}.part")

internal fun contentMatches(file: ContentFile, path: File): Boolean {
    if (!path.isFile || path.length() != file.size) return false
    return sha256(path).equals(file.sha256, ignoreCase = true)
}

internal fun sha256(path: File): String {
    val digest = MessageDigest.getInstance("SHA-256")
    path.inputStream().use { input ->
        val buffer = ByteArray(64 * 1024)
        while (true) {
            val read = input.read(buffer)
            if (read < 0) break
            digest.update(buffer, 0, read)
        }
    }
    return digest.digest().toHex()
}

private fun ByteArray.toHex(): String =
    joinToString(separator = "") { byte -> "%02x".format(byte.toInt() and 0xff) }
