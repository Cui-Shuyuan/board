package com.boardai.tutorial.uaal.player

import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.height
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.boardai.tutorial.uaal.content.ContentUpdateStatus
import com.boardai.tutorial.uaal.content.ContentUpdateUiState
import com.boardai.tutorial.uaal.content.toDisplayText

@Composable
internal fun PlayerContentPanel(
    contentState: ContentUpdateUiState,
    onDismiss: () -> Unit,
    onCheck: () -> Unit
) {
    val busy = contentState.status == ContentUpdateStatus.Checking ||
        contentState.status is ContentUpdateStatus.Downloading

    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text("内容版本") },
        text = {
            Column {
                Text(
                    text = "当前版本：" +
                        (contentState.activeVersion?.let { "v$it" } ?: "未激活"),
                    fontSize = 14.sp
                )
                Spacer(Modifier.height(6.dp))
                Text(
                    text = contentState.status.toDisplayText(),
                    fontSize = 13.sp,
                    color = Color.White.copy(alpha = 0.78f)
                )
            }
        },
        confirmButton = {
            TextButton(onClick = onCheck, enabled = !busy) {
                Text(if (busy) "检查中…" else "检查更新")
            }
        },
        dismissButton = {
            TextButton(onClick = onDismiss) {
                Text("关闭")
            }
        }
    )
}
