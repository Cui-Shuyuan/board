package com.boardai.tutorial.uaal.content

import android.text.format.Formatter
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.boardai.tutorial.uaal.catalog.GameCatalogEntry

data class GamePromptState(
    val game: GameCatalogEntry,
    val status: ContentStatus
)

data class DownloadOverlayState(
    val game: GameCatalogEntry,
    val status: ContentUpdateStatus
)

enum class GamePromptAction {
    DOWNLOAD,
    REDOWNLOAD,
    CONTINUE_DOWNLOAD,
    UPDATE,
    CONTINUE_UPDATE,
    USE_CURRENT,
    CANCEL
}

@Composable
fun GameContentPromptDialog(
    state: GamePromptState,
    onAction: (GamePromptAction) -> Unit
) {
    val context = LocalContext.current
    val game = state.game
    val sizeText = game.contentSizeBytes?.let {
        Formatter.formatShortFileSize(context, it)
    }

    val message = when (val status = state.status) {
        ContentStatus.NoServerResource -> "暂无教程资源"
        ContentStatus.NotDownloaded -> buildString {
            append("《${game.nameZh}》需要下载")
            if (sizeText != null) append("约 $sizeText")
        }
        is ContentStatus.Paused -> "上次下载到 ${status.progress.percent}%，是否继续？"
        is ContentStatus.UpdateAvailable -> buildString {
            append("发现新版本 ${"v" + status.serverVersion.take(8)}")
            if (sizeText != null) append("（约 $sizeText）")
            append("，是否更新？")
        }
        is ContentStatus.UpdatePaused -> "新版本已下载 ${status.progress.percent}%，是否继续更新？"
        is ContentStatus.InstalledCurrent,
        is ContentStatus.InstalledOffline -> "本地内容已安装"
    }

    val buttons: @Composable () -> Unit = {
        Row(
            horizontalArrangement = Arrangement.spacedBy(8.dp),
            verticalAlignment = Alignment.CenterVertically
        ) {
            when (state.status) {
                ContentStatus.NoServerResource -> {
                    Button(onClick = { onAction(GamePromptAction.CANCEL) }) {
                        Text("知道了")
                    }
                }

                ContentStatus.NotDownloaded -> {
                    Button(onClick = { onAction(GamePromptAction.DOWNLOAD) }) {
                        Text("下载")
                    }
                    TextButton(onClick = { onAction(GamePromptAction.CANCEL) }) {
                        Text("取消")
                    }
                }

                is ContentStatus.Paused -> {
                    Button(onClick = { onAction(GamePromptAction.CONTINUE_DOWNLOAD) }) {
                        Text("继续下载")
                    }
                    TextButton(onClick = { onAction(GamePromptAction.REDOWNLOAD) }) {
                        Text("重新下载")
                    }
                    TextButton(onClick = { onAction(GamePromptAction.CANCEL) }) {
                        Text("取消")
                    }
                }

                is ContentStatus.UpdateAvailable -> {
                    Button(onClick = { onAction(GamePromptAction.UPDATE) }) {
                        Text("更新")
                    }
                    TextButton(onClick = { onAction(GamePromptAction.USE_CURRENT) }) {
                        Text("使用当前版本")
                    }
                    TextButton(onClick = { onAction(GamePromptAction.CANCEL) }) {
                        Text("取消")
                    }
                }

                is ContentStatus.UpdatePaused -> {
                    Button(onClick = { onAction(GamePromptAction.CONTINUE_UPDATE) }) {
                        Text("继续更新")
                    }
                    TextButton(onClick = { onAction(GamePromptAction.USE_CURRENT) }) {
                        Text("使用当前版本")
                    }
                    TextButton(onClick = { onAction(GamePromptAction.CANCEL) }) {
                        Text("取消")
                    }
                }

                is ContentStatus.InstalledCurrent,
                is ContentStatus.InstalledOffline -> {
                    Button(onClick = { onAction(GamePromptAction.USE_CURRENT) }) {
                        Text("进入")
                    }
                }
            }
        }
    }

    AlertDialog(
        onDismissRequest = { onAction(GamePromptAction.CANCEL) },
        title = { Text(game.nameZh) },
        text = { Text(message, color = Color(0xFFE6EAF0)) },
        confirmButton = buttons,
        containerColor = Color(0xFF1B202A),
        titleContentColor = Color.White,
        textContentColor = Color(0xFFE6EAF0)
    )
}

