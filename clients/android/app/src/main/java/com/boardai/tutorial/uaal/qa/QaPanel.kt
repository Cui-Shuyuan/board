package com.boardai.tutorial.uaal.qa

import android.util.Log
import androidx.compose.animation.core.RepeatMode
import androidx.compose.animation.core.animateFloat
import androidx.compose.animation.core.infiniteRepeatable
import androidx.compose.animation.core.rememberInfiniteTransition
import androidx.compose.animation.core.tween
import androidx.compose.foundation.background
import androidx.compose.foundation.gestures.detectTapGestures
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.KeyboardActions
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.role
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.boardai.tutorial.uaal.UnityStatus
import com.boardai.tutorial.uaal.catalog.GameCatalogEntry
import com.boardai.tutorial.uaal.timeline.TutorialTimeline
import com.boardai.tutorial.uaal.voice.AnswerAudioPlayer
import com.boardai.tutorial.uaal.voice.AsrRepository
import com.boardai.tutorial.uaal.voice.AudioRecorder
import com.boardai.tutorial.uaal.voice.TtsRepository
import kotlinx.coroutines.Job
import kotlinx.coroutines.launch

private const val QA_VOICE_TAG = "BoardAI-QaVoice"

private val QaAccentYellow = Color(0xFFFFC107)
private val QaPanelColor = Color(0x801B1B20)

/** Rules-only QA is opened from the home screen, so closing returns there. */
internal fun closeLabelForQaMode(isRulesOnly: Boolean): String =
    if (isRulesOnly) "返回" else "继续播放"

/**
 * Full-height text QA panel with the composer pinned to the bottom.
 *
 * Playback metadata (game / cue / chapter / position) is built into every
 * request by [buildQaContext], but is intentionally not shown to the guest.
 *
 * Voice is an enhancement: ASR fills the normal text input and TTS runs after
 * the text answer exists.  Any voice failure leaves the text flow untouched.
 */
