package com.boardai.tutorial.uaal.qa

import org.junit.Assert.assertEquals
import org.junit.Test

class QaPanelModeTest {
    @Test
    fun tutorialQaCloseLabelContinuesPlayback() {
        assertEquals("继续播放", closeLabelForQaMode(isRulesOnly = false))
    }

    @Test
    fun rulesOnlyQaCloseLabelReturnsToHome() {
        assertEquals("返回", closeLabelForQaMode(isRulesOnly = true))
    }
}
