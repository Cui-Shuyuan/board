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
}