@Composable
fun QaPanel(
    game: GameCatalogEntry?,
    status: UnityStatus?,
    timeline: TutorialTimeline?,
    repository: QaRepository,
    asrRepository: AsrRepository,
    ttsRepository: TtsRepository,
    hasRecordPermission: () -> Boolean,
    requestRecordPermission: (onResult: (Boolean) -> Unit) -> Unit,
    onClose: () -> Unit,
    modifier: Modifier = Modifier,
    isRulesOnly: Boolean = false
) {
    val currentContext = buildQaContext(game, status, timeline)
    val session = QaSessionHolder.sessionState.value
    val messages = session?.messages.orEmpty()
    val listState = rememberLazyListState()
    val scope = rememberCoroutineScope()
    val appContext = LocalContext.current.applicationContext
    val activeChatJob = remember { mutableStateOf<Job?>(null) }

    val audioRecorder = remember { AudioRecorder(appContext) }
    val answerPlayer = remember { AnswerAudioPlayer() }

    var input by remember { mutableStateOf("") }
    var sendState by remember { mutableStateOf<QaSendState>(QaSendState.Idle) }
    val sending = sendState is QaSendState.Sending

    val voiceController = remember {
        QaVoiceController(
            scope = scope,
            recorder = audioRecorder,
            player = answerPlayer,
            asr = asrRepository,
            tts = ttsRepository,
            autoTtsStore = SharedPrefsQaAutoTtsStore(appContext),
            hasRecordPermission = hasRecordPermission,
            onRecognizedText = { input = it },
            log = { message -> Log.d(QA_VOICE_TAG, message) }
        )
    }
    val voiceState by voiceController.state.collectAsState()

    LaunchedEffect(game?.id) {
        if (game != null) {
            QaSessionHolder.ensureSession(currentContext)
        }
    }

    LaunchedEffect(messages.size, sending) {
        val targetIndex = if (sending) messages.size else messages.lastIndex
        if (messages.isNotEmpty() || sending) {
            listState.animateScrollToItem(targetIndex)
        }
    }

    LaunchedEffect(Unit) {
        if (!hasRecordPermission()) {
            requestRecordPermission { granted ->
                if (!granted) {
                    voiceController.onPermissionResult(false)
                }
            }
        }
    }

    DisposableEffect(Unit) {
        onDispose {
            activeChatJob.value?.cancel()
            activeChatJob.value = null
            repository.cancelActiveRequests()
            voiceController.close()
        }
    }

    fun startNewSession() {
        val context = buildQaContext(game, status, timeline)
        QaSessionHolder.startNewSession(context)
        activeChatJob.value?.cancel()
        activeChatJob.value = null
        repository.cancelActiveRequests()
        voiceController.startNewSession()
        input = ""
        sendState = QaSendState.Idle
    }

    fun sendQuestion() {
        val question = input.trim()
        if (question.isBlank() || sending) return
        val context = buildQaContext(game, status, timeline)
        val activeSession = QaSessionHolder.ensureSession(context)
        val sessionGeneration = activeSession.generation
        val userMessage = QaMessage(
            role = QaMessage.ROLE_USER,
            content = question
        )
        if (!QaSessionHolder.appendMessage(userMessage, context, sessionGeneration)) {
            Log.d(QA_VOICE_TAG, "chat send ignored: session changed while composing")
            return
        }
        input = ""
        sendState = QaSendState.Sending
        val history = QaSessionHolder.session
            ?.takeIf { it.generation == sessionGeneration }
            ?.messages
            .orEmpty()

        activeChatJob.value = scope.launch {
            val result = repository.send(
                gameId = activeSession.gameId.ifBlank { context.gameId },
                messages = history,
                context = context
            )
            if (!QaSessionHolder.isCurrent(sessionGeneration)) {
                Log.d(QA_VOICE_TAG, "stale chat result ignored: session changed")
                return@launch
            }

            result.onSuccess { reply ->
                val replyText = reply.trim().ifBlank { "（未收到回答）" }
                val replyMessage = QaMessage(
                    role = QaMessage.ROLE_ASSISTANT,
                    content = replyText
                )
                if (!QaSessionHolder.appendMessage(replyMessage, context, sessionGeneration)) {
                    Log.d(QA_VOICE_TAG, "stale chat reply ignored: session changed")
                    return@onSuccess
                }
                sendState = QaSendState.Idle
                voiceController.onAssistantReply(replyMessage)
            }.onFailure { error ->
                if (error is kotlinx.coroutines.CancellationException) {
                    return@onFailure
                }
                val errorMessage = QaMessage(
                    role = QaMessage.ROLE_ASSISTANT,
                    content = QA_NETWORK_ERROR_MESSAGE
                )
                if (!QaSessionHolder.appendMessage(errorMessage, context, sessionGeneration)) {
                    Log.d(QA_VOICE_TAG, "stale chat failure ignored: session changed")
                    return@onFailure
                }
                sendState = QaSendState.Error(QA_NETWORK_ERROR_MESSAGE)
            }
        }
    }

    Box(
        modifier = modifier.fillMaxSize()
    ) {
        Column(
            modifier = Modifier
                .fillMaxSize()
                .background(QaPanelColor)
                .padding(horizontal = 16.dp)
        ) {
            QaTopBar(
                gameName = currentContext.gameName.ifBlank {
                    game?.nameZh.orEmpty()
                },
                onClose = {
                    voiceController.stopPlayback()
                    onClose()
                },
                onNewSession = { startNewSession() },
                isRulesOnly = isRulesOnly
            )

            if (messages.isEmpty() && !sending) {
                Spacer(Modifier.weight(1f))
            } else {
                LazyColumn(
                    modifier = Modifier
                        .fillMaxWidth()
                        .weight(1f),
                    state = listState,
                    verticalArrangement = Arrangement.spacedBy(10.dp)
                ) {
                    items(messages) { message ->
                        QaMessageBubble(
                            message = message,
                            isPlaying = voiceState.playingMessageId == message.timestamp,
                            canReplay = message.role == QaMessage.ROLE_ASSISTANT &&
                                !voiceState.ttsBusy &&
                                message.content.isNotBlank(),
                            onReplay = { voiceController.replayAnswer(message) }
                        )
                    }
                    if (sending) {
                        item { QaTypingBubble() }
                    }
                }
            }

            QaComposer(
                value = input,
                onValueChange = { input = it },
                autoTtsEnabled = voiceState.autoTtsEnabled,
                onAutoTtsEnabledChange = { voiceController.setAutoTtsEnabled(it) },
                sending = sending,
                errorMessage = (sendState as? QaSendState.Error)?.message,
                voiceStatus = voiceState.voiceStatus,
                recording = voiceState.isRecording,
                onStartListening = {
                    when (voiceController.onPttDown()) {
                        QaPttStartResult.NEED_PERMISSION -> {
                            requestRecordPermission { granted ->
                                voiceController.onPermissionResult(granted)
                            }
                        }
                        else -> Unit
                    }
                },
                onStopListening = { voiceController.onPttUp() },
                onSend = { sendQuestion() }
            )
        }
    }
}

