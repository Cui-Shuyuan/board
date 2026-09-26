package com.boardai.tutorial.uaal.qa

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.KeyboardActions
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.boardai.tutorial.uaal.UnityStatus
import com.boardai.tutorial.uaal.catalog.GameCatalogEntry
import com.boardai.tutorial.uaal.timeline.TutorialTimeline
import kotlinx.coroutines.launch

private val QaAccentYellow = Color(0xFFFFC107)
private val QaPanelColor = Color(0xF21B1B20)
private const val QaPanelHeightFraction = 0.72f

/**
 * Top-anchored text QA panel with a translucent animation backdrop.
 *
 * The panel intentionally owns only transient UI state (input text / sending
 * state).  Durable-for-the-player-lifetime session data lives in
 * [QaSessionHolder], which MainActivity clears when the tutorial is unloaded.
 */
@Composable
fun QaPanel(
    game: GameCatalogEntry?,
    status: UnityStatus?,
    timeline: TutorialTimeline?,
    repository: QaRepository,
    onClose: () -> Unit,
    modifier: Modifier = Modifier
) {
    val currentContext = buildQaContext(game, status, timeline)
    val session = QaSessionHolder.sessionState.value
    val messages = session?.messages.orEmpty()
    val listState = rememberLazyListState()
    val scope = rememberCoroutineScope()

    var input by remember { mutableStateOf("") }
    var sendState by remember { mutableStateOf<QaSendState>(QaSendState.Idle) }
    val sending = sendState is QaSendState.Sending

    LaunchedEffect(game?.id) {
        if (game != null) {
            QaSessionHolder.ensureSession(currentContext)
        }
    }

    LaunchedEffect(messages.size, sending) {
        if (messages.isNotEmpty()) {
            listState.animateScrollToItem(messages.lastIndex)
        }
    }

    fun startNewSession() {
        QaSessionHolder.startNewSession(buildQaContext(game, status, timeline))
        input = ""
        sendState = QaSendState.Idle
    }

    fun sendQuestion() {
        val question = input.trim()
        if (question.isBlank() || sending) return
        if (QaSessionHolder.session == null) {
            QaSessionHolder.ensureSession(buildQaContext(game, status, timeline))
        }
        val activeSession = QaSessionHolder.session ?: return
        val context = buildQaContext(game, status, timeline)
        val userMessage = QaMessage(
            role = QaMessage.ROLE_USER,
            content = question
        )
        QaSessionHolder.appendMessage(userMessage, context)
        input = ""
        sendState = QaSendState.Sending
        val history = QaSessionHolder.session?.messages.orEmpty()

        scope.launch {
            val result = repository.send(
                gameId = activeSession.gameId.ifBlank { context.gameId },
                messages = history,
                context = context
            )
            result.onSuccess { reply ->
                QaSessionHolder.appendMessage(
                    QaMessage(
                        role = QaMessage.ROLE_ASSISTANT,
                        content = reply.trim().ifBlank { "（未收到回答）" }
                    ),
                    context
                )
                sendState = QaSendState.Idle
            }.onFailure {
                QaSessionHolder.appendMessage(
                    QaMessage(
                        role = QaMessage.ROLE_ASSISTANT,
                        content = QA_NETWORK_ERROR_MESSAGE
                    ),
                    context
                )
                sendState = QaSendState.Error(QA_NETWORK_ERROR_MESSAGE)
            }
        }
    }

    Box(
        modifier = modifier
            .fillMaxSize()
            .background(Color.Black.copy(alpha = 0.35f))
    ) {
        Column(
            modifier = Modifier
                .align(Alignment.TopCenter)
                .fillMaxWidth()
                .fillMaxHeight(QaPanelHeightFraction)
                .clip(
                    RoundedCornerShape(
                        bottomStart = 22.dp,
                        bottomEnd = 22.dp
                    )
                )
                .background(QaPanelColor)
                .padding(horizontal = 16.dp)
        ) {
            QaTopBar(
                gameName = currentContext.gameName.ifBlank {
                    game?.nameZh.orEmpty()
                },
                onClose = onClose,
                onNewSession = { startNewSession() }
            )

            QaContextStrip(context = currentContext)

            if (messages.isEmpty()) {
                Box(
                    modifier = Modifier
                        .fillMaxWidth()
                        .weight(1f)
                        .padding(vertical = 18.dp),
                    contentAlignment = Alignment.TopStart
                ) {
                    Text(
                        text = "可以问我这一小节的规则、操作和卡牌含义。\n" +
                            "发送问题时会自动带上当前游戏和播放位置。",
                        color = Color.White.copy(alpha = 0.68f),
                        fontSize = 14.sp,
                        lineHeight = 21.sp
                    )
                }
            } else {
                LazyColumn(
                    modifier = Modifier
                        .fillMaxWidth()
                        .weight(1f),
                    state = listState,
                    verticalArrangement = Arrangement.spacedBy(10.dp)
                ) {
                    items(messages) { message -> QaMessageBubble(message) }
                }
            }

            QaComposer(
                value = input,
                onValueChange = { input = it },
                sending = sending,
                errorMessage = (sendState as? QaSendState.Error)?.message,
                onSend = { sendQuestion() }
            )
        }
    }
}