@Composable
fun DownloadProgressOverlay(
    state: DownloadOverlayState,
    onPause: () -> Unit,
    onRetry: () -> Unit,
    onReturnHome: () -> Unit
) {
    val context = LocalContext.current
    val status = state.status
    val gameName = state.game.nameZh

    val bytesCompleted: Long
    val bytesTotal: Long
    val currentPath: String
    when (status) {
        is ContentUpdateStatus.Downloading -> {
            bytesCompleted = status.bytesCompleted
            bytesTotal = status.totalBytes
            currentPath = status.currentPath
        }
        is ContentUpdateStatus.Paused -> {
            bytesCompleted = status.bytesCompleted
            bytesTotal = status.totalBytes
            currentPath = status.currentPath
        }
        else -> {
            bytesCompleted = 0L
            bytesTotal = 0L
            currentPath = ""
        }
    }

    val fraction = if (bytesTotal > 0L) {
        (bytesCompleted.toDouble() / bytesTotal.toDouble()).coerceIn(0.0, 1.0).toFloat()
    } else {
        0f
    }

    Box(
        modifier = Modifier
            .fillMaxSize()
            .background(Color.Black.copy(alpha = 0.70f)),
        contentAlignment = Alignment.Center
    ) {
        Card(
            modifier = Modifier
                .fillMaxWidth(0.86f)
                .padding(20.dp),
            shape = RoundedCornerShape(16.dp),
            colors = CardDefaults.cardColors(containerColor = Color(0xFF141820))
        ) {
            Column(modifier = Modifier.padding(20.dp)) {
                Text(
                    text = gameName,
                    color = Color.White,
                    fontSize = 18.sp,
                    fontWeight = FontWeight.Bold
                )
                Spacer(Modifier.height(8.dp))
                Text(
                    text = status.phaseText(),
                    color = Color(0xFF8AB4F8),
                    fontSize = 14.sp,
                    fontWeight = FontWeight.SemiBold
                )
                Spacer(Modifier.height(10.dp))

                if (status is ContentUpdateStatus.Failed) {
                    Text(
                        text = status.message,
                        color = Color(0xFFFFB4AB),
                        fontSize = 13.sp
                    )
                    Spacer(Modifier.height(16.dp))
                    Row(horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                        Button(onClick = onRetry) {
                            Text("重试")
                        }
                        TextButton(onClick = onReturnHome) {
                            Text("返回首页")
                        }
                    }
                } else {
                    Box(
                        modifier = Modifier
                            .fillMaxWidth()
                            .height(8.dp)
                            .clip(RoundedCornerShape(4.dp))
                            .background(Color.White.copy(alpha = 0.12f))
                    ) {
                        Box(
                            modifier = Modifier
                                .fillMaxWidth(fraction)
                                .height(8.dp)
                                .clip(RoundedCornerShape(4.dp))
                                .background(Color(0xFF8AB4F8))
                        )
                    }

                    Spacer(Modifier.height(8.dp))
                    Text(
                        // Reserve a fixed one-line slot even before byte totals
                        // are known.  This keeps the card height stable while
                        // the download moves through Checking/Downloading.
                        text = if (bytesTotal > 0L) {
                            "${Formatter.formatShortFileSize(context, bytesCompleted)} / " +
                                Formatter.formatShortFileSize(context, bytesTotal) +
                                "  ${(fraction * 100f).toInt()}%"
                        } else {
                            " "
                        },
                        color = Color(0xFFE6EAF0),
                        fontSize = 13.sp,
                        minLines = 1,
                        maxLines = 1,
                        softWrap = false
                    )

                    Spacer(Modifier.height(4.dp))
                    Text(
                        // Keep this line present from the first frame so the
                        // card does not grow when the first file path arrives.
                        text = if (currentPath.isNotBlank()) {
                            "当前文件：$currentPath"
                        } else {
                            " "
                        },
                        color = Color(0xFF98A2B3),
                        fontSize = 11.sp,
                        minLines = 1,
                        maxLines = 1,
                        softWrap = false,
                        overflow = TextOverflow.Ellipsis
                    )

                    Spacer(Modifier.height(16.dp))
                    Button(onClick = onPause) {
                        Text(if (status is ContentUpdateStatus.Paused) "返回首页" else "暂停并返回")
                    }
                }
            }
        }
    }
}
