package com.boardai.tutorial.uaal.home

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyRow
import androidx.compose.foundation.lazy.items
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import com.boardai.tutorial.uaal.catalog.GameCatalogEntry
import com.boardai.tutorial.uaal.content.ContentStatus
import com.boardai.tutorial.uaal.content.shortCardText
import com.boardai.tutorial.uaal.history.RecentGame

@Composable
fun RecentGamesRow(
    recentGames: List<RecentGame>,
    onGameClick: (GameCatalogEntry) -> Unit,
    modifier: Modifier = Modifier,
    preparingGameId: String? = null,
    contentStatuses: Map<String, ContentStatus> = emptyMap()
) {
    LazyRow(
        modifier = modifier,
        horizontalArrangement = Arrangement.spacedBy(10.dp),
        contentPadding = PaddingValues(horizontal = 16.dp, vertical = 4.dp)
    ) {
        items(recentGames, key = { it.game.id }) { recent ->
            GameCard(
                game = recent.game,
                onClick = { onGameClick(recent.game) },
                compact = true,
                extraInfo = "玩过 ${recent.playCount} 次",
                busy = preparingGameId == recent.game.id,
                statusText = contentStatuses[recent.game.id]?.shortCardText(),
                modifier = Modifier.width(190.dp)
            )
        }
    }
}
