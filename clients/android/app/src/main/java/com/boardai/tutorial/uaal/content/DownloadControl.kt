package com.boardai.tutorial.uaal.content

/**
 * Cooperative pause flag shared between the UI thread and the single content
 * download worker.  Each update run gets a fresh instance.
 */
class DownloadControl {
    @Volatile
    var pauseRequested: Boolean = false
        private set

    fun requestPause() {
        pauseRequested = true
    }
}
