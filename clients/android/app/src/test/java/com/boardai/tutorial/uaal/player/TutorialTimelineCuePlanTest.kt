package com.boardai.tutorial.uaal.player

import com.boardai.tutorial.uaal.catalog.GameCatalogEntry
import com.boardai.tutorial.uaal.timeline.TimelineSource
import com.boardai.tutorial.uaal.timeline.TutorialTimeline
import org.json.JSONArray
import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder
import java.io.File

class TutorialTimelineCuePlanTest {

    @get:Rule
    val temp = TemporaryFolder()

    @Test
    fun planModeUsesPlanStartsDurationsTotalAndChapterTree() {
        val root = runtimeTree()
        writePlan(root)
        val logs = mutableListOf<String>()

        val timeline = TutorialTimeline.load(
            gameRoot = root,
            track = TRACK,
            planEnabled = true,
            logger = { logs += it }
        )

        assertEquals(TimelineSource.CUE_PLAN, timeline.source)
        assertTrue(timeline.usesCueTimelinePlan)
        assertEquals(5.5f, timeline.totalDuration, 1e-5f)
        assertEquals(2, timeline.cueCount)
        assertEquals(0f, timeline.cueAt(0)!!.start, 1e-5f)
        assertEquals(3.5f, timeline.cueAt(0)!!.duration, 1e-5f)
        assertEquals(3.5f, timeline.cueAt(1)!!.start, 1e-5f)
        assertEquals(2f, timeline.cueAt(1)!!.duration, 1e-5f)
        assertEquals("planned A", timeline.cueAt(0)!!.text)
        assertEquals(listOf("Planned Chapter"), timeline.cueAt(0)!!.groupPath)
        assertEquals(listOf("Planned Chapter"), timeline.rootChapters.map { it.title })
        assertEquals(0, timeline.rootChapters.single().firstCueIndex)
        assertTrue(logs.isEmpty())
    }

    @Test
    fun fullTrackPlanModeUsesSharedPlanStartDurationAndTotal() {
        val root = runtimeTree(track = "full")
        writePlan(root, track = "full")
        val timeline = TutorialTimeline.load(
            gameRoot = root,
            track = "full",
            planEnabled = true,
            logger = { }
        )

        assertEquals(TimelineSource.CUE_PLAN, timeline.source)
        assertEquals("q/a", timeline.cueAt(0)!!.id)
        assertEquals(5.5f, timeline.totalDuration, 1e-5f)
        assertEquals(3.5f, timeline.cueAt(0)!!.duration, 1e-5f)
        assertEquals(3.5f, timeline.cueAt(1)!!.start, 1e-5f)
    }

    @Test
    fun multiVisualSegmentsAreAcceptedByPlanLoader() {
        val root = runtimeTree()
        writeCompiled(root)
        writePlan(root, includeMultiVisual = true)
        val timeline = TutorialTimeline.load(root, TRACK, planEnabled = true) { }
        assertEquals(TimelineSource.CUE_PLAN, timeline.source)
        assertEquals(5.5f, timeline.totalDuration, 1e-5f)
    }

    @Test
    fun unknownVisualAnimationReferenceFallsBackWholeTrack() {
        val root = runtimeTree()
        writeCompiled(root, ids = emptyList())
        writePlan(root, includeMultiVisual = true)
        val logs = mutableListOf<String>()
        val timeline = TutorialTimeline.load(
            root, TRACK, planEnabled = true
        ) { logs += it }
        assertEquals(TimelineSource.RUNTIME, timeline.source)
        assertTrue(timeline.fallbackReason!!.contains(
            "unknown compiled animation"))
        assertTrue(logs.single().contains("falling back to whole-track runtime compatibility mode"))
    }

    @Test
    fun globalAndCueLocalMappingUsePlanTimings() {
        val root = runtimeTree()
        writePlan(root)
        val timeline = TutorialTimeline.load(root, TRACK, planEnabled = true) { }

        val middleOfSecondCue = timeline.globalToCue(4.0f)
        assertEquals(1, middleOfSecondCue.targetCue.index)
        assertEquals(0.5f, middleOfSecondCue.localSeconds, 1e-5f)
        assertEquals(4.0f, timeline.cueIndexToGlobal(1, 0.5f), 1e-5f)

        val atCueBoundary = timeline.globalToCue(3.5f)
        assertEquals(1, atCueBoundary.targetCue.index)
        assertEquals(0f, atCueBoundary.localSeconds, 1e-5f)

        val end = timeline.globalToCue(timeline.totalDuration)
        assertEquals(1, end.targetCue.index)
        assertEquals(2f, end.localSeconds, 1e-5f)
    }

