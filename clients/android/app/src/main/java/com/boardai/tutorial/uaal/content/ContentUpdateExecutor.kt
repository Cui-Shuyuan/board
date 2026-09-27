package com.boardai.tutorial.uaal.content

interface ContentUpdateExecutor {
    fun update(
        game: String,
        onStatus: (ContentUpdateStatus) -> Unit,
        control: DownloadControl = DownloadControl()
    ): ContentUpdateResult
}
