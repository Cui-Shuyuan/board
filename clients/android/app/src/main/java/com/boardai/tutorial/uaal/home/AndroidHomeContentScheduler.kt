package com.boardai.tutorial.uaal.home

import android.os.Handler
import android.os.Looper
import java.util.concurrent.ExecutorService
import java.util.concurrent.Executors

class AndroidHomeContentScheduler : HomeContentScheduler {
    private val mainHandler = Handler(Looper.getMainLooper())
    private val catalogExecutor: ExecutorService = Executors.newSingleThreadExecutor()
    private val contentExecutor: ExecutorService = Executors.newSingleThreadExecutor()

    override fun postToMain(block: () -> Unit) {
        mainHandler.post(block)
    }

    override fun runCatalogTask(block: () -> Unit) {
        catalogExecutor.execute(block)
    }

    override fun runContentTask(block: () -> Unit) {
        contentExecutor.execute(block)
    }

    override fun dispose() {
        catalogExecutor.shutdownNow()
        contentExecutor.shutdownNow()
    }
}
