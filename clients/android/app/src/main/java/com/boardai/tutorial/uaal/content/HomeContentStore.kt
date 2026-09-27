package com.boardai.tutorial.uaal.content

interface HomeContentStore : ContentStatusSource {
    fun deletePaused(gameId: String)
    fun deleteLocalContent(gameId: String)
}
