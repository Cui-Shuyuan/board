package com.boardai.tutorial.uaal.qa

import org.junit.Assert.assertEquals
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
}
