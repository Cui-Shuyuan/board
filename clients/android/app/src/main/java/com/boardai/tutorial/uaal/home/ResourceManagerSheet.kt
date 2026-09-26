package com.boardai.tutorial.uaal.home

import android.text.format.Formatter
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.boardai.tutorial.uaal.catalog.GameCatalogEntry
import com.boardai.tutorial.uaal.content.ContentStatus
import com.boardai.tutorial.uaal.content.hasLocalContent
import com.boardai.tutorial.uaal.content.localVersionText
import com.boardai.tutorial.uaal.content.resourceStatusText
import com.boardai.tutorial.uaal.content.serverVersionText

@Composable
fun ResourceManagerOverlay(
    games: List<GameCatalogEntry>,
    statuses: Map<String, ContentStatus>,
    checking: Boolean,
    onDismiss: () -> Unit,
    onCheckUpdates: () -> Unit,
    onDownload: (GameCatalogEntry) -> Unit,
    onContinueDownload: (GameCatalogEntry) -> Unit,
    onUpdate: (GameCatalogEntry) -> Unit,
    onContinueUpdate: (GameCatalogEntry) -> Unit,
    onDelete: (GameCatalogEntry) -> Unit,
    modifier: Modifier = Modifier
) {
    val context = LocalContext.current

    Box(
        modifier = modifier
            .fillMaxSize()
            .background(Color.Black.copy(alpha = 0.72f))
            .clickable(onClick = onDismiss)
    ) {
        Card(
            modifier = Modifier
                .fillMaxWidth(0.92f)
                .fillMaxSize(0.84f)
                .align(Alignment.Center)
                .clickable(enabled = false) {},
            shape = RoundedCornerShape(16.dp),
            colors = CardDefaults.cardColors(containerColor = Color(0xFF141820))
        ) {
            Column(modifier = Modifier.fillMaxSize()) {
                Row(
                    modifier = Modifier
                        .fillMaxWidth()
                        .padding(horizontal = 16.dp, vertical = 14.dp),
                    verticalAlignment = Alignment.CenterVertically
                ) {
                    Text(
                        text = "资源管理",
                        color = Color.White,
                        fontSize = 18.sp,
                        fontWeight = FontWeight.Bold
                    )
                    Spacer(Modifier.weight(1f))
                    TextButton(onClick = onDismiss) {
                        Text("关闭", color = Color(0xFF8AB4F8))
                    }
                }

                if (games.isEmpty()) {
                    Box(
                        modifier = Modifier
                            .weight(1f)
                            .fillMaxWidth(),
                        contentAlignment = Alignment.Center
                    ) {
                        Text("暂无可管理的游戏", color = Color(0xFF98A2B3), fontSize = 13.sp)
                    }
                } else {
                    LazyColumn(
                        modifier = Modifier.weight(1f),
                        contentPadding = PaddingValues(horizontal = 14.dp, vertical = 4.dp),
                        verticalArrangement = Arrangement.spacedBy(10.dp)
                    ) {
                        items(games, key = { it.id }) { game ->
                            ResourceManagerRow(
                                game = game,
                                status = statuses[game.id] ?: ContentStatus.NotDownloaded,
                                sizeText = game.contentSizeBytes?.let {
                                    Formatter.formatShortFileSize(context, it)
                                },
                                onDownload = { onDownload(game) },
                                onContinueDownload = { onContinueDownload(game) },
                                onUpdate = { onUpdate(game) },
                                onContinueUpdate = { onContinueUpdate(game) },
                                onDelete = { onDelete(game) }
                            )
                        }
                    }
                }

                Row(
                    modifier = Modifier
                        .fillMaxWidth()
                        .padding(16.dp),
                    verticalAlignment = Alignment.CenterVertically
                ) {
                    if (checking) {
                        CircularProgressIndicator(
                            color = Color(0xFF8AB4F8),
                            strokeWidth = 2.dp,
                            modifier = Modifier.width(18.dp).height(18.dp)
                        )
                        Spacer(Modifier.width(8.dp))
                    }
                    Text(
                        text = if (checking) "检查更新中…" else "只刷新目录和状态，不自动下载",
                        color = Color(0xFF98A2B3),
                        fontSize = 12.sp,
                        modifier = Modifier.weight(1f)
                    )
                    Button(
                        onClick = onCheckUpdates,
                        enabled = !checking
                    ) {
                        Text("检查更新")
                    }
                }
            }
        }
    }
}