@Composable
private fun QaTopBar(
    gameName: String,
    onClose: () -> Unit,
    onNewSession: () -> Unit
) {
    Box(
        modifier = Modifier
            .fillMaxWidth()
            .height(54.dp)
    ) {
        TextButton(
            onClick = onClose,
            modifier = Modifier.align(Alignment.CenterStart)
        ) {
            Text(
                text = "继续播放",
                color = Color.White.copy(alpha = 0.9f),
                fontSize = 14.sp,
                fontWeight = FontWeight.Medium
            )
        }

        Text(
            text = gameName.ifBlank { "教程问答" },
            color = Color.White,
            fontSize = 16.sp,
            fontWeight = FontWeight.Bold,
            maxLines = 1,
            overflow = TextOverflow.Ellipsis,
            modifier = Modifier
                .align(Alignment.Center)
                .padding(horizontal = 96.dp)
        )

        TextButton(
            onClick = onNewSession,
            modifier = Modifier.align(Alignment.CenterEnd)
        ) {
            Text(
                text = "新会话",
                color = QaAccentYellow,
                fontSize = 14.sp,
                fontWeight = FontWeight.SemiBold
            )
        }
    }
}

@Composable
private fun QaContextStrip(context: QaContext) {
    val section = context.sectionPath
        .takeIf { it.isNotEmpty() }
        ?.joinToString(" > ")
        ?.takeIf { it.isNotBlank() }
        ?: context.cueText.takeIf { it.isNotBlank() }
        ?: "当前小节"

    Column(
        modifier = Modifier
            .fillMaxWidth()
            .clip(RoundedCornerShape(12.dp))
            .background(Color.White.copy(alpha = 0.07f))
            .padding(horizontal = 12.dp, vertical = 9.dp)
    ) {
        Text(
            text = section,
            color = Color.White.copy(alpha = 0.88f),
            fontSize = 13.sp,
            fontWeight = FontWeight.Medium,
            maxLines = 2,
            overflow = TextOverflow.Ellipsis
        )
        Spacer(Modifier.height(3.dp))
        Text(
            text = buildString {
                append(if (context.cueIndex >= 0) "Cue ${context.cueIndex + 1}" else "Cue --")
                if (context.cueId.isNotBlank()) {
                    append("  ")
                    append(context.cueId)
                }
                append("  ·  ")
                append("${"%.2f".format(context.positionInCue)}s")
            },
            color = Color.White.copy(alpha = 0.56f),
            fontSize = 11.sp,
            maxLines = 1,
            overflow = TextOverflow.Ellipsis
        )
    }
    Spacer(Modifier.height(10.dp))
}

@Composable
private fun QaMessageBubble(message: QaMessage) {
    val isUser = message.role == QaMessage.ROLE_USER
    Row(
        modifier = Modifier.fillMaxWidth(),
        horizontalArrangement = if (isUser) Arrangement.End else Arrangement.Start
    ) {
        Box(
            modifier = Modifier
                .widthIn(max = 560.dp)
                .clip(
                    RoundedCornerShape(
                        topStart = 14.dp,
                        topEnd = 14.dp,
                        bottomStart = if (isUser) 14.dp else 3.dp,
                        bottomEnd = if (isUser) 3.dp else 14.dp
                    )
                )
                .background(
                    if (isUser) {
                        QaAccentYellow
                    } else {
                        Color.White.copy(alpha = 0.10f)
                    }
                )
                .padding(horizontal = 13.dp, vertical = 10.dp)
        ) {
            Text(
                text = message.content,
                color = if (isUser) Color(0xFF1B1B1B) else Color.White.copy(alpha = 0.94f),
                fontSize = 14.sp,
                lineHeight = 21.sp
            )
        }
    }
}

@Composable
private fun QaComposer(
    value: String,
    onValueChange: (String) -> Unit,
    sending: Boolean,
    errorMessage: String?,
    onSend: () -> Unit
) {
    Column(
        modifier = Modifier
            .fillMaxWidth()
            .padding(top = 6.dp, bottom = 12.dp)
    ) {
        if (sending) {
            Text(
                text = "思考中…",
                color = QaAccentYellow.copy(alpha = 0.9f),
                fontSize = 12.sp,
                modifier = Modifier.padding(bottom = 6.dp)
            )
        } else if (!errorMessage.isNullOrBlank()) {
            Text(
                text = errorMessage,
                color = MaterialTheme.colorScheme.error,
                fontSize = 12.sp,
                modifier = Modifier.padding(bottom = 6.dp)
            )
        }

        Row(
            modifier = Modifier.fillMaxWidth(),
            verticalAlignment = Alignment.Bottom
        ) {
            OutlinedTextField(
                value = value,
                onValueChange = onValueChange,
                modifier = Modifier
                    .weight(1f)
                    .widthIn(min = 180.dp),
                enabled = !sending,
                placeholder = {
                    Text(
                        text = "输入问题…",
                        color = Color.White.copy(alpha = 0.42f)
                    )
                },
                maxLines = 4,
                keyboardOptions = KeyboardOptions(imeAction = ImeAction.Send),
                keyboardActions = KeyboardActions(onSend = { onSend() })
            )

            Spacer(Modifier.width(10.dp))

            Button(
                onClick = onSend,
                enabled = !sending && value.isNotBlank(),
                colors = ButtonDefaults.buttonColors(
                    containerColor = QaAccentYellow,
                    contentColor = Color(0xFF1B1B1B),
                    disabledContainerColor = QaAccentYellow.copy(alpha = 0.28f),
                    disabledContentColor = Color(0xFF1B1B1B).copy(alpha = 0.55f)
                ),
                modifier = Modifier.height(52.dp),
                shape = RoundedCornerShape(12.dp)
            ) {
                Text(
                    text = if (sending) "思考中…" else "发送",
                    fontSize = 14.sp,
                    fontWeight = FontWeight.Bold
                )
            }
        }
    }
}
