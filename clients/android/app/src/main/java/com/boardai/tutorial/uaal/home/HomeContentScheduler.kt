package com.boardai.tutorial.uaal.home

interface HomeContentScheduler {
    fun postToMain(block: () -> Unit)
    fun runCatalogTask(block: () -> Unit)
    fun runContentTask(block: () -> Unit)
    fun dispose()
}