@Composable
private fun QaTopBar(
    gameName: String,
    onClose: () -> Unit,
    onNewSession: () -> Unit,
    isRulesOnly: Boolean
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
                text = closeLabelForQaMode(isRulesOnly),
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
private fun QaMessageBubble(
    message: QaMessage,
    isPlaying: Boolean,
    canReplay: Boolean,
    onReplay: () -> Unit
) {
    val isUser = message.role == QaMessage.ROLE_USER
    Column(
        modifier = Modifier.fillMaxWidth(),
        horizontalAlignment = if (isUser) Alignment.End else Alignment.Start
    ) {
        Box(
            modifier = Modifier
                .widthIn(max = 1000.dp)
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
                        Color.White.copy(alpha = 0.16f)
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

        if (!isUser) {
            TextButton(
                onClick = onReplay,
                enabled = canReplay,
                modifier = Modifier.padding(top = 1.dp)
            ) {
                Text(
                    text = if (isPlaying) "播放中…" else "重播",
                    color = if (canReplay) QaAccentYellow else QaAccentYellow.copy(alpha = 0.45f),
                    fontSize = 12.sp,
                    fontWeight = FontWeight.Medium
                )
            }
        }
    }
}

@Composable
private fun QaTypingBubble() {
    val transition = rememberInfiniteTransition(label = "qa-typing")
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .semantics { contentDescription = "LLM 正在组织语言" },
        horizontalArrangement = Arrangement.Start
    ) {
        Box(
            modifier = Modifier
                .clip(
                    RoundedCornerShape(
                        topStart = 14.dp,
                        topEnd = 14.dp,
                        bottomStart = 3.dp,
                        bottomEnd = 14.dp
                    )
                )
                .background(Color.White.copy(alpha = 0.16f))
                .padding(horizontal = 14.dp, vertical = 12.dp)
        ) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                repeat(3) { index ->
                    val alpha by transition.animateFloat(
                        initialValue = 0.25f,
                        targetValue = 1f,
                        animationSpec = infiniteRepeatable(
                            animation = tween(
                                durationMillis = 520,
                                delayMillis = index * 160
                            ),
                            repeatMode = RepeatMode.Reverse
                        ),
                        label = "qa-dot-$index"
                    )
                    Box(
                        modifier = Modifier
                            .size(7.dp)
                            .clip(CircleShape)
                            .background(Color.White.copy(alpha = alpha))
                    )
                    if (index < 2) {
                        Spacer(Modifier.width(5.dp))
                    }
                }
            }
        }
    }
}

@Composable
private fun QaComposer(
    value: String,
    onValueChange: (String) -> Unit,
    autoTtsEnabled: Boolean,
    onAutoTtsEnabledChange: (Boolean) -> Unit,
    sending: Boolean,
    errorMessage: String?,
    voiceStatus: String?,
    recording: Boolean,
    onStartListening: () -> Unit,
    onStopListening: () -> Unit,
    onSend: () -> Unit
) {
    var pttPressed by remember { mutableStateOf(false) }

    Column(
        modifier = Modifier
            .fillMaxWidth()
            .padding(top = 6.dp, bottom = 12.dp)
    ) {
        Row(
            modifier = Modifier
                .fillMaxWidth()
                .padding(bottom = 4.dp),
            verticalAlignment = Alignment.CenterVertically
        ) {
            Spacer(Modifier.weight(1f))
            Text(
                text = "回答自动播报",
                color = Color.White.copy(alpha = 0.90f),
                fontSize = 13.sp
            )
            Spacer(Modifier.width(8.dp))
            Switch(
                checked = autoTtsEnabled,
                onCheckedChange = onAutoTtsEnabledChange,
                modifier = Modifier.semantics {
                    contentDescription = "回答自动播报"
                }
            )
        }

        if (!errorMessage.isNullOrBlank()) {
            Text(
                text = errorMessage,
                color = MaterialTheme.colorScheme.error,
                fontSize = 12.sp,
                modifier = Modifier.padding(bottom = 4.dp)
            )
        }

        if (!voiceStatus.isNullOrBlank()) {
            Text(
                text = voiceStatus,
                color = QaAccentYellow,
                fontSize = 12.sp,
                modifier = Modifier.padding(bottom = 4.dp)
            )
        }

        Row(
            modifier = Modifier.fillMaxWidth(),
            verticalAlignment = Alignment.Bottom
        ) {
            Box(
                modifier = Modifier
                    .height(52.dp)
                    .width(98.dp)
                    .clip(RoundedCornerShape(12.dp))
                    .background(
                        if (pttPressed || recording) {
                            Color.White
                        } else {
                            QaAccentYellow
                        }
                    )
                    .pointerInput(sending) {
                        if (!sending) {
                            detectTapGestures(
                                onPress = {
                                    pttPressed = true
                                    try {
                                        onStartListening()
                                        tryAwaitRelease()
                                    } finally {
                                        pttPressed = false
                                        onStopListening()
                                    }
                                }
                            )
                        }
                    }
                    .semantics {
                        role = Role.Button
                        contentDescription = "按住说话"
                    },
                contentAlignment = Alignment.Center
            ) {
                Text(
                    text = when {
                        recording -> "放手结束"
                        pttPressed -> "聆听中…"
                        else -> "按住说话"
                    },
                    color = Color(0xFF1B1B1B),
                    fontSize = 12.sp,
                    fontWeight = FontWeight.Bold
                )
            }

            Spacer(Modifier.width(8.dp))

            OutlinedTextField(
                value = value,
                onValueChange = onValueChange,
                modifier = Modifier
                    .weight(1f)
                    .widthIn(min = 140.dp),
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

            Spacer(Modifier.width(8.dp))

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
                    text = "发送",
                    fontSize = 14.sp,
                    fontWeight = FontWeight.Bold
                )
            }
        }
    }
}
