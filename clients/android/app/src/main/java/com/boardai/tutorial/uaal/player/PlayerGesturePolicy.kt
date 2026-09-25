package com.boardai.tutorial.uaal.player

/**
 * High-level policy for full-screen horizontal gestures.
 *
 * The default remains [VIDEO_SCRUB] so playback behaviour is unchanged.  The
 * reserved mode lets future interaction work claim full-screen horizontal drags
 * while still allowing seek through the progress bar.
 */
enum class PlayerGestureMode {
    VIDEO_SCRUB,
    INTERACTION_RESERVED
}

class PlayerGesturePolicy(
    val mode: PlayerGestureMode = PlayerGestureMode.VIDEO_SCRUB
) {
    val allowFullScreenHorizontalSeek: Boolean
        get() = mode == PlayerGestureMode.VIDEO_SCRUB
}
