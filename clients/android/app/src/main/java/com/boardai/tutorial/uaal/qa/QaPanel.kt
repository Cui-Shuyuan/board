package com.boardai.tutorial.uaal.qa

import android.os.SystemClock
import android.util.Log
import android.widget.Toast
import androidx.compose.animation.core.RepeatMode
import androidx.compose.animation.core.animateFloat
import androidx.compose.animation.core.infiniteRepeatable
import androidx.compose.animation.core.rememberInfiniteTransition
import androidx.compose.animation.core.tween
import androidx.compose.foundation.background
import androidx.compose.foundation.gestures.awaitEachGesture
import androidx.compose.foundation.gestures.awaitFirstDown
import androidx.compose.foundation.gestures.detectTapGestures
import androidx.compose.foundation.gestures.detectVerticalDragGestures
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.offset
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
import androidx.compose.ui.platform.LocalFocusManager
import androidx.compose.ui.platform.LocalSoftwareKeyboardController
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.role
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.zIndex
import com.boardai.tutorial.uaal.UnityStatus
import com.boardai.tutorial.uaal.catalog.GameCatalogEntry
import com.boardai.tutorial.uaal.timeline.TutorialTimeline
import com.boardai.tutorial.uaal.voice.AnswerAudioPlayer
import com.boardai.tutorial.uaal.voice.AsrRepository
import com.boardai.tutorial.uaal.voice.AudioRecorder
import com.boardai.tutorial.uaal.voice.TtsRepository
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch

private const val QA_VOICE_TAG = "BoardAI-QaVoice"

private val QaAccentYellow = Color(0xFFFFC107)
private val QaPanelColor = Color(0x801B1B20)
private val QaPanelOpaqueColor = Color(0xFF1B1B20)

internal const val QA_SWIPE_UP_CLOSE_DP = 56f

/** Standalone QA is opened from the home screen, so closing returns there. */
internal fun closeLabelForQaMode(isStandalone: Boolean): String =
    if (isStandalone) "返回" else "继续播放"

internal fun qaSwipeUpHint(isStandalone: Boolean): String =
    if (isStandalone) "向上滑动返回首页" else "向上滑动返回播放"

internal fun shouldCloseQaOnUpSwipe(totalDyPx: Float, thresholdPx: Float): Boolean =
    thresholdPx > 0f && totalDyPx <= -thresholdPx

/**
 * Swipe-up dismiss for the non-scrollable QA chrome.  The chat list keeps
 * normal vertical scrolling; this modifier is attached to the header and the
 * empty state so an upward gesture cannot steal a conversation scroll.
 */
