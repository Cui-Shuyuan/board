package com.boardai.tutorial.uaal.qa

import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.remember
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp

const val QA_ENTRY_BUTTON_WIDTH_DP = 104
const val QA_ENTRY_BUTTON_HEIGHT_DP = 56
const val QA_ENTRY_BUTTON_END_PADDING_DP = 12
const val QA_ENTRY_BUTTON_BOTTOM_PADDING_DP = 10

/**
 * Unified question entry.
 *
 * It is deliberately a single Compose component so the home screen and the
 * playback overlay can place it at exactly the same bottom-right position.
 * Whether it opens a game picker or the current game is decided by the owner.
 */
@Composable
fun QaEntryButton(
    onClick: () -> Unit,
    modifier: Modifier = Modifier
) {
    val interactionSource = remember { MutableInteractionSource() }
    Box(
        modifier = modifier
            .width(QA_ENTRY_BUTTON_WIDTH_DP.dp)
            .height(QA_ENTRY_BUTTON_HEIGHT_DP.dp)
            .clip(RoundedCornerShape(12.dp))
            .background(Color(0xFFFFC107))
            .clickable(
                interactionSource = interactionSource,
                indication = null,
                onClick = onClick
            ),
        contentAlignment = Alignment.Center
    ) {
        Text(
            text = "问答",
            color = Color(0xFF1B1B1B),
            fontSize = 18.sp,
            fontWeight = FontWeight.Bold,
            maxLines = 1,
            overflow = TextOverflow.Ellipsis
        )
    }
}
