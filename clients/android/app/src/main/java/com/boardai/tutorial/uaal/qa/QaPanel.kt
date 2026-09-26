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
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
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
import com.boardai.tutorial.uaal.voice.DEFAULT_TTS_VOICE
import com.boardai.tutorial.uaal.voice.TtsRepository
import kotlinx.coroutines.launch
import java.io.File

private const val QA_VOICE_TAG = "BoardAI-QaVoice"

private val QaAccentYellow = Color(0xFFFFC107)
private val QaPanelColor = Color(0x801B1B20)

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
    modifier: Modifier = Modifier
) {
    val currentContext = buildQaContext(game, status, timeline)
    val session = QaSessionHolder.sessionState.value
    val messages = session?.messages.orEmpty()
    val listState = rememberLazyListState()
    val scope = rememberCoroutineScope()
    val appContext = LocalContext.current.applicationContext

    val audioRecorder = remember { AudioRecorder(appContext) }
    val answerPlayer = remember { AnswerAudioPlayer() }

    var input by remember { mutableStateOf("") }
    var sendState by remember { mutableStateOf<QaSendState>(QaSendState.Idle) }
    val sending = sendState is QaSendState.Sending

    var voiceStatus by remember { mutableStateOf<String?>(null) }
    var isRecording by remember { mutableStateOf(false) }
    var recordingToken by remember { mutableStateOf(0) }
    var ttsBusy by remember { mutableStateOf(false) }
    var ttsMessageInFlight by remember { mutableStateOf<Long?>(null) }
    var answerAudioFiles by remember { mutableStateOf<Map<Long, File>>(emptyMap()) }
    var playingMessageId by remember { mutableStateOf<Long?>(null) }

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
                    voiceStatus = "未授予麦克风权限，可键盘输入"
                }
            }
        }
    }

    DisposableEffect(Unit) {
        onDispose {
            audioRecorder.cancel()
            answerPlayer.stop()
        }
    }

    fun stopPlayback() {
        answerPlayer.stop()
        playingMessageId = null
    }

    fun handleRecorded(file: File?) {
        if (file == null) {
            Log.d(QA_VOICE_TAG, "recording dropped: too short or invalid")
            voiceStatus = "录音太短，请按住多说一会儿"
            return
        }

        Log.d(QA_VOICE_TAG, "ASR request starting, wavBytes=${file.length()}")
        voiceStatus = "识别中…"
        scope.launch {
            try {
                asrRepository.transcribe(file).onSuccess { recognized ->
                    val text = recognized.trim()
                    if (text.isBlank()) {
                        Log.w(QA_VOICE_TAG, "ASR success but text blank")
                        voiceStatus = "没有识别到内容，请重试"
                    } else {
                        Log.d(QA_VOICE_TAG, "ASR success: textLength=${text.length}")
                        input = text
                        voiceStatus = null
                    }
                }.onFailure { error ->
                    Log.w(
                        QA_VOICE_TAG,
                        "ASR failed: ${error.javaClass.simpleName}: ${error.message}"
                    )
                    voiceStatus = "识别失败，可键盘输入"
                }
            } finally {
                val deleted = file.delete()
                Log.d(QA_VOICE_TAG, "ASR temp wav deleted=$deleted")
            }
        }
    }

    fun beginRecording() {
        Log.d(QA_VOICE_TAG, "beginRecording requested, isRecording=$isRecording")
        if (isRecording) return
        stopPlayback()

        val token = recordingToken + 1
        recordingToken = token
        val started = audioRecorder.start { file ->
            if (recordingToken == token) {
                isRecording = false
                recordingToken += 1
                handleRecorded(file)
            }
        }
        Log.d(QA_VOICE_TAG, "audioRecorder.start returned started=$started")

        if (started) {
            isRecording = true
            voiceStatus = "正在聆听…"
        } else {
            voiceStatus = "录音启动失败，请重试"
        }
    }

    fun startListening() {
        Log.d(QA_VOICE_TAG, "PTT down: isRecording=$isRecording")
        if (isRecording) return
        stopPlayback()
        voiceStatus = null

        if (!hasRecordPermission()) {
            Log.d(QA_VOICE_TAG, "PTT down without RECORD_AUDIO permission")
            voiceStatus = "需要麦克风权限"
            requestRecordPermission { granted ->
                voiceStatus = if (granted) {
                    "权限已授予，请再次按住说话"
                } else {
                    "未授予麦克风权限，可键盘输入"
                }
            }
            return
        }

        Log.d(QA_VOICE_TAG, "PTT down with RECORD_AUDIO permission")
        beginRecording()
    }

    fun finishListening() {
        Log.d(QA_VOICE_TAG, "PTT up: isRecording=$isRecording")
        if (!isRecording) return
        Log.d(QA_VOICE_TAG, "PTT recording stopping")
        isRecording = false
        recordingToken += 1
        handleRecorded(audioRecorder.stop())
    }

    fun playAnswerFile(message: QaMessage, file: File) {
        stopPlayback()
        playingMessageId = message.timestamp
        answerPlayer.play(
            file = file,
            onCompletion = {
                if (playingMessageId == message.timestamp) {
                    playingMessageId = null
                }
            },
            onError = {
                if (playingMessageId == message.timestamp) {
                    playingMessageId = null
                }
                voiceStatus = "语音播放失败，已保留文字回答"
            }
        )
    }

    fun synthesizeAndPlay(message: QaMessage) {
        val existing = answerAudioFiles[message.timestamp]
        if (existing != null && existing.exists()) {
            playAnswerFile(message, existing)
            return
        }

        if (ttsMessageInFlight == message.timestamp) return
        ttsMessageInFlight = message.timestamp
        ttsBusy = true
        voiceStatus = "正在合成语音…"

        scope.launch {
            ttsRepository.synthesize(
                text = message.content,
                voice = DEFAULT_TTS_VOICE,
                speed = 1.0
            ).onSuccess { file ->
                answerAudioFiles = answerAudioFiles + (message.timestamp to file)
                if (ttsMessageInFlight == message.timestamp) {
                    ttsMessageInFlight = null
                }
                ttsBusy = false
                if (voiceStatus == "正在合成语音…") {
                    voiceStatus = null
                }
                playAnswerFile(message, file)
            }.onFailure {
                if (ttsMessageInFlight == message.timestamp) {
                    ttsMessageInFlight = null
                }
                ttsBusy = false
                voiceStatus = "语音合成失败，已保留文字回答"
            }
        }
    }

    fun replayAnswer(message: QaMessage) {
        val existing = answerAudioFiles[message.timestamp]
        if (existing != null && existing.exists()) {
            playAnswerFile(message, existing)
        } else {
            synthesizeAndPlay(message)
        }
    }

    fun startNewSession() {
        stopPlayback()
        answerAudioFiles = emptyMap()
        ttsMessageInFlight = null
        ttsBusy = false
        voiceStatus = null
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
                val replyText = reply.trim().ifBlank { "（未收到回答）" }
                val replyMessage = QaMessage(
                    role = QaMessage.ROLE_ASSISTANT,
                    content = replyText
                )
                QaSessionHolder.appendMessage(replyMessage, context)
                sendState = QaSendState.Idle
                if (replyText != "（未收到回答）") {
                    synthesizeAndPlay(replyMessage)
                }
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
                    stopPlayback()
                    onClose()
                },
                onNewSession = { startNewSession() }
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
                            isPlaying = playingMessageId == message.timestamp,
                            canReplay = message.role == QaMessage.ROLE_ASSISTANT &&
                                !ttsBusy &&
                                message.content.isNotBlank(),
                            onReplay = { replayAnswer(message) }
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
                sending = sending,
                errorMessage = (sendState as? QaSendState.Error)?.message,
                voiceStatus = voiceStatus,
                recording = isRecording,
                onStartListening = { startListening() },
                onStopListening = { finishListening() },
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
