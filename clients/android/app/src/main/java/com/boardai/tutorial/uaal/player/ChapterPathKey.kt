package com.boardai.tutorial.uaal.player

import com.boardai.tutorial.uaal.timeline.ChapterNode

internal fun chapterPathKey(path: List<String>): String =
    path.joinToString(ChapterNode.CHAPTER_KEY_SEPARATOR)
