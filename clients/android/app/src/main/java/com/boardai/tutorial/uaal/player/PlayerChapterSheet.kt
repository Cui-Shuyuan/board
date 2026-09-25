package com.boardai.tutorial.uaal.player

import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.Text
import androidx.compose.material3.rememberModalBottomSheetState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.mutableStateMapOf
import androidx.compose.runtime.remember
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.boardai.tutorial.uaal.timeline.ChapterNode
import com.boardai.tutorial.uaal.timeline.TutorialTimeline

private data class ChapterRow(
    val node: ChapterNode,
    val depth: Int
)

@OptIn(ExperimentalMaterial3Api::class)
@Composable
internal fun PlayerChapterSheet(
    timeline: TutorialTimeline?,
    currentCueIndex: Int,
    onDismiss: () -> Unit,
    onJumpToCue: (String) -> Unit
) {
    val sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true)
    ModalBottomSheet(
        onDismissRequest = onDismiss,
        sheetState = sheetState
    ) {
        ChapterTree(
            timeline = timeline,
            currentCueIndex = currentCueIndex,
            onJumpToCue = onJumpToCue
        )
    }
}

@Composable
private fun ChapterTree(
    timeline: TutorialTimeline?,
    currentCueIndex: Int,
    onJumpToCue: (String) -> Unit
) {
    if (timeline == null) {
        Text(
            text = "时间轴未加载",
            color = Color.White,
            modifier = Modifier.padding(24.dp)
        )
        return
    }

    val expanded = remember(timeline) { mutableStateMapOf<String, Boolean>() }
    val currentPath = timeline.cueAt(currentCueIndex)?.groupPath ?: emptyList()
    val currentPathKey = chapterPathKey(currentPath)

    LaunchedEffect(timeline, currentCueIndex) {
        val prefix = mutableListOf<String>()
        for (title in currentPath) {
            prefix += title
            expanded[chapterPathKey(prefix)] = true
        }
    }

    val rows = mutableListOf<ChapterRow>()
    fun visit(nodes: List<ChapterNode>, depth: Int) {
        for (node in nodes) {
            rows += ChapterRow(node = node, depth = depth)
            if (expanded[node.key] == true) {
                visit(node.children, depth + 1)
            }
        }
    }
    visit(timeline.rootChapters, 0)

    Column(
        modifier = Modifier
            .fillMaxWidth()
            .navigationBarsPadding()
            .padding(bottom = 8.dp)
    ) {
        Text(
            text = "章节",
            color = Color.White,
            fontSize = 20.sp,
            fontWeight = FontWeight.Bold,
            modifier = Modifier.padding(horizontal = 16.dp, vertical = 8.dp)
        )
        if (rows.isEmpty()) {
            Text(
                text = "当前内容没有章节信息",
                color = Color.White.copy(alpha = 0.7f),
                modifier = Modifier.padding(16.dp)
            )
        } else {
            LazyColumn(
                modifier = Modifier
                    .fillMaxWidth()
                    .heightIn(max = 480.dp)
            ) {
                items(rows, key = { it.node.key }) { row ->
                    ChapterRowItem(
                        row = row,
                        isCurrent = row.node.key == currentPathKey,
                        expanded = expanded[row.node.key] == true,
                        onToggle = {
                            expanded[row.node.key] = expanded[row.node.key] != true
                        },
                        onJump = { onJumpToCue(row.node.firstCueId) }
                    )
                }
            }
        }
    }
}

@Composable
private fun ChapterRowItem(
    row: ChapterRow,
    isCurrent: Boolean,
    expanded: Boolean,
    onToggle: () -> Unit,
    onJump: () -> Unit
) {
    val interactionSource = remember { MutableInteractionSource() }
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .background(
                if (isCurrent) MaterialTheme.colorScheme.primary.copy(alpha = 0.20f)
                else Color.Transparent
            )
            .clickable(
                interactionSource = interactionSource,
                indication = null,
                onClick = {
                    if (row.node.children.isNotEmpty()) onToggle() else onJump()
                }
            )
            .padding(
                start = (12 + row.depth * 16).dp,
                end = 12.dp,
                top = 7.dp,
                bottom = 7.dp
            ),
        verticalAlignment = Alignment.CenterVertically
    ) {
        if (row.node.children.isNotEmpty()) {
            Text(
                text = if (expanded) "▾" else "▸",
                color = Color.White.copy(alpha = 0.78f),
                fontSize = 14.sp,
                modifier = Modifier.width(20.dp)
            )
        } else {
            Spacer(Modifier.width(20.dp))
        }

        Text(
            text = row.node.title,
            color = Color.White,
            fontSize = 14.sp,
            fontWeight = if (isCurrent) FontWeight.SemiBold else FontWeight.Normal,
            maxLines = 1,
            overflow = TextOverflow.Ellipsis,
            modifier = Modifier.weight(1f)
        )

        if (isCurrent) {
            Spacer(Modifier.width(6.dp))
            Text(
                text = "当前",
                color = MaterialTheme.colorScheme.primary,
                fontSize = 11.sp,
                fontWeight = FontWeight.SemiBold
            )
        }

        if (row.node.children.isNotEmpty() && row.node.firstCueId.isNotBlank()) {
            Spacer(Modifier.width(8.dp))
            PlayerControlButton(
                label = "跳到本节",
                compact = true,
                modifier = Modifier.width(72.dp),
                onClick = onJump
            )
        }
    }
}