private fun Modifier.qaSwipeUpToClose(onSwipeUp: () -> Unit): Modifier =
    pointerInput(onSwipeUp) {
        var totalDyPx = 0f
        var triggered = false
        val thresholdPx = QA_SWIPE_UP_CLOSE_DP.dp.toPx()
        detectVerticalDragGestures(
            onDragStart = {
                totalDyPx = 0f
                triggered = false
            },
            onVerticalDrag = { change, dragAmount ->
                totalDyPx += dragAmount
                if (!triggered && shouldCloseQaOnUpSwipe(totalDyPx, thresholdPx)) {
                    triggered = true
                    change.consume()
                    onSwipeUp()
                }
            }
        )
    }

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
    isStandalone: Boolean = false,
    debugInfoEnabled: Boolean = false
) {
    val currentContext = buildQaContext(game, status, timeline)
    val session = QaSessionHolder.sessionState.value
    val messages = session?.messages.orEmpty()
    val listState = rememberLazyListState()
    val scope = rememberCoroutineScope()
    val appContext = LocalContext.current.applicationContext
    val focusManager = LocalFocusManager.current
    val keyboardController = LocalSoftwareKeyboardController.current
    val activeChatJob = remember { mutableStateOf<Job?>(null) }

    val audioRecorder = remember { AudioRecorder(appContext) }
    val answerPlayer = remember { AnswerAudioPlayer() }

    var input by remember { mutableStateOf("") }
    var autoSendText by remember { mutableStateOf<String?>(null) }
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
            onRecognizedText = { recognized ->
                // Voice input is sent directly. Keep any text draft intact so
                // the user can switch back to the keyboard without losing it.
                autoSendText = recognized
            },
            log = { message -> Log.d(QA_VOICE_TAG, message) }
        )
    }
    val voiceState by voiceController.state.collectAsState()
    val recognizing = isAsrRecognizing(voiceState.voiceStatus)

    LaunchedEffect(game?.id) {
        if (game != null) {
            QaSessionHolder.ensureSession(currentContext)
        }
    }

    LaunchedEffect(messages.size, sending, recognizing) {
        val extraItemCount = (if (recognizing) 1 else 0) + (if (sending) 1 else 0)
        val itemCount = messages.size + extraItemCount
        if (itemCount > 0) {
            listState.animateScrollToItem(itemCount - 1)
        }
    }

    LaunchedEffect(voiceState.voiceStatus) {
        when (voiceState.voiceStatus) {
            "已取消" -> {
                delay(3_000)
                voiceController.clearVoiceStatus()
            }
            "识别失败，可键盘输入" -> {
                Toast.makeText(appContext, voiceState.voiceStatus, Toast.LENGTH_SHORT).show()
                voiceController.clearVoiceStatus()
            }
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
        autoSendText = null
        sendState = QaSendState.Idle
    }

    fun sendQuestion(questionOverride: String? = null) {
        val question = (questionOverride ?: input).trim()
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
        if (questionOverride == null) {
            input = ""
        }
        sendState = QaSendState.Sending
        val history = QaSessionHolder.session
            ?.takeIf { it.generation == sessionGeneration }
            ?.messages
            .orEmpty()

        activeChatJob.value = scope.launch {
            val startedAtMs = SystemClock.elapsedRealtime()
            val result = repository.send(
                gameId = activeSession.gameId.ifBlank { context.gameId },
                messages = history,
                context = context
            )
            if (!QaSessionHolder.isCurrent(sessionGeneration)) {
                Log.d(QA_VOICE_TAG, "stale chat result ignored: session changed")
                return@launch
            }

            result.onSuccess { chat ->
                val replyText = chat.reply.trim().ifBlank { "（未收到回答）" }
                val evidence = chat.evidence
                val debugSummary = qaAnswerDebugSummary(
                    chat,
                    SystemClock.elapsedRealtime() - startedAtMs
                )
                Log.i(QA_VOICE_TAG, debugSummary)
                val replyMessage = QaMessage(
                    role = QaMessage.ROLE_ASSISTANT,
                    content = replyText,
                    evidence = evidence,
                    debugSummary = debugSummary
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

    LaunchedEffect(autoSendText) {
        val pending = autoSendText ?: return@LaunchedEffect
        autoSendText = null
        if (pending.isNotBlank()) {
            sendQuestion(pending)
        }
    }

    val panelColor = if (isStandalone) QaPanelOpaqueColor else QaPanelColor

    fun closePanel() {
        voiceController.stopPlayback()
        onClose()
    }

    Box(
        modifier = modifier
            .fillMaxSize()
            .pointerInput(Unit) {
                detectTapGestures {
                    focusManager.clearFocus()
                    keyboardController?.hide()
                }
            }
    ) {
        Column(
            modifier = Modifier
                .fillMaxSize()
                .background(panelColor)
                .padding(horizontal = 16.dp)
        ) {
            QaTopBar(
                gameName = currentContext.gameName.ifBlank {
                    game?.nameZh.orEmpty()
                },
                onClose = { closePanel() },
                onNewSession = { startNewSession() },
                isStandalone = isStandalone,
                onSwipeUpToClose = { closePanel() }
            )

            if (messages.isEmpty() && !sending && !recognizing) {
                Box(
                    modifier = Modifier
                        .fillMaxWidth()
                        .weight(1f)
                        .qaSwipeUpToClose { closePanel() },
                    contentAlignment = Alignment.Center
                ) {
                    Text(
                        text = qaSwipeUpHint(isStandalone),
                        color = Color.White.copy(alpha = 0.38f),
                        fontSize = 12.sp,
                        fontWeight = FontWeight.Medium
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
                    items(messages) { message ->
                        QaMessageBubble(
                            message = message,
                            isPlaying = voiceState.playingMessageId == message.timestamp,
                            canReplay = message.role == QaMessage.ROLE_ASSISTANT &&
                                !voiceState.ttsBusy &&
                                message.content.isNotBlank(),
                            debugInfoEnabled = debugInfoEnabled,
                            onReplay = { voiceController.replayAnswer(message) }
                        )
                    }
                    if (recognizing) {
                        item(key = "qa-recognizing") { QaRecognizingBubble() }
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
                onCancelListening = { voiceController.cancelRecording() },
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
    isStandalone: Boolean,
    onSwipeUpToClose: () -> Unit
) {
    Box(
        modifier = Modifier
            .fillMaxWidth()
            .height(54.dp)
            .qaSwipeUpToClose(onSwipeUpToClose)
    ) {
        TextButton(
            onClick = onClose,
            modifier = Modifier.align(Alignment.CenterStart)
        ) {
            Text(
                text = closeLabelForQaMode(isStandalone),
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
    debugInfoEnabled: Boolean,
    onReplay: () -> Unit
) {
    val isUser = message.role == QaMessage.ROLE_USER
    val debugSummary = message.debugSummary.takeIf { debugInfoEnabled && !isUser }
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

            if (debugSummary != null) {
                Text(
                    text = debugSummary,
                    color = QaAccentYellow.copy(alpha = 0.92f),
                    fontSize = 11.sp,
                    lineHeight = 15.sp,
                    modifier = Modifier.padding(start = 2.dp, top = 1.dp, end = 2.dp)
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
private fun QaRecognizingBubble() {
    val transition = rememberInfiniteTransition(label = "qa-recognizing")
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .semantics { contentDescription = "正在识别语音" },
        horizontalArrangement = Arrangement.End
    ) {
        Box(
            modifier = Modifier
                .clip(
                    RoundedCornerShape(
                        topStart = 14.dp,
                        topEnd = 14.dp,
                        bottomStart = 14.dp,
                        bottomEnd = 3.dp
                    )
                )
                .background(QaAccentYellow)
                .padding(horizontal = 14.dp, vertical = 12.dp)
        ) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                repeat(3) { index ->
                    val lift by transition.animateFloat(
                        initialValue = 0f,
                        targetValue = 1f,
                        animationSpec = infiniteRepeatable(
                            animation = tween(
                                durationMillis = 420,
                                delayMillis = index * 130
                            ),
                            repeatMode = RepeatMode.Reverse
                        ),
                        label = "qa-recognizing-dot-$index"
                    )
                    Box(
                        modifier = Modifier
                            .offset(y = (-2).dp * lift)
                            .size(7.dp)
                            .clip(CircleShape)
                            .background(
                                Color(0xFF1B1B1B).copy(alpha = 0.35f + 0.65f * lift)
                            )
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
    recording: Boolean,
    onStartListening: () -> Unit,
    onStopListening: () -> Unit,
    onCancelListening: () -> Unit,
    onSend: () -> Unit
) {
    var inputMode by remember { mutableStateOf(QaInputMode.TEXT) }
    val focusManager = LocalFocusManager.current
    val keyboardController = LocalSoftwareKeyboardController.current

    fun switchInputMode() {
        if (inputMode == QaInputMode.TEXT) {
            focusManager.clearFocus()
            keyboardController?.hide()
            inputMode = QaInputMode.VOICE
        } else {
            // Show the text field without forcing focus or opening the
            // keyboard. The user can tap the field when ready.
            inputMode = QaInputMode.TEXT
        }
    }

    Column(
        modifier = Modifier
            .fillMaxWidth()
            .padding(top = 6.dp, bottom = 12.dp)
    ) {
        if (!errorMessage.isNullOrBlank()) {
            Text(
                text = errorMessage,
                color = MaterialTheme.colorScheme.error,
                fontSize = 12.sp,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
                modifier = Modifier
                    .fillMaxWidth()
                    .padding(bottom = 4.dp)
            )
        }

        Row(
            modifier = Modifier.fillMaxWidth(),
            verticalAlignment = Alignment.Bottom
        ) {
            Button(
                onClick = { switchInputMode() },
                enabled = !sending && !recording,
                colors = ButtonDefaults.buttonColors(
                    containerColor = Color.White.copy(alpha = 0.15f),
                    contentColor = Color.White,
                    disabledContainerColor = Color.White.copy(alpha = 0.08f),
                    disabledContentColor = Color.White.copy(alpha = 0.38f)
                ),
                modifier = Modifier
                    .height(52.dp)
                    .width(86.dp)
                    .semantics {
                        contentDescription = if (inputMode == QaInputMode.TEXT) {
                            "切换到语音输入"
                        } else {
                            "切换到键盘输入"
                        }
                    },
                shape = RoundedCornerShape(12.dp)
            ) {
                Text(
                    text = if (inputMode == QaInputMode.TEXT) "语音" else "键盘",
                    fontSize = 14.sp,
                    fontWeight = FontWeight.Bold
                )
            }

            Spacer(Modifier.width(8.dp))

            if (inputMode == QaInputMode.TEXT) {
                OutlinedTextField(
                    value = value,
                    onValueChange = onValueChange,
                    modifier = Modifier
                        .weight(1f)
                        .widthIn(min = 120.dp),
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
                Spacer(Modifier.width(8.dp))
            } else {
                QaPushToTalkButton(
                    sending = sending,
                    recording = recording,
                    onStartListening = onStartListening,
                    onStopListening = onStopListening,
                    onCancelListening = onCancelListening,
                    modifier = Modifier
                        .weight(1f)
                        .height(52.dp)
                )
                Spacer(Modifier.width(8.dp))
            }

            Row(
                modifier = Modifier.align(Alignment.CenterVertically),
                verticalAlignment = Alignment.CenterVertically
            ) {
                Text(
                    text = "回答自动播报",
                    color = Color.White.copy(alpha = 0.90f),
                    fontSize = 12.sp,
                    maxLines = 1
                )
                Spacer(Modifier.width(4.dp))
                Switch(
                    checked = autoTtsEnabled,
                    onCheckedChange = onAutoTtsEnabledChange,
                    modifier = Modifier.semantics {
                        contentDescription = "回答自动播报"
                    }
                )
            }
        }
    }
}

@Composable
private fun QaPushToTalkButton(
    sending: Boolean,
    recording: Boolean,
    onStartListening: () -> Unit,
    onStopListening: () -> Unit,
    onCancelListening: () -> Unit,
    modifier: Modifier = Modifier
) {
    var pttPressed by remember { mutableStateOf(false) }
    var cancelSelected by remember { mutableStateOf(false) }
    var listeningDotCount by remember { mutableStateOf(1) }
    val listening = pttPressed || recording

    LaunchedEffect(listening) {
        if (!listening) {
            listeningDotCount = 1
            return@LaunchedEffect
        }
        while (true) {
            delay(450)
            listeningDotCount = listeningDotCount % 3 + 1
        }
    }

    Box(modifier = modifier) {
        if (pttPressed || recording || cancelSelected) {
            Column(
                modifier = Modifier
                    .align(Alignment.TopCenter)
                    .offset(y = (-76).dp)
                    .zIndex(2f),
                horizontalAlignment = Alignment.CenterHorizontally
            ) {
                Text(
                    text = if (cancelSelected) "松手取消" else "上划取消",
                    color = if (cancelSelected) {
                        Color(0xFFE5484D)
                    } else {
                        Color.White.copy(alpha = 0.92f)
                    },
                    fontSize = 15.sp,
                    fontWeight = FontWeight.Bold,
                    maxLines = 1
                )
                Spacer(Modifier.height(3.dp))
                Box(
                    modifier = Modifier
                        .size(36.dp)
                        .clip(CircleShape)
                        .background(
                            if (cancelSelected) {
                                Color(0xFFE5484D)
                            } else {
                                Color(0xCC3A3F4B)
                            }
                        ),
                    contentAlignment = Alignment.Center
                ) {
                    Text(
                        text = "✕",
                        color = Color.White,
                        fontSize = 18.sp,
                        fontWeight = FontWeight.Bold
                    )
                }
            }
        }

        Box(
            modifier = Modifier
                .fillMaxSize()
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
                        awaitEachGesture {
                            val down = awaitFirstDown(requireUnconsumed = false)
                            down.consume()
                            val cancelCenterX = size.width / 2f
                            val cancelActivationY = (-24).dp.toPx()
                            val cancelHalfWidth = size.width / 2f + 16.dp.toPx()
                            pttPressed = true
                            cancelSelected = false
                            onStartListening()

                            var completed = false
                            try {
                                while (true) {
                                    val event = awaitPointerEvent()
                                    val change = event.changes.firstOrNull {
                                        it.id == down.id
                                    }
                                    if (change == null) {
                                        onCancelListening()
                                        completed = true
                                        break
                                    }

                                    if (!change.pressed) {
                                        if (cancelSelected) {
                                            onCancelListening()
                                        } else {
                                            onStopListening()
                                        }
                                        completed = true
                                        change.consume()
                                        break
                                    }

                                    val insideHorizontal =
                                        change.position.x >= cancelCenterX - cancelHalfWidth &&
                                            change.position.x <= cancelCenterX + cancelHalfWidth
                                    val overCancel =
                                        change.position.y <= cancelActivationY && insideHorizontal
                                    if (overCancel != cancelSelected) {
                                        cancelSelected = overCancel
                                    }
                                    change.consume()
                                }
                            } finally {
                                pttPressed = false
                                if (!completed) {
                                    onCancelListening()
                                }
                                cancelSelected = false
                            }
                        }
                    }
                }
                .semantics {
                    role = Role.Button
                    contentDescription = "按住说话"
                },
            contentAlignment = Alignment.Center
        ) {
            if (listening) {
                Column(horizontalAlignment = Alignment.CenterHorizontally) {
                    Text(
                        text = qaListeningLabel(listeningDotCount),
                        color = Color(0xFF1B1B1B),
                        fontSize = 13.sp,
                        fontWeight = FontWeight.Bold,
                        maxLines = 1
                    )
                    Text(
                        text = "放手结束",
                        color = Color(0xFF1B1B1B).copy(alpha = 0.72f),
                        fontSize = 11.sp,
                        fontWeight = FontWeight.Medium,
                        maxLines = 1
                    )
                }
            } else {
                Text(
                    text = "按住说话",
                    color = Color(0xFF1B1B1B),
                    fontSize = 12.sp,
                    fontWeight = FontWeight.Bold
                )
            }
        }
    }
}