    @Test
    fun missingPlanFallsBackToWholeTrackCompatibilityAndLogs() {
        val root = runtimeTree()
        val logs = mutableListOf<String>()

        val timeline = TutorialTimeline.load(
            gameRoot = root,
            track = TRACK,
            planEnabled = true,
            logger = { logs += it }
        )

        assertEquals(TimelineSource.RUNTIME, timeline.source)
        assertEquals(4f, timeline.totalDuration, 1e-5f)
        assertEquals(2f, timeline.cueAt(0)!!.duration, 1e-5f)
        assertTrue(timeline.fallbackReason!!.contains("missing cue timeline plan"))
        assertTrue(logs.single().contains("falling back to whole-track runtime compatibility mode"))
        assertTrue(logs.single().contains("missing cue timeline plan"))
    }

    @Test
    fun malformedPlanJsonFallsBackWithExplicitLog() {
        val root = runtimeTree()
        File(root, "tutorial/$TRACK.cue-timeline.json").writeText(
            "{ not-json",
            Charsets.UTF_8
        )
        val logs = mutableListOf<String>()

        val timeline = TutorialTimeline.load(
            gameRoot = root,
            track = TRACK,
            planEnabled = true,
            logger = { logs += it }
        )

        assertEquals(TimelineSource.RUNTIME, timeline.source)
        assertTrue(timeline.fallbackReason!!.contains("invalid cue timeline plan JSON"))
        assertTrue(logs.single().contains("invalid cue timeline plan JSON"))
        assertEquals(4f, timeline.totalDuration, 1e-5f)
    }

    @Test
    fun schemaAndVersionMismatchFallBackExplicitly() {
        val root = runtimeTree()
        writePlan(root, schema = "tutorial-cue-plan-set/v999")
        val logs = mutableListOf<String>()

        val timeline = TutorialTimeline.load(root, TRACK, planEnabled = true) { logs += it }
        assertEquals(TimelineSource.RUNTIME, timeline.source)
        assertTrue(timeline.fallbackReason!!.contains("schema mismatch"))
        assertTrue(logs.single().contains("schema mismatch"))

        writePlan(root, schemaVersion = 2)
        val versionLogs = mutableListOf<String>()
        val versionFallback = TutorialTimeline.load(
            root, TRACK, planEnabled = true
        ) { versionLogs += it }
        assertEquals(TimelineSource.RUNTIME, versionFallback.source)
        assertTrue(versionFallback.fallbackReason!!.contains("version mismatch"))
        assertTrue(versionLogs.single().contains("version mismatch"))
    }

    @Test
    fun mixedCueCoverageIsRejectedAsWholeTrack() {
        val root = runtimeTree()
        writePlan(root, omitSecondCue = true)
        val logs = mutableListOf<String>()

        val timeline = TutorialTimeline.load(
            gameRoot = root,
            track = TRACK,
            planEnabled = true,
            logger = { logs += it }
        )

        assertEquals(TimelineSource.RUNTIME, timeline.source)
        assertEquals(4f, timeline.totalDuration, 1e-5f)
        assertTrue(timeline.fallbackReason!!.contains("coverage mismatch"))
        assertTrue(logs.single().contains("coverage mismatch"))
    }

    @Test
    fun missingPlanAudioFallsBackEntireTrackAndLogs() {
        val root = runtimeTree()
        writePlan(root)
        File(root, "media/fixture.mp3").delete()
        val logs = mutableListOf<String>()

        val timeline = TutorialTimeline.load(
            gameRoot = root,
            track = TRACK,
            planEnabled = true,
            logger = { logs += it }
        )

        assertEquals(TimelineSource.RUNTIME, timeline.source)
        assertEquals(4f, timeline.totalDuration, 1e-5f)
        assertEquals("runtime A", timeline.cueAt(0)!!.text)
        assertTrue(timeline.fallbackReason!!.contains("missing audio file"))
        assertTrue(logs.single().contains("falling back to whole-track runtime compatibility mode"))
        assertTrue(logs.single().contains("missing audio file"))
    }

    @Test
    fun trackSwitchOffIgnoresPlanAndKeepsLegacyBehavior() {
        val root = runtimeTree()
        writePlan(root)
        val logs = mutableListOf<String>()

        val timeline = TutorialTimeline.load(
            gameRoot = root,
            track = TRACK,
            planEnabled = false,
            logger = { logs += it }
        )

        assertEquals(TimelineSource.RUNTIME, timeline.source)
        assertFalse(timeline.usesCueTimelinePlan)
        assertEquals(4f, timeline.totalDuration, 1e-5f)
        assertEquals(2f, timeline.cueAt(0)!!.duration, 1e-5f)
        assertEquals("runtime A", timeline.cueAt(0)!!.text)
        assertTrue(logs.isEmpty())
    }

    @Test
    fun catalogSwitchSourceIsParsedAndFilteredToTutorialTracks() {
        val entry = GameCatalogEntry.fromJson(JSONObject()
            .put("id", "fixture")
            .put("tutorial_tracks", JSONArray(listOf("quick", "full")))
            .put("cue_timeline_tracks", JSONArray(listOf("quick", "unknown", "quick")))
        )
        assertNotNull(entry)
        assertEquals(listOf("quick"), entry!!.cueTimelineTracks)
    }

