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
        private const val MAX_ACTIONS_PER_CUE = 8

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
                    groupPath = item.optJSONArray("group_path").toStringList(),
                    refs = item.optJSONArray("refs").toStringList()
                )
            }

            val zoneBindings = loadZoneBindings(gameRoot, track)
            val actionsByCue = loadActionsByCue(gameRoot, track, zoneBindings)
            val sorted = parsed
                .sortedWith(compareBy<TimelineCue> { it.start }.thenBy { it.index })
                .mapIndexed { index, cue ->
                    cue.copy(index = index, actions = actionsByCue[cue.id].orEmpty())
                }

            val total = sorted.maxOfOrNull { it.start + it.duration } ?: 0f
            val chapters = buildChapterTree(sorted)

            return TutorialTimeline(
                cues = sorted,
                totalDuration = total,
                rootChapters = chapters
            )
        }

        private val ACTION_OP_PRIORITY = listOf(
            "transfer",
            "create",
            "destroy",
            "move_order",
            "stack",
            "set_face",
            "shuffle"
        )

        /**
         * Reads the compiled track's top-level `zone_bindings` map.  Missing or
         * malformed compiled data never blocks the timeline / QA path; callers
         * simply fall back to raw physical zone ids.
         */
        private fun loadZoneBindings(gameRoot: File, track: String): Map<String, ZoneBinding> {
            val compiledFile = File(gameRoot, "tutorial/anim/v2/$track.compiled.json")
            if (!compiledFile.isFile) return emptyMap()

            return try {
                val root = JSONObject(compiledFile.readText(Charsets.UTF_8))
                val raw = root.optJSONObject("zone_bindings") ?: return emptyMap()
                val result = LinkedHashMap<String, ZoneBinding>()
                val keys = raw.keys()
                while (keys.hasNext()) {
                    val zoneId = keys.next().trim()
                    if (zoneId.isEmpty()) continue
                    val binding = raw.optJSONObject(zoneId) ?: continue
                    result[zoneId] = ZoneBinding(
                        logicalZone = binding.optString("logical_zone", "").trim(),
                        label = binding.optString("label", "").trim(),
                        parts = binding.optJSONArray("parts").toPartRefs(),
                        qaIgnore = binding.optBoolean("qa_ignore", false)
                    )
                }
                result
            } catch (_: Exception) {
                emptyMap()
            }
        }

        /**
         * Reads the hand-written v2 animation source and keeps only a short,
         * deterministic state-action summary per cue.  Missing or malformed
         * anim data never blocks the timeline / QA path.
         *
         * Action summaries are emitted in priority order and multi-source
         * transfers expand to one line per source.
         */
        private fun loadActionsByCue(
            gameRoot: File,
            track: String,
            zoneBindings: Map<String, ZoneBinding>
        ): Map<String, List<String>> {
            val animFile = File(gameRoot, "tutorial/anim/v2/$track.anim.json")
            if (!animFile.isFile) return emptyMap()

            return try {
                val root = JSONObject(animFile.readText(Charsets.UTF_8))
                val cueArray = root.optJSONArray("cues") ?: return emptyMap()
                val result = LinkedHashMap<String, List<String>>()

                for (i in 0 until cueArray.length()) {
                    val cue = cueArray.optJSONObject(i) ?: continue
                    val cueId = cue.optString("id", "").trim()
                    if (cueId.isEmpty()) continue

                    val events = cue.optJSONArray("events") ?: continue
                    val actions = ArrayList<String>(MAX_ACTIONS_PER_CUE)
                    collectActionSummaries(events, zoneBindings, actions)
                    if (actions.isNotEmpty()) result[cueId] = actions
                }

                result
            } catch (_: Exception) {
                emptyMap()
            }
        }

        private fun collectActionSummaries(
            events: JSONArray,
            zoneBindings: Map<String, ZoneBinding>,
            actions: MutableList<String>
        ) {
            for (op in ACTION_OP_PRIORITY) {
                for (i in 0 until events.length()) {
                    if (actions.size >= MAX_ACTIONS_PER_CUE) return
                    val event = events.optJSONObject(i) ?: continue
                    if (event.optString("op", "").trim() != op) continue
                    actions.addAll(summarizeStateAction(event, zoneBindings))
                }
            }
        }

        private fun summarizeStateAction(
            event: JSONObject,
            zoneBindings: Map<String, ZoneBinding>
        ): List<String> {
            if (qaZoneRefs(event).any { isQaIgnored(it, zoneBindings) }) {
                return emptyList()
            }

            return when (event.optString("op", "").trim()) {
                "transfer" -> transferActions(event, zoneBindings)

                "create" -> {
                    val zone = event.optString("zone", "").trim()
                    if (zone.isEmpty()) emptyList()
                    else {
                        val count = event.eventCount(default = 1)
                        val label = event.actionLabel().ifBlank {
                            event.inferredLabelFromZones(zone).ifBlank { "物件" }
                        }
                        listOf("生成 ${countedLabel(count, label)} 到 ${zoneName(zone, zoneBindings)}")
                    }
                }

                "destroy" -> {
                    val zone = event.optString("zone", "").trim()
                    if (zone.isEmpty()) emptyList()
                    else {
                        val count = event.eventCount(default = 1)
                        val label = event.actionLabel().ifBlank {
                            event.inferredLabelFromZones(zone).ifBlank { "物件" }
                        }
                        listOf("从 ${zoneName(zone, zoneBindings)} 移除 ${countedLabel(count, label)}")
                    }
                }

                "move_order" -> {
                    val zone = event.optString("zone", "").trim()
                    if (zone.isEmpty()) emptyList()
                    else {
                        val label = event.actionLabel().ifBlank { "物件" }
                        listOf("调整 ${zoneName(zone, zoneBindings)} 中 $label 的 order")
                    }
                }

                "stack" -> {
                    val destination = event.optString("destination", "").trim()
                        .ifBlank { event.optString("zone", "").trim() }
                    if (destination.isEmpty()) emptyList()
                    else {
                        val count = event.eventCount(
                            default = event.optInt("capacity", 1),
                            quantityFirst = false
                        )
                        val label = event.actionLabel().ifBlank {
                            event.inferredLabelFromZones(destination).ifBlank { "牌" }
                        }
                        listOf("在 ${zoneName(destination, zoneBindings)} 堆叠 ${countedLabel(count, label)}")
                    }
                }

                "set_face" -> {
                    val zone = event.optString("zone", "").trim()
                    if (zone.isEmpty()) emptyList()
                    else {
                        val label = event.actionLabel().ifBlank {
                            event.inferredLabelFromZones(zone).ifBlank { "物件" }
                        }
                        val to = event.optString("to", "").trim().ifBlank { "?" }
                        listOf("将 ${zoneName(zone, zoneBindings)} 中 $label 翻到 $to")
                    }
                }

                "shuffle" -> {
                    val zone = event.optString("zone", "").trim()
                    if (zone.isEmpty()) emptyList()
                    else listOf("洗混 ${zoneName(zone, zoneBindings)}")
                }

                // `ensure` is authoritative state plumbing, not a player-facing
                // action; omit it so it does not consume the 8 action slots.
                else -> emptyList()
            }
        }

        /**
         * A multi-source transfer means one transfer from each source.  Keep
         * one summary per source instead of collapsing the list into a single
         * misleading line.
         */
        private fun transferActions(
            event: JSONObject,
            zoneBindings: Map<String, ZoneBinding>
        ): List<String> {
            val destination = event.optString("destination", "").trim()
            val sources = event.sourceZones()
            if (destination.isEmpty() || sources.isEmpty()) return emptyList()

            val count = event.eventCount(default = 1, quantityFirst = true)
            val explicitLabel = event.actionLabel()
            return sources.map { source ->
                val label = explicitLabel.ifBlank {
                    event.inferredLabelFromZones(source, destination).ifBlank { "物件" }
                }
                "${countedLabel(count, label)}：${zoneName(source, zoneBindings)} -> " +
                    zoneName(destination, zoneBindings)
            }
        }

        private fun JSONObject.eventCount(default: Int, quantityFirst: Boolean = false): Int {
            val raw = when {
                quantityFirst && has("quantity") -> optInt("quantity", default)
                has("count") -> optInt("count", default)
                has("quantity") -> optInt("quantity", default)
                else -> default
            }
            return raw.coerceAtLeast(0)
        }

        /**
         * Label preference follows the QA spec: concept, then template, then
         * palette.  If both template and palette exist, keep both so a generic
         * concept (gem) does not lose its concrete kind (gem|gem_diamond).
         */
        private fun JSONObject.actionLabel(): String {
            val concept = optString("concept", "").trim()
            if (concept.isNotEmpty()) return concept

            val template = optString("template", "").trim()
            val palette = optString("palette", "").trim()
            return when {
                template.isNotEmpty() && palette.isNotEmpty() -> "$template|$palette"
                template.isNotEmpty() -> template
                palette.isNotEmpty() -> palette
                else -> ""
            }
        }

        private fun JSONObject.sourceZones(): List<String> {
            val raw = opt("source") ?: return emptyList()
            return when (raw) {
                is JSONArray -> buildList {
                    for (i in 0 until raw.length()) {
                        raw.optString(i, "").trim().takeIf { it.isNotEmpty() }?.let(::add)
                    }
                }

                is String -> listOfNotNull(raw.trim().takeIf { it.isNotEmpty() })
                else -> emptyList()
            }
        }

        private fun JSONObject.inferredLabelFromZones(vararg zones: String): String {
            val joined = zones.joinToString(" ")
            return when {
                joined.contains("gold") -> "黄金"
                joined.contains("gem") -> "宝石"
                joined.contains("noble") -> "贵族"
                joined.contains("card") || joined.contains("deck") -> "发展卡"
                else -> "物件"
            }
        }

        /**
         * Turn a raw physical zone id into its logical label.
         *
         * Priority is compiled `label`, then `logical_zone`, then the original
         * id.  The short functions below exist so every zone reference in the
         * summary path goes through this one fallback chain.
         */
        private fun zoneName(raw: String, bindings: Map<String, ZoneBinding>): String {
            val zone = raw.trim()
            if (zone.isEmpty()) return ""
            val binding = bindings[zone] ?: return zone
            return binding.label.ifBlank { binding.logicalZone.ifBlank { zone } }
        }

        /**
         * True when the compiled binding marks this physical zone as
         * animation-only and therefore excluded from QA action summaries.
         */
        private fun isQaIgnored(zone: String, bindings: Map<String, ZoneBinding>): Boolean {
            return bindings[zone.trim()]?.qaIgnore == true
        }

        /**
         * Physical zones that make this state action visible to QA.  Unlike
         * [zoneName], no label fallback is applied: unknown zones use raw ids,
         * while known `qa_ignore` zones suppress the whole action line.
         */
        private fun qaZoneRefs(event: JSONObject): List<String> {
            return when (event.optString("op", "").trim()) {
                "create", "destroy", "move_order", "set_face", "shuffle" ->
                    listOf(event.optString("zone", "").trim())

                "transfer" -> buildList {
                    val destination = event.optString("destination", "").trim()
                    if (destination.isNotEmpty()) add(destination)
                    addAll(event.sourceZones())
                }

                "stack" -> listOf(event.optString("destination", "").trim())
                else -> emptyList()
            }
        }

        private fun countedLabel(count: Int, label: String): String {
            val safeCount = count.coerceAtLeast(0)
            val unit = when {
                label.contains("宝石") -> "颗"
                label.contains("发展卡") || label.contains("牌") -> "张"
                label.contains("贵族") || label.contains("标记") -> "枚"
                else -> "个"
            }
            return "$safeCount $unit$label"
        }

        private fun JSONArray?.toPartRefs(): List<PartRef> {
            if (this == null) return emptyList()
            val result = ArrayList<PartRef>(length())
            for (i in 0 until length()) {
                val item = optJSONObject(i) ?: continue
                val key = item.optString("key", "").trim()
                if (key.isEmpty()) continue
                result += PartRef(key, item.optString("value", ""))
            }
            return result
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
