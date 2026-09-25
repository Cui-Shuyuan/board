package com.boardai.tutorial.uaal.timeline

import org.json.JSONArray
import org.json.JSONObject
import java.io.File
import kotlin.math.abs
import kotlin.math.max

/**
 * Native model of `tutorial/{track}.runtime.json`.
 *
 * The runtime JSON already contains global cue start times, so the native layer
 * can expose a true video-style continuous timeline:
 *
 *   global seconds <-> (cue index, cue-local seconds)
 *
 * This is intentionally independent from Unity's playback clock.  Unity remains
 * the source of truth for actual playback; this model is used for seeking,
 * preview text and the chapter tree.
 */
class TutorialTimeline private constructor(
    val cues: List<TimelineCue>,
    val totalDuration: Float,
    val rootChapters: List<ChapterNode>
) {
    val cueCount: Int get() = cues.size

    val allChapters: List<ChapterNode> = buildList {
        fun visit(nodes: List<ChapterNode>) {
            for (node in nodes) {
                add(node)
                visit(node.children)
            }
        }
        visit(rootChapters)
    }

    private val chapterByKey: Map<String, ChapterNode> =
        allChapters.associateBy { it.key }

    fun cueAt(index: Int): TimelineCue? = cues.getOrNull(index)

    fun cueById(cueId: String): TimelineCue? {
        if (cueId.isBlank()) return null
        return cues.firstOrNull { it.id == cueId }
    }

    fun chapterForCue(cueIndex: Int): ChapterNode? {
        val cue = cueAt(cueIndex) ?: return null
        return chapterByKey[pathKey(cue.groupPath)]
    }

    /**
     * Converts a cue-local position to the tutorial's global timeline.
     */
    fun cueIndexToGlobal(cueIndex: Int, localSeconds: Float): Float {
        if (cues.isEmpty()) return 0f
        val index = cueIndex.coerceIn(0, cues.lastIndex)
        val cue = cues[index]
        return cue.start + localSeconds.coerceIn(0f, cue.duration)
    }

    /**
     * Converts a global time to a cue-local position and applies the player's
     * magnetic snap rules.
     *
     * Magnetic priority:
     *   1. nearest chapter first-cue start within 0.5 s;
     *   2. nearest cue start within 0.3 s;
     *   3. otherwise continuous seconds inside the containing cue.
     */
    fun globalToCue(
        globalSeconds: Float,
        snapToCueStart: Boolean = true,
        snapToChapterStart: Boolean = true
    ): TimelineTarget {
        require(cues.isNotEmpty()) { "timeline has no cues" }

        val global = globalSeconds.coerceIn(0f, max(0f, totalDuration))
        val raw = cues[cueIndexForGlobal(global)]
        var targetCue = raw
        var local = (global - raw.start).coerceIn(0f, raw.duration)
        var snappedCue = false
        var snappedChapter = false
        var snappedChapterNode: ChapterNode? = null

        if (snapToChapterStart) {
            val chapter = nearestChapterStart(global)
            if (chapter != null) {
                val chapterStartCue = cues.getOrNull(chapter.firstCueIndex)
                if (chapterStartCue != null &&
                    abs(global - chapterStartCue.start) <= CHAPTER_START_SNAP_SECONDS
                ) {
                    targetCue = chapterStartCue
                    local = 0f
                    snappedCue = true
                    snappedChapter = true
                    snappedChapterNode = chapter
                }
            }
        }

        if (!snappedChapter && snapToCueStart) {
            val nearest = nearestCueStart(global)
            if (nearest != null && abs(global - nearest.start) <= CUE_START_SNAP_SECONDS) {
                targetCue = nearest
                local = 0f
                snappedCue = true
            }
        }

        return TimelineTarget(
            targetCue = targetCue,
            localSeconds = local,
            snappedToCueStart = snappedCue,
            snappedToChapterStart = snappedChapter,
            snappedChapter = snappedChapterNode
        )
    }

    private fun cueIndexForGlobal(global: Float): Int {
        var low = 0
        var high = cues.lastIndex
        while (low < high) {
            val mid = (low + high + 1) ushr 1
            if (cues[mid].start <= global) low = mid else high = mid - 1
        }
        return low
    }

    private fun nearestCueStart(global: Float): TimelineCue? {
        var best: TimelineCue? = null
        var bestDistance = Float.MAX_VALUE
        for (cue in cues) {
            val distance = abs(global - cue.start)
            if (distance <= CUE_START_SNAP_SECONDS && distance < bestDistance) {
                best = cue
                bestDistance = distance
            }
        }
        return best
    }

    private fun nearestChapterStart(global: Float): ChapterNode? {
        var best: ChapterNode? = null
        var bestDistance = Float.MAX_VALUE
        for (chapter in allChapters) {
            val firstCue = cues.getOrNull(chapter.firstCueIndex) ?: continue
            val distance = abs(global - firstCue.start)
            if (distance <= CHAPTER_START_SNAP_SECONDS &&
                (distance < bestDistance ||
                    (distance == bestDistance && chapter.depth > (best?.depth ?: 0)))
            ) {
                best = chapter
                bestDistance = distance
            }
        }
        return best
    }

    companion object {
        const val CUE_START_SNAP_SECONDS = 0.3f
        const val CHAPTER_START_SNAP_SECONDS = 0.5f

        fun load(gameRoot: File, track: String = "full"): TutorialTimeline {
            val runtimeFile = File(gameRoot, "tutorial/$track.runtime.json")
            require(runtimeFile.isFile) {
                "missing runtime file: ${runtimeFile.absolutePath}"
            }

            val root = JSONObject(runtimeFile.readText(Charsets.UTF_8))
            val cueArray = root.optJSONArray("cues") ?: JSONArray()

            val parsed = ArrayList<TimelineCue>(cueArray.length())
            for (i in 0 until cueArray.length()) {
                val item = cueArray.optJSONObject(i) ?: continue
                val id = item.optString("id", "")
                if (id.isBlank()) continue

                parsed += TimelineCue(
                    id = id,
                    index = i,
                    start = item.optDouble("start", 0.0).toFloat().coerceAtLeast(0f),
                    duration = item.optDouble("duration", 0.0).toFloat().coerceAtLeast(0f),
                    text = item.optString("text", ""),
                    groupPath = item.optJSONArray("group_path").toStringList()
                )
            }

            val sorted = parsed
                .sortedWith(compareBy<TimelineCue> { it.start }.thenBy { it.index })
                .mapIndexed { index, cue -> cue.copy(index = index) }

            val total = sorted.maxOfOrNull { it.start + it.duration } ?: 0f
            val chapters = buildChapterTree(sorted)

            return TutorialTimeline(
                cues = sorted,
                totalDuration = total,
                rootChapters = chapters
            )
        }

        private fun buildChapterTree(cues: List<TimelineCue>): List<ChapterNode> {
            val roots = mutableListOf<ChapterNode>()

            for (cue in cues) {
                var siblings = roots
                val prefix = ArrayList<String>(cue.groupPath.size)

                for (title in cue.groupPath) {
                    if (title.isBlank()) continue
                    prefix += title
                    val node = siblings.firstOrNull { it.title == title }
                        ?: ChapterNode(path = prefix.toList(), title = title).also {
                            siblings += it
                        }
                    if (node.firstCueIndex < 0) {
                        node.firstCueIndex = cue.index
                        node.firstCueId = cue.id
                    }
                    siblings = node.children
                }
            }

            return roots
        }

        private fun JSONArray?.toStringList(): List<String> {
            if (this == null) return emptyList()
            val result = ArrayList<String>(length())
            for (i in 0 until length()) {
                val value = optString(i, "")
                if (value.isNotBlank()) result += value
            }
            return result
        }

        private fun pathKey(path: List<String>): String =
            path.joinToString(ChapterNode.CHAPTER_KEY_SEPARATOR)
    }
}
