package com.boardai.tutorial.uaal.content

import java.io.File

/**
 * Result of one [ContentFetcher.downloadFile] attempt.
 */
enum class ContentFileDownloadOutcome {
    COMPLETED,
    PAUSED
}

/**
 * Transport boundary for board content.
 *
 * Implementations fetch the game manifest and transfer individual files.  The
 * updater owns all local-file verification, resume-state scanning, atomic
 * installation and activation.
 */
interface ContentFetcher {
    fun fetchManifest(game: String): ContentManifest

    fun downloadFile(
        file: ContentFile,
        target: File,
        control: DownloadControl,
        onBytesWritten: (Long) -> Unit
    ): ContentFileDownloadOutcome
}
