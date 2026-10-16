package com.boardai.tutorial.uaal.home

/**
 * What a home-screen game selection should do next.
 *
 * The same game list / recent row is reused for both tutorial playback and
 * rule questions; only one mode can be active at a time.
 */
enum class HomeMode {
    ANIMATION,
    QUESTION
}
