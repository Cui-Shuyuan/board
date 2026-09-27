package com.boardai.tutorial.uaal.catalog

interface HomeCatalogSource {
    fun readCache(): List<GameCatalogEntry>
    fun fetchCatalog(): List<GameCatalogEntry>
    fun visibleGames(allGames: List<GameCatalogEntry>): List<GameCatalogEntry>
}
