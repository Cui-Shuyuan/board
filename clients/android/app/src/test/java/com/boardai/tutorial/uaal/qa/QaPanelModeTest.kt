package com.boardai.tutorial.uaal.qa

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class QaPanelModeTest {
    @Test
    fun tutorialQaCloseLabelContinuesPlayback() {
        assertEquals("继续播放", closeLabelForQaMode(isStandalone = false))
    }

    @Test
    fun standaloneQaCloseLabelReturnsToHome() {
        assertEquals("返回", closeLabelForQaMode(isStandalone = true))
    }

    @Test
    fun tutorialQaSwipeUpHintReturnsToPlayback() {
        assertEquals("向上滑动返回播放", qaSwipeUpHint(isStandalone = false))
    }

    @Test
    fun standaloneQaSwipeUpHintReturnsHome() {
        assertEquals("向上滑动返回首页", qaSwipeUpHint(isStandalone = true))
    }

    @Test
    fun upwardSwipePastThresholdClosesQa() {
        assertTrue(shouldCloseQaOnUpSwipe(totalDyPx = -72f, thresholdPx = 56f))
    }

    @Test
    fun upwardSwipeBeforeThresholdKeepsQaOpen() {
        assertFalse(shouldCloseQaOnUpSwipe(totalDyPx = -40f, thresholdPx = 56f))
    }

    @Test
    fun downwardSwipeNeverClosesQa() {
        assertFalse(shouldCloseQaOnUpSwipe(totalDyPx = 72f, thresholdPx = 56f))
    }

    @Test
    fun invalidThresholdDoesNotCloseQa() {
        assertFalse(shouldCloseQaOnUpSwipe(totalDyPx = -100f, thresholdPx = 0f))
    }

    @Test
    fun typedVoiceStateControlsTemporaryRecognizingBubble() {
        assertEquals(
            QaAsrState.RECOGNIZING,
            QaVoiceState(asr = QaAsrState.RECOGNIZING).asr
        )
        assertEquals(
            QaAsrState.IDLE,
            QaVoiceState(asr = QaAsrState.IDLE).asr
        )
    }

    @Test
    fun voiceAndSendErrorsMapToDisplayCopyOnlyInUiLayer() {
        assertEquals(
            "识别失败，可键盘输入",
            qaVoiceNoticeText(QaVoiceNotice.RECOGNITION_FAILED)
        )
        assertEquals(QA_NETWORK_ERROR_MESSAGE, qaSendErrorMessage(QaSendState.NetworkError))
        assertEquals(null, qaSendErrorMessage(QaSendState.Idle))
    }

    @Test
    fun listeningLabelCyclesAsciiDots() {
        assertEquals("正在聆听.", qaListeningLabel(1))
        assertEquals("正在聆听..", qaListeningLabel(2))
        assertEquals("正在聆听...", qaListeningLabel(3))
        assertEquals("正在聆听.", qaListeningLabel(0))
        assertEquals("正在聆听...", qaListeningLabel(9))
    }
}
