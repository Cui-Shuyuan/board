package com.boardai.tutorial.uaal.home

import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.boardai.tutorial.uaal.BuildConfig
import com.boardai.tutorial.uaal.catalog.GameCatalogEntry

@Composable
fun GameCard(
    game: GameCatalogEntry,
    onClick: () -> Unit,
    modifier: Modifier = Modifier,
    compact: Boolean = false,
    extraInfo: String? = null,
    busy: Boolean = false,
    statusText: String? = null
) {
    val playerText = when {
        game.minPlayers > 0 && game.maxPlayers > 0 -> "${game.minPlayers}-${game.maxPlayers} 人"
        game.maxPlayers > 0 -> "最多 ${game.maxPlayers} 人"
        game.minPlayers > 0 -> "${game.minPlayers} 人起"
        else -> ""
    }

    Card(
        modifier = modifier
            .fillMaxWidth()
            .clickable(enabled = !busy, onClick = onClick),
        shape = RoundedCornerShape(if (compact) 12.dp else 14.dp),
        colors = CardDefaults.cardColors(
            containerColor = if (busy) Color(0xFF252B35) else Color(0xFF1B202A)
        )
    ) {
        Column(
            modifier = Modifier.padding(if (compact) 12.dp else 14.dp),
            verticalArrangement = Arrangement.spacedBy(if (compact) 4.dp else 6.dp)
        ) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Text(
                    text = game.nameZh,
                    color = Color.White,
                    fontSize = if (compact) 15.sp else 17.sp,
                    fontWeight = FontWeight.Bold,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                    modifier = Modifier.weight(1f)
                )
                if (busy) {
                    Text(
                        text = "加载中…",
                        color = Color(0xFF8AB4F8),
                        fontSize = 11.sp,
                        fontWeight = FontWeight.SemiBold
                    )
                }
            }

            if (game.nameEn.isNotBlank()) {
                Text(
                    text = game.nameEn,
                    color = Color(0xFFAAB2BF),
                    fontSize = if (compact) 12.sp else 13.sp,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis
                )
            }

            Row(
                horizontalArrangement = Arrangement.spacedBy(8.dp),
                verticalAlignment = Alignment.CenterVertically
            ) {
                if (playerText.isNotBlank()) {
                    Text(
                        text = playerText,
                        color = Color(0xFF98A2B3),
                        fontSize = 12.sp
                    )
                }
                if (extraInfo != null) {
                    Text(
                        text = extraInfo,
                        color = Color(0xFF8AB4F8),
                        fontSize = 12.sp,
                        fontWeight = FontWeight.Medium
                    )
                }
                Spacer(Modifier.weight(1f))
                if (!statusText.isNullOrBlank()) {
                    Text(
                        text = statusText,
                        color = statusTextColor(statusText),
                        fontSize = if (compact) 10.sp else 11.sp,
                        fontWeight = FontWeight.SemiBold,
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis
                    )
                }
            }

            if (BuildConfig.DEBUG && !game.released) {
                Text(
                    text = "DEBUG/未发布",
                    color = Color(0xFFFFB86B),
                    fontSize = 11.sp,
                    fontWeight = FontWeight.Bold,
                    modifier = Modifier
                        .clip(RoundedCornerShape(4.dp))
                        .background(Color(0xFF3B2C18))
                        .padding(horizontal = 6.dp, vertical = 2.dp)
                )
            }
        }
    }
}

private fun statusTextColor(statusText: String): Color = when {
    statusText.startsWith("已暂停") || statusText.startsWith("更新暂停") -> Color(0xFFFFB86B)
    statusText == "已是最新" || statusText == "已安装" -> Color(0xFF9AD29A)
    statusText.startsWith("可更新") -> Color(0xFFFFB86B)
    statusText == "暂无资源" -> Color(0xFF98A2B3)
    statusText == "仅规则问答" -> Color(0xFFB7C4D8)
    else -> Color(0xFF8AB4F8)
}
