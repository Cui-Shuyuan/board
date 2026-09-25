package com.boardai.tutorial.uaal.content

import java.io.File
import java.util.Locale

/**
 * Immutable lookup table built from all complete local versions except the
 * target version.
 *
 * Layout:
 *   path -> normalized sha256 -> source file
 *
 * The SHA-256 key is normalized to lower case so manifest validation and file
 * reuse are consistent on every platform.
 */
class ReusableContentIndex(
    private val sourcesByPath: Map<String, Map<String, File>>
) {
    fun find(file: ContentFile): File? {
        val sourcesByHash = sourcesByPath[file.path] ?: return null
        val source = sourcesByHash[file.sha256.lowercase(Locale.ROOT)] ?: return null

        // The directory may have been cleaned while the index was alive, and a
        // file may have been tampered with.  Never return a source that does
        // not still have the size expected by the caller.
        return source.takeIf { it.isFile && it.length() == file.size }
    }
}