@Composable
private fun ResourceManagerRow(
    game: GameCatalogEntry,
    status: ContentStatus,
    sizeText: String?,
    onDownload: () -> Unit,
    onContinueDownload: () -> Unit,
    onUpdate: () -> Unit,
    onContinueUpdate: () -> Unit,
    onDelete: () -> Unit
) {
    Card(
        modifier = Modifier.fillMaxWidth(),
        shape = RoundedCornerShape(12.dp),
        colors = CardDefaults.cardColors(containerColor = Color(0xFF1B202A))
    ) {
        Column(
            modifier = Modifier.padding(12.dp),
            verticalArrangement = Arrangement.spacedBy(6.dp)
        ) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Text(
                    text = game.nameZh,
                    color = Color.White,
                    fontSize = 15.sp,
                    fontWeight = FontWeight.Bold,
                    modifier = Modifier.weight(1f)
                )
                Text(
                    text = status.resourceStatusText(),
                    color = statusColor(status),
                    fontSize = 12.sp,
                    fontWeight = FontWeight.SemiBold
                )
            }

            Text(
                text = "本地：${status.localVersionText()}    服务端：${status.serverVersionText(game)}",
                color = Color(0xFFAAB2BF),
                fontSize = 12.sp
            )

            if (sizeText != null) {
                Text(
                    text = "下载大小：$sizeText",
                    color = Color(0xFF98A2B3),
                    fontSize = 12.sp
                )
            }

            Row(
                horizontalArrangement = Arrangement.spacedBy(8.dp),
                verticalAlignment = Alignment.CenterVertically
            ) {
                when (status) {
                    ContentStatus.NotDownloaded -> {
                        ActionButton("下载", onDownload)
                    }

                    is ContentStatus.Paused -> {
                        ActionButton("继续下载 ${status.progress.percent}%", onContinueDownload)
                    }

                    is ContentStatus.UpdateAvailable -> {
                        ActionButton("更新", onUpdate)
                    }

                    is ContentStatus.UpdatePaused -> {
                        ActionButton("继续更新 ${status.progress.percent}%", onContinueUpdate)
                    }

                    ContentStatus.NoServerResource,
                    is ContentStatus.InstalledOffline,
                    is ContentStatus.InstalledCurrent -> Unit
                }

                if (status.hasLocalContent) {
                    TextButton(onClick = onDelete) {
                        Text("删除本地资源", color = Color(0xFFFFB4AB))
                    }
                }
            }
        }
    }
}

@Composable
private fun ActionButton(label: String, onClick: () -> Unit) {
    Button(
        onClick = onClick,
        contentPadding = PaddingValues(horizontal = 14.dp, vertical = 4.dp)
    ) {
        Text(label, fontSize = 12.sp)
    }
}

private fun statusColor(status: ContentStatus): Color = when (status) {
    is ContentStatus.Paused,
    is ContentStatus.UpdatePaused -> Color(0xFFFFB86B)
    is ContentStatus.UpdateAvailable -> Color(0xFFFFB86B)
    is ContentStatus.InstalledCurrent,
    is ContentStatus.InstalledOffline -> Color(0xFF9AD29A)
    ContentStatus.NoServerResource -> Color(0xFF98A2B3)
    ContentStatus.NotDownloaded -> Color(0xFF8AB4F8)
}
