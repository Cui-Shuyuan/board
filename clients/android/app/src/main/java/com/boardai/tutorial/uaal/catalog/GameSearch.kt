package com.boardai.tutorial.uaal.catalog

import java.util.Locale

/**
 * Local fuzzy search over the currently visible catalog.
 *
 * Matching order:
 *   1. exact match against name_zh, name_en, aliases or search_keys;
 *   2. prefix match against any of those values;
 *   3. substring match against any of those values;
 *   4. otherwise the game is excluded.
 *
 * Everything is lower-cased with Locale.ROOT, so Chinese input such as
 * "璀璨" and ASCII pinyin/initials such as "ccbs" both work naturally.
 */
object GameSearch {
    fun search(games: List<GameCatalogEntry>, rawQuery: String): List<GameCatalogEntry> {
        val query = rawQuery.trim().lowercase(Locale.ROOT)
        if (query.isEmpty()) return games

        return games
            .mapNotNull { game ->
                val rank = matchRank(game, query)
                if (rank == null) null else RankedGame(game, rank)
            }
            .sortedWith(
                compareBy<RankedGame> { it.rank }
                    .thenBy { it.game.nameZh.lowercase(Locale.ROOT) }
                    .thenBy { it.game.nameEn.lowercase(Locale.ROOT) }
                    .thenBy { it.game.id }
            )
            .map { it.game }
    }

    private fun matchRank(game: GameCatalogEntry, query: String): Int? {
        val candidates = buildList {
            add(game.nameZh)
            add(game.nameEn)
            addAll(game.aliases)
            addAll(game.searchKeys)
        }.map { it.trim().lowercase(Locale.ROOT) }
            .filter { it.isNotEmpty() }

        if (candidates.any { it == query }) return RANK_EXACT
        if (candidates.any { it.startsWith(query) }) return RANK_PREFIX
        if (candidates.any { it.contains(query) }) return RANK_CONTAINS
        return null
    }

    private data class RankedGame(val game: GameCatalogEntry, val rank: Int)

    private const val RANK_EXACT = 0
    private const val RANK_PREFIX = 1
    private const val RANK_CONTAINS = 2
}
