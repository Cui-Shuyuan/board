package com.boardai.tutorial.uaal.home

import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ExperimentalLayoutApi
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.systemBarsIgnoringVisibility
import androidx.compose.foundation.layout.windowInsetsPadding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.boardai.tutorial.uaal.catalog.GameCatalogEntry
import com.boardai.tutorial.uaal.catalog.GameSearch
import com.boardai.tutorial.uaal.history.RecentGame

private val HomeBackground = Color(0xFF0F1116)

@OptIn(ExperimentalLayoutApi::class)
@Composable
fun HomeScreen(
    games: List<GameCatalogEntry>,
    recentGames: List<RecentGame>,
    query: String,
    onQueryChange: (String) -> Unit,
    onGameClick: (GameCatalogEntry) -> Unit,
    onRetry: () -> Unit,
    isLoading: Boolean,
    errorMessage: String?,
    preparingGameId: String?,
    playbackError: String?,
    modifier: Modifier = Modifier
) {
    val searchResults = if (query.isBlank()) {
        emptyList()
    } else {
        GameSearch.search(games, query)
    }

    Column(
        modifier = modifier
            .fillMaxSize()
            .background(HomeBackground)
            .windowInsetsPadding(WindowInsets.systemBarsIgnoringVisibility)
    ) {
        Column(modifier = Modifier.padding(horizontal = 16.dp, vertical = 12.dp)) {
            Text(
                text = "BoardAI 教程",
                color = Color.White,
                fontSize = 24.sp,
                fontWeight = FontWeight.Bold
            )
            Text(
                text = "选一款桌游，开始交互式教程",
                color = Color(0xFF98A2B3),
                fontSize = 13.sp,
                modifier = Modifier.padding(top = 2.dp, bottom = 12.dp)
            )
            GameSearchBar(query = query, onQueryChange = onQueryChange)
        }

        Box(
            modifier = Modifier
                .weight(1f)
                .fillMaxWidth()
        ) {
            when {
                isLoading && games.isEmpty() -> {
                Box(
                    modifier = Modifier.fillMaxSize(),
                    contentAlignment = Alignment.Center
                ) {
                    Column(horizontalAlignment = Alignment.CenterHorizontally) {
                        CircularProgressIndicator(color = Color(0xFF8AB4F8))
                        Text(
                            text = "正在加载游戏目录…",
                            color = Color(0xFF98A2B3),
                            fontSize = 13.sp,
                            modifier = Modifier.padding(top = 12.dp)
                        )
                    }
                }
            }

            errorMessage != null && games.isEmpty() -> {
                Box(
                    modifier = Modifier
                        .fillMaxSize()
                        .padding(24.dp),
                    contentAlignment = Alignment.Center
                ) {
                    Column(horizontalAlignment = Alignment.CenterHorizontally) {
                        Text(
                            text = errorMessage,
                            color = Color(0xFFFFB4AB),
                            fontSize = 14.sp,
                            textAlign = TextAlign.Center
                        )
                        Text(
                            text = "重试",
                            color = Color(0xFF8AB4F8),
                            fontSize = 15.sp,
                            fontWeight = FontWeight.Bold,
                            modifier = Modifier
                                .padding(top = 16.dp)
                                .clickable(onClick = onRetry)
                        )
                    }
                }
            }

            query.isNotBlank() -> {
                if (searchResults.isEmpty()) {
                    EmptyState("没有找到匹配的游戏", modifier = Modifier.fillMaxSize())
                } else {
                    LazyColumn(
                        modifier = Modifier.fillMaxSize(),
                        contentPadding = PaddingValues(horizontal = 16.dp, vertical = 4.dp),
                        verticalArrangement = Arrangement.spacedBy(10.dp)
                    ) {
                        item {
                            Text(
                                text = "搜索结果 · ${searchResults.size}",
                                color = Color(0xFF98A2B3),
                                fontSize = 12.sp,
                                modifier = Modifier.padding(top = 4.dp, bottom = 2.dp)
                            )
                        }
                        items(searchResults, key = { it.id }) { game ->
                            GameCard(
                                game = game,
                                onClick = { onGameClick(game) },
                                busy = preparingGameId == game.id
                            )
                        }
                    }
                }
            }

            else -> {
                LazyColumn(
                    modifier = Modifier.fillMaxSize(),
                    contentPadding = PaddingValues(bottom = 20.dp)
                ) {
                    if (playbackError != null) {
                        item {
                            Text(
                                text = playbackError,
                                color = Color(0xFFFFB4AB),
                                fontSize = 13.sp,
                                modifier = Modifier.padding(horizontal = 16.dp, vertical = 6.dp)
                            )
                        }
                    }

                    if (recentGames.isNotEmpty()) {
                        item {
                            SectionTitle("常玩 / 最近")
                        }
                        item {
                            RecentGamesRow(
                                recentGames = recentGames,
                                onGameClick = onGameClick,
                                preparingGameId = preparingGameId
                            )
                        }
                    }

                    item {
                        SectionTitle("全部游戏")
                    }

                    if (games.isEmpty()) {
                        item {
                            Text(
                                text = "暂无可发布的游戏",
                                color = Color(0xFF98A2B3),
                                fontSize = 13.sp,
                                modifier = Modifier
                                    .fillMaxWidth()
                                    .padding(32.dp),
                                textAlign = TextAlign.Center
                            )
                        }
                    } else {
                        items(games, key = { it.id }) { game ->
                            GameCard(
                                game = game,
                                onClick = { onGameClick(game) },
                                busy = preparingGameId == game.id,
                                modifier = Modifier.padding(horizontal = 16.dp)
                            )
                        }
                    }
                }
            }
            }
        }
    }
}

@Composable
private fun SectionTitle(title: String) {
    Text(
        text = title,
        color = Color.White,
        fontSize = 16.sp,
        fontWeight = FontWeight.SemiBold,
        modifier = Modifier.padding(horizontal = 16.dp, vertical = 8.dp)
    )
}

@Composable
private fun EmptyState(text: String, modifier: Modifier = Modifier) {
    Box(modifier = modifier, contentAlignment = Alignment.Center) {
        Text(text = text, color = Color(0xFF98A2B3), fontSize = 13.sp)
    }
}
