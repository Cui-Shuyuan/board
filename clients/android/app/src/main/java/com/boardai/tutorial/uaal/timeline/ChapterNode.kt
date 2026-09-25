package com.boardai.tutorial.uaal.timeline

/**
 * A node in the chapter tree built from runtime cue `group_path` arrays.
 *
 * The synthetic root is represented by `TutorialTimeline.rootChapters`; every
 * real node has a non-empty [path].  [firstCueIndex] and [firstCueId] point at
 * the first cue in this chapter or any descendant chapter, so a parent node can
 * offer "jump to this section".
 */
data class ChapterNode(
    val path: List<String>,
    val title: String,
    val children: MutableList<ChapterNode> = mutableListOf()
) {
    var firstCueIndex: Int = -1
    var firstCueId: String = ""

    val key: String get() = path.joinToString(CHAPTER_KEY_SEPARATOR)

    val depth: Int get() = path.size

    val isLeaf: Boolean get() = children.isEmpty()

    companion object {
        internal const val CHAPTER_KEY_SEPARATOR = "\u001f"
    }
}
