package com.boardai.tutorial.uaal.player

import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class TutorialPlayerOverlayGestureTest {
    @Test
    fun downwardSwipePastThresholdOpensQa() {
        assertTrue(shouldOpenQaOnDownSwipe(totalDyPx = 72f, thresholdPx = 56f))
    }

    @Test
    fun downwardSwipeBeforeThresholdKeepsPlayback() {
        assertFalse(shouldOpenQaOnDownSwipe(totalDyPx = 40f, thresholdPx = 56f))
    }

    @Test
    fun upwardSwipeNeverOpensQa() {
        assertFalse(shouldOpenQaOnDownSwipe(totalDyPx = -72f, thresholdPx = 56f))
    }

    @Test
    fun invalidThresholdDoesNotOpenQa() {
        assertFalse(shouldOpenQaOnDownSwipe(totalDyPx = 100f, thresholdPx = 0f))
    }
}
