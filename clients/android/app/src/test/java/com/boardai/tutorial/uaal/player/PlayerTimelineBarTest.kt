package com.boardai.tutorial.uaal.player

import com.boardai.tutorial.uaal.timeline.ChapterNode
import com.boardai.tutorial.uaal.timeline.TimelineCue
import org.junit.Assert.assertEquals
import org.junit.Test

class PlayerTimelineBarTest {

    @Test
    fun buildTimelineSegmentsUsesFirstCuePerChapterAndTotalDurationForLastEnd() {
        val cues = listOf(
            cue(id = "a1", start = 0f, duration = 10f, path = listOf("Chapter A")),
            cue(id = "a2", start = 10f, duration = 10f, path = listOf("Chapter A")),
            cue(id = "b1", start = 20f, duration = 15f, path = listOf("Chapter B")),
            cue(id = "c1", start = 35f, duration = 25f, path = listOf("Chapter C"))
        )

        val segments = buildTimelineSegments(cues, totalDuration = 60f)

        assertEquals(3, segments.size)
        assertEquals(listOf("Chapter A", "Chapter B", "Chapter C"), segments.map { it.title })
        assertEquals(listOf(0f, 20f, 35f), segments.map { it.start })
        assertEquals(listOf(20f, 35f, 60f), segments.map { it.end })
        assertEquals(listOf(20f, 15f, 25f), segments.map { it.duration })
        assertEquals(60f, segments.sumOf { it.duration.toDouble() }.toFloat(), 0.0001f)
        assertEquals(60f, segments.last().end, 0.0001f)
    }

    @Test
    fun buildTimelineSegmentsCollapsesMultipleCuesFromSameChapter() {
        val cues = listOf(
            cue(id = "same-1", start = 0f, duration = 4f, path = listOf("Only")),
            cue(id = "same-2", start = 4f, duration = 6f, path = listOf("Only")),
            cue(id = "other", start = 10f, duration = 5f, path = listOf("Other"))
        )

        val segments = buildTimelineSegments(cues, totalDuration = 15f)

        assertEquals(2, segments.size)
        assertEquals(listOf("Only", "Other"), segments.map { it.title })
        assertEquals(0f, segments[0].start, 0.0001f)
        assertEquals(10f, segments[0].end, 0.0001f)
        assertEquals(10f, segments[1].start, 0.0001f)
        assertEquals(15f, segments[1].end, 0.0001f)
    }

    @Test
    fun buildTimelineSegmentsIncludesEmptyAncestorTitles() {
        val cues = listOf(
            cue(
                id = "setup-1",
                start = 0f,
                duration = 10f,
                path = listOf("2 设置", "2.1 发展卡牌")
            ),
            cue(
                id = "action-root",
                start = 10f,
                duration = 1f,
                path = listOf("3 行动")
            ),
            cue(
                id = "action-1",
                start = 11f,
                duration = 9f,
                path = listOf("3 行动", "3.2 发展卡牌与贵族", "3.2.1 认识发展卡牌")
            ),
            cue(
                id = "action-2",
                start = 20f,
                duration = 5f,
                path = listOf("3 行动", "3.2 发展卡牌与贵族", "3.2.2 贵族结算")
            )
        )

        val segments = buildTimelineSegments(cues, totalDuration = 25f)

        assertEquals(
            listOf(
                "2 设置。2.1 发展卡牌",
                "3 行动",
                "3.2 发展卡牌与贵族。3.2.1 认识发展卡牌",
                "3.2 发展卡牌与贵族。3.2.2 贵族结算"
            ),
            segments.map { it.title }
        )
    }

    @Test
    fun chapterRowsPromoteChildrenOfEmptyGroupingNodes() {
        val root = ChapterNode(
            path = listOf("2 设置"),
            title = "2 设置"
        )
        val child = ChapterNode(
            path = listOf("2 设置", "2.1 发展卡牌"),
            title = "2.1 发展卡牌"
        )
        child.ownCueCount = 4
        child.firstCueIndex = 0
        child.firstCueId = "setup-1"
        root.children += child

        val rows = buildChapterRows(listOf(root), emptyMap())

        assertEquals(1, rows.size)
        assertEquals("2 设置。2.1 发展卡牌", rows.single().title)
        assertEquals(0, rows.single().depth)
        assertEquals("setup-1", rows.single().node.firstCueId)
    }

    @Test
    fun chapterPathKeyKeepsChapterSeparatorBehavior() {
        assertEquals("Root\u001fChild", chapterPathKey(listOf("Root", "Child")))
        assertEquals("", chapterPathKey(emptyList()))
    }

    private fun cue(
        id: String,
        start: Float,
        duration: Float,
        path: List<String>
    ): TimelineCue = TimelineCue(
        id = id,
        index = 0,
        start = start,
        duration = duration,
        text = "",
        groupPath = path
    )
}
