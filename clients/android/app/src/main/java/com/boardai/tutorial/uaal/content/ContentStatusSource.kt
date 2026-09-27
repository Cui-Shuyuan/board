package com.boardai.tutorial.uaal.content

import java.io.File

interface ContentStatusSource {
    fun readActiveValid(gameId: String): ActiveContent?
    fun readPaused(gameId: String): PausedContent?
    fun isVersionComplete(version: String, gameId: String? = null): Boolean
    fun gameRoot(version: String, gameId: String): File
}
