package com.boardai.tutorial.uaal

import android.graphics.Color
import android.os.Bundle
import android.util.Log
import android.view.Gravity
import android.view.ViewGroup
import android.view.WindowManager
import android.widget.FrameLayout
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.wrapContentHeight
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.darkColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.draw.clip
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color as ComposeColor
import androidx.compose.ui.platform.ComposeView
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.unity3d.player.UnityPlayer
import com.unity3d.player.UnityPlayerGameActivity

/**
 * Kotlin + Jetpack Compose playback shell for the UaaL runtime.
 *
 * The Unity surface remains the Activity's main content view. A ComposeView is
 * added on top through android.R.id.content, so the native controls never take
 * ownership of Unity's rendering view.
 *
 * UnityPlayerGameActivity already derives from an AndroidX ComponentActivity,
 * so its decor view supplies the ViewTree owners that ComposeView needs.
 */
class MainActivity : UnityPlayerGameActivity() {

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)

        keepScreenOn()
        addComposeControlLayer()

        // The bridge defaults to disabled on Android, but send this explicitly so
        // the policy stays visible and can be changed later through the bridge API.
        window.decorView.postDelayed({
            sendToUnity("SetUnityTouchControlsEnabled", "false")
        }, 1_500L)
    }

    override fun onResume() {
        super.onResume()
        // Re-assert after Unity/GameActivity has finished its own window setup.
        keepScreenOn()
    }

    private fun keepScreenOn() {
        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        window.decorView.keepScreenOn = true
    }

    private fun addComposeControlLayer() {
        val composeView = ComposeView(this).apply {
            setBackgroundColor(Color.TRANSPARENT)
            setContent {
                MaterialTheme(colorScheme = darkColorScheme()) {
                    val status = UnityStatusHolder.status.value
                    BoardAiControls(
                        status = status,
                        onCommand = { method, value -> sendToUnity(method, value) }
                    )
                }
            }
        }

        val params = FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MATCH_PARENT,
            ViewGroup.LayoutParams.WRAP_CONTENT,
            Gravity.TOP or Gravity.CENTER_HORIZONTAL
        )
        val content = findViewById<ViewGroup>(android.R.id.content)
        if (content != null) {
            content.addView(composeView, params)
        } else {
            addContentView(composeView, params)
        }
    }

    private fun sendToUnity(method: String, value: String = "") {
        try {
            UnityPlayer.UnitySendMessage(BRIDGE_OBJECT, method, value)
        } catch (t: Throwable) {
            Log.e(TAG, "UnitySendMessage failed: $method($value)", t)
        }
    }

    companion object {
        private const val TAG = "BoardAI-UaaL"
        private const val BRIDGE_OBJECT = "AndroidTutorialBridge"
    }
}

@Composable
private fun BoardAiControls(
    status: UnityStatus?,
    onCommand: (method: String, value: String) -> Unit
) {
    var localPaused by remember { mutableStateOf(false) }
    var localVolume by remember { mutableStateOf(1f) }

    LaunchedEffect(status) {
        status?.let {
            localPaused = it.isPaused
            localVolume = it.volume
        }
    }

    val paused = status?.isPaused ?: localPaused
    val volume = (status?.volume ?: localVolume).coerceIn(0f, 1f)
    val stateLabel = when {
        status == null -> "本地状态"
        paused -> "已暂停"
        status.isPlaying -> "播放中"
        else -> "加载中 / 停止"
    }
    val cueLabel = buildCueLabel(status)

    Box(
        modifier = Modifier
            .fillMaxWidth()
            .wrapContentHeight()
    ) {
        Column(
            modifier = Modifier
                .fillMaxWidth()
                .background(
                    color = ComposeColor(0xD9141414),
                    shape = RoundedCornerShape(bottomStart = 18.dp, bottomEnd = 18.dp)
                )
                .statusBarsPadding()
                .padding(horizontal = 12.dp, vertical = 8.dp)
        ) {
            Row(
                modifier = Modifier.fillMaxWidth(),
                verticalAlignment = Alignment.CenterVertically
            ) {
                Text(
                    text = "BoardAI · $stateLabel",
                    color = ComposeColor.White,
                    fontSize = 14.sp,
                    fontWeight = FontWeight.Medium,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis
                )
                if (cueLabel != null) {
                    Spacer(Modifier.width(12.dp))
                    Text(
                        text = cueLabel,
                        color = ComposeColor.White.copy(alpha = 0.82f),
                        fontSize = 13.sp,
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis,
                        modifier = Modifier.weight(1f)
                    )
                } else {
                    Spacer(Modifier.weight(1f))
                }
                Text(
                    text = "音量 ${(volume * 100f).toInt()}%",
                    color = ComposeColor.White.copy(alpha = 0.92f),
                    fontSize = 14.sp,
                    fontWeight = FontWeight.SemiBold,
                    maxLines = 1
                )
            }

            Spacer(Modifier.height(6.dp))

            Row(
                modifier = Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.spacedBy(8.dp)
            ) {
                ControlButton(
                    label = if (paused) "继续" else "暂停",
                    modifier = Modifier.weight(1f)
                ) {
                    localPaused = !paused
                    onCommand(if (paused) "Resume" else "Pause", "")
                }
                ControlButton(label = "−15 秒", modifier = Modifier.weight(1f)) {
                    onCommand("SeekRelative", "-15")
                }
                ControlButton(label = "+15 秒", modifier = Modifier.weight(1f)) {
                    onCommand("SeekRelative", "15")
                }
                ControlButton(label = "音量 −", modifier = Modifier.weight(1f)) {
                    localVolume = (volume - 0.1f).coerceIn(0f, 1f)
                    onCommand("AdjustVolume", "-0.1")
                }
                ControlButton(label = "音量 +", modifier = Modifier.weight(1f)) {
                    localVolume = (volume + 0.1f).coerceIn(0f, 1f)
                    onCommand("AdjustVolume", "0.1")
                }
            }
        }
    }
}

@Composable
private fun ControlButton(
    label: String,
    modifier: Modifier = Modifier,
    onClick: () -> Unit
) {
    // Do not use a Material ripple here. The Unity Activity deliberately keeps
    // android:hardwareAccelerated=false, and Android 10's RippleDrawable cannot
    // start its RenderNodeAnimator in software-rendered windows.
    val interactionSource = remember { MutableInteractionSource() }
    Box(
        modifier = modifier
            .height(56.dp)
            .clip(RoundedCornerShape(12.dp))
            .background(ComposeColor.White.copy(alpha = 0.14f))
            .clickable(
                interactionSource = interactionSource,
                indication = null,
                onClick = onClick
            ),
        contentAlignment = Alignment.Center
    ) {
        Text(
            text = label,
            color = ComposeColor.White,
            fontSize = 18.sp,
            fontWeight = FontWeight.SemiBold,
            maxLines = 1,
            overflow = TextOverflow.Ellipsis
        )
    }
}

private fun buildCueLabel(status: UnityStatus?): String? {
    if (status == null || status.cueId.isBlank()) return null
    val current = if (status.cueIndex >= 0) status.cueIndex + 1 else "?"
    val total = if (status.cueTotal > 0) status.cueTotal.toString() else "?"
    return "cue $current/$total · ${status.cueId}"
}
