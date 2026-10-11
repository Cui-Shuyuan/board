package com.boardai.tutorial.uaal.home

import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.boardai.tutorial.uaal.player.tutorialTrackInfo

@Composable
fun TrackPickerDialog(
    state: TrackPickerState,
    onSelect: (String) -> Unit,
    onDismiss: () -> Unit
) {
    AlertDialog(
        onDismissRequest = onDismiss,
        title = {
            Text(
                text = state.game.nameZh,
                color = Color.White
            )
        },
        text = {
            Column(verticalArrangement = Arrangement.spacedBy(10.dp)) {
                Text(
                    text = "选择讲解版本",
                    color = androidx.compose.ui.graphics.Color(0xFFB7C4D8),
                    fontSize = 13.sp
                )
                state.tracks.forEach { trackId ->
                    TrackOption(
                        trackId = trackId,
                        onClick = { onSelect(trackId) }
                    )
                }
            }
        },
        confirmButton = {
            TextButton(onClick = onDismiss) {
                Text("取消")
            }
        },
        containerColor = androidx.compose.ui.graphics.Color(0xFF1B202A),
        titleContentColor = Color.White,
        textContentColor = androidx.compose.ui.graphics.Color(0xFFE6EAF0)
    )
}

@Composable
private fun TrackOption(
    trackId: String,
    onClick: () -> Unit
) {
    val info = tutorialTrackInfo(trackId)
    Card(
        modifier = Modifier
            .fillMaxWidth()
            .clickable(onClick = onClick),
        shape = RoundedCornerShape(12.dp),
        colors = CardDefaults.cardColors(
            containerColor = if (info.recommended) {
                androidx.compose.ui.graphics.Color(0xFF20415F)
            } else {
                androidx.compose.ui.graphics.Color(0xFF252B35)
            }
        )
    ) {
        Row(
            modifier = Modifier
                .fillMaxWidth()
                .padding(horizontal = 14.dp, vertical = 12.dp),
            verticalAlignment = Alignment.CenterVertically
        ) {
            Column(modifier = Modifier.weight(1f)) {
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Text(
                        text = info.title,
                        color = Color.White,
                        fontSize = 16.sp,
                        fontWeight = FontWeight.Bold
                    )
                    if (info.recommended) {
                        Spacer(Modifier.width(8.dp))
                        Text(
                            text = "推荐",
                            color = androidx.compose.ui.graphics.Color(0xFF9AD29A),
                            fontSize = 11.sp,
                            fontWeight = FontWeight.SemiBold
                        )
                    }
                }
                Text(
                    text = info.subtitle,
                    color = androidx.compose.ui.graphics.Color(0xFFB7C4D8),
                    fontSize = 12.sp,
                    modifier = Modifier.padding(top = 2.dp)
                )
            }
        }
    }
}
