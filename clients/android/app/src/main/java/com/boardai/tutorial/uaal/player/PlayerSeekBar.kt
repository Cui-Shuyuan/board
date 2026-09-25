package com.boardai.tutorial.uaal.player

import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.material3.Slider
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier

@Composable
internal fun PlayerSeekBar(
    scrubbing: Boolean,
    scrubGlobal: Float,
    shownGlobal: Float,
    progressTotal: Float,
    enabled: Boolean,
    onScrubStart: (Float) -> Unit,
    onScrubChange: (Float) -> Unit,
    onScrubFinished: () -> Unit,
    modifier: Modifier = Modifier
) {
    Slider(
        value = (if (scrubbing) scrubGlobal else shownGlobal)
            .coerceIn(0f, progressTotal),
        valueRange = 0f..progressTotal,
        onValueChange = { value ->
            if (enabled) {
                if (!scrubbing) onScrubStart(value) else onScrubChange(value)
            }
        },
        onValueChangeFinished = {
            if (enabled) onScrubFinished()
        },
        modifier = modifier.fillMaxWidth()
    )
}