    private fun runtimeTree(track: String = TRACK): File {
        val root = temp.newFolder("game-root")
        val tutorial = File(root, "tutorial").apply { mkdirs() }
        File(root, "media").mkdirs()
        File(root, "media/fixture.mp3").writeBytes(byteArrayOf(1, 2, 3))
        File(tutorial, "$track.runtime.json").writeText(
            """
            {
              "schema_version": 3,
              "game_id": "fixture",
              "track": "$track",
              "cues": [
                {
                  "id": "q/a",
                  "start": 0.0,
                  "duration": 2.0,
                  "text": "runtime A",
                  "group_path": ["Runtime Chapter"],
                  "refs": []
                },
                {
                  "id": "q/b",
                  "start": 2.0,
                  "duration": 2.0,
                  "text": "runtime B",
                  "group_path": ["Runtime Chapter"],
                  "refs": []
                }
              ]
            }
            """.trimIndent(),
            Charsets.UTF_8
        )
        return root
    }

    private fun writePlan(
        root: File,
        track: String = TRACK,
        schema: String = "tutorial-cue-plan-set/v1",
        schemaVersion: Int = 1,
        omitSecondCue: Boolean = false,
        includeMultiVisual: Boolean = false
    ) {
        val cues = JSONArray()
        cues.put(planCue(
            "q/a", 0.0, 3.5, "planned A", "Planned Chapter",
            includeMultiVisual = includeMultiVisual))
        if (!omitSecondCue) {
            cues.put(planCue("q/b", 3.5, 2.0, "planned B", "Planned Chapter"))
        }
        val document = JSONObject()
            .put("schema", schema)
            .put("schema_version", schemaVersion)
            .put("game_id", "fixture")
            .put("track", track)
            .put("course", JSONObject().put("start", 0.0).put("duration", 5.5))
            .put("cues", cues)
        File(root, "tutorial/$track.cue-timeline.json").writeText(
            document.toString(2),
            Charsets.UTF_8
        )
    }

    private fun writeCompiled(
        root: File,
        ids: List<String> = listOf("q/a", "q/b")
    ) {
        val cues = JSONArray()
        for (id in ids) {
            cues.put(JSONObject().put("id", id).put("duration", 3.5))
        }
        val document = JSONObject()
            .put("schema", "tutorial-compiled-animation/v2")
            .put("game", "fixture")
            .put("track", TRACK)
            .put("cues", cues)
        File(root, "tutorial/anim/v2").mkdirs()
        File(root, "tutorial/anim/v2/$TRACK.compiled.json").writeText(
            document.toString(2), Charsets.UTF_8)
    }

    private fun planCue(
        id: String,
        start: Double,
        duration: Double,
        text: String,
        chapter: String,
        includeMultiVisual: Boolean = false
    ): JSONObject {
        val tracks = JSONArray()
            .put(JSONObject()
                .put("id", "audio.main")
                .put("kind", "audio")
                .put("tail_policy", "silence")
                .put("segments", JSONArray(listOf(
                    JSONObject()
                        .put("id", "audio.clip")
                        .put("type", "audio_clip")
                        .put("asset", "media/fixture.mp3")
                        .put("cue_start", 0.0)
                        .put("source_start", 0.0)
                        .put("source_end", duration)
                ))))
            .put(JSONObject()
                .put("id", "subtitle.main")
                .put("kind", "subtitle")
                .put("tail_policy", "clear")
                .put("segments", JSONArray(listOf(
                    JSONObject()
                        .put("id", "subtitle.0")
                        .put("type", "subtitle")
                        .put("cue_start", 0.0)
                        .put("duration", duration)
                        .put("text", text)
                ))))
        if (includeMultiVisual) {
            tracks.put(JSONObject()
                .put("id", "visual.main")
                .put("kind", "visual")
                .put("tail_policy", "hold_last_frame")
                .put("segments", JSONArray(listOf(
                    JSONObject()
                        .put("id", "visual.first")
                        .put("type", "animation")
                        .put("animation", id)
                        .put("cue_start", 0.0)
                        .put("source_start", 0.0)
                        .put("source_end", 1.0)
                        .put("after_end", "hold_last_frame"),
                    JSONObject()
                        .put("id", "visual.second")
                        .put("type", "animation")
                        .put("animation", id)
                        .put("cue_start", 1.0)
                        .put("source_start", 1.0)
                        .put("source_end", duration)
                        .put("after_end", "hold_last_frame")
                )))
            )
        }
        return JSONObject()
            .put("schema", "tutorial-cue-plan/v1")
            .put("cue_id", id)
            .put("entry", JSONObject.NULL)
            .put("course", JSONObject().put("start", start).put("duration", duration))
            .put("text", text)
            .put("group_path", JSONArray(listOf(chapter)))
            .put("refs", JSONArray(listOf("ref")))
            .put("subtitles", JSONArray())
            .put("tracks", tracks)
    }

    private companion object {
        const val TRACK = "quick"
    }
}
