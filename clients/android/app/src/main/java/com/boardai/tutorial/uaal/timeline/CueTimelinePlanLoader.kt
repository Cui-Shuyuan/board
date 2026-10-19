package com.boardai.tutorial.uaal.timeline

import org.json.JSONArray
import org.json.JSONObject
import java.io.File
import kotlin.math.abs

/**
 * One validated cue timing entry from `tutorial/{track}.cue-timeline.json`.
 *
 * [groupPath], [text] and [refs] are presentation/navigation metadata carried
 * in the plan so the native chapter tree does not need legacy timing fields.
 */
internal data class CuePlanEntry(
    val id: String,
    val start: Float,
    val duration: Float,
    val text: String,
    val groupPath: List<String>,
    val refs: List<String>
)

internal data class CuePlanSet(
    val cues: List<CuePlanEntry>,
    val totalDuration: Float
)

/**
 * Loads and strictly validates the shared Phase 4 plan set.
 *
 * The loader is intentionally all-or-nothing.  A plan with a missing cue, an
 * extra cue, out-of-order course starts or an unsupported schema/version is
 * rejected as a whole; the caller then falls back to the legacy runtime for
 * the entire track.
 */
internal object CueTimelinePlanLoader {
    const val SCHEMA = "tutorial-cue-plan-set/v1"
    const val SCHEMA_VERSION = 1
    private const val EPSILON = 1e-4f

    fun planFile(gameRoot: File, track: String): File =
        File(gameRoot, "tutorial/$track.cue-timeline.json")

    fun load(
        gameRoot: File,
        track: String,
        runtimeCueIds: List<String>
    ): CuePlanSet {
        val file = planFile(gameRoot, track)
        require(file.isFile) { "missing cue timeline plan: ${file.absolutePath}" }

        val root = try {
            JSONObject(file.readText(Charsets.UTF_8))
        } catch (error: Exception) {
            throw IllegalArgumentException(
                "invalid cue timeline plan JSON at ${file.absolutePath}: ${error.message}",
                error
            )
        }

        val schema = root.optString("schema", "")
        require(schema == SCHEMA) {
            "cue timeline plan schema mismatch: expected '$SCHEMA', got '$schema'"
        }
        val schemaVersion = root.optInt("schema_version", -1)
        require(schemaVersion == SCHEMA_VERSION) {
            "cue timeline plan version mismatch: expected $SCHEMA_VERSION, got $schemaVersion"
        }

        val planTrack = root.optString("track", "").trim()
        require(planTrack == track) {
            "cue timeline plan track mismatch: expected '$track', got '$planTrack'"
        }

        val course = root.optJSONObject("course")
            ?: throw IllegalArgumentException("cue timeline plan is missing course")
        val courseStart = finiteNonNegative(course, "start", "plan.course.start")
        require(courseStart <= EPSILON) {
            "cue timeline plan course.start must be 0 for the track timeline"
        }
        val totalDuration = finitePositive(course, "duration", "plan.course.duration")

        val cueArray = root.optJSONArray("cues")
            ?: throw IllegalArgumentException("cue timeline plan is missing cues")
        require(cueArray.length() > 0) { "cue timeline plan has no cues" }
        require(cueArray.length() == runtimeCueIds.size) {
            "cue timeline plan cue coverage mismatch: runtime=${runtimeCueIds.size}, " +
                "plan=${cueArray.length()}"
        }

        val animationDurations = loadCompiledAnimationDurations(gameRoot, track)
        val cues = ArrayList<CuePlanEntry>(cueArray.length())
        var previousEnd: Float? = null
        for (index in 0 until cueArray.length()) {
            val cue = cueArray.optJSONObject(index)
                ?: throw IllegalArgumentException("cue timeline plan cues[$index] is not an object")
            val path = "plan.cues[$index]"
            val id = cue.optString("cue_id", "").trim()
            require(id.isNotBlank()) { "$path.cue_id must be non-empty" }
            require(id == runtimeCueIds[index]) {
                "cue timeline plan cue order/coverage mismatch at $index: " +
                    "runtime='${runtimeCueIds[index]}', plan='$id'"
            }
            require(cue.has("entry")) { "$path.entry is missing" }

            val cueCourse = cue.optJSONObject("course")
                ?: throw IllegalArgumentException("$path.course is missing")
            val start = finiteNonNegative(cueCourse, "start", "$path.course.start")
            val duration = finitePositive(cueCourse, "duration", "$path.course.duration")
            if (previousEnd != null) {
                require(start >= previousEnd - EPSILON) {
                    "$path.course.start overlaps the previous cue"
                }
            }
            previousEnd = start + duration

            validateTracks(
                cue.optJSONArray("tracks"), duration, path, gameRoot,
                animationDurations
            )

            cues += CuePlanEntry(
                id = id,
                start = start,
                duration = duration,
                text = cue.optString("text", ""),
                groupPath = cue.optJSONArray("group_path").toStringList(),
                refs = cue.optJSONArray("refs").toStringList()
            )
        }

        val expectedTotal = previousEnd ?: 0f
        require(abs(totalDuration - expectedTotal) <= EPSILON) {
            "cue timeline plan course.duration mismatch: " +
                "declared=$totalDuration, cueEnd=$expectedTotal"
        }

        return CuePlanSet(cues = cues, totalDuration = totalDuration)
    }

    private fun validateTracks(
        tracks: JSONArray?,
        cueDuration: Float,
        path: String,
        gameRoot: File,
        animationDurations: Map<String, Float>
    ) {
        require(tracks != null && tracks.length() > 0) { "$path.tracks must not be empty" }
        val trackIds = HashSet<String>()
        val segmentIds = HashSet<String>()

        for (trackIndex in 0 until tracks.length()) {
            val track = tracks.optJSONObject(trackIndex)
                ?: throw IllegalArgumentException("$path.tracks[$trackIndex] is not an object")
            val trackPath = "$path.tracks[$trackIndex]"
            val trackId = track.optString("id", "").trim()
            require(trackId.isNotBlank()) { "$trackPath.id must be non-empty" }
            require(trackIds.add(trackId)) { "$trackPath.id duplicates '$trackId'" }

            val kind = track.optString("kind", "").trim()
            require(kind == "audio" || kind == "visual" || kind == "subtitle") {
                "$trackPath.kind must be audio, visual or subtitle"
            }
            val tailPolicy = track.optString("tail_policy", "").trim()
            val allowedTails = when (kind) {
                "audio" -> setOf("silence", "stop")
                "visual" -> setOf("hold_last_frame", "clear")
                else -> setOf("clear")
            }
            require(tailPolicy in allowedTails) {
                "$trackPath.tail_policy is invalid for kind '$kind'"
            }

            val segments = track.optJSONArray("segments")
                ?: throw IllegalArgumentException("$trackPath.segments is missing")
            val intervals = ArrayList<Pair<Float, Float>>(segments.length())
            for (segmentIndex in 0 until segments.length()) {
                val segment = segments.optJSONObject(segmentIndex)
                    ?: throw IllegalArgumentException(
                        "$trackPath.segments[$segmentIndex] is not an object"
                    )
                val segmentPath = "$trackPath.segments[$segmentIndex]"
                val segmentId = segment.optString("id", "").trim()
                require(segmentId.isNotBlank()) { "$segmentPath.id must be non-empty" }
                require(segmentIds.add(segmentId)) {
                    "$segmentPath.id duplicates '$segmentId'"
                }

                val type = segment.optString("type", "").trim()
                val cueStart = finiteNonNegative(
                    segment,
                    "cue_start",
                    "$segmentPath.cue_start"
                )
                val cueEnd = when {
                    kind == "visual" && type == "state" -> {
                        val animation = segment.optString("animation", "").trim()
                        require(animation.isNotBlank()) {
                            "$segmentPath.animation must be non-empty"
                        }
                        require(animationDurations[animation] != null) {
                            "$segmentPath.animation references unknown compiled animation: " +
                                animation
                        }
                        require(segment.optString("after_end", "") in
                            setOf("hold_last_frame", "clear")) {
                            "$segmentPath.after_end is invalid"
                        }
                        cueStart + finitePositive(
                            segment,
                            "duration",
                            "$segmentPath.duration"
                        )
                    }

                    kind == "visual" && type == "animation" -> {
                        val animation = segment.optString("animation", "").trim()
                        require(animation.isNotBlank()) {
                            "$segmentPath.animation must be non-empty"
                        }
                        val compiledDuration = animationDurations[animation]
                        require(compiledDuration != null) {
                            "$segmentPath.animation references unknown compiled animation: " +
                                animation
                        }
                        require(segment.optString("after_end", "") in
                            setOf("hold_last_frame", "clear")) {
                            "$segmentPath.after_end is invalid"
                        }
                        val sourceStart = finiteNonNegative(
                            segment,
                            "source_start",
                            "$segmentPath.source_start"
                        )
                        val sourceEnd = finitePositive(
                            segment,
                            "source_end",
                            "$segmentPath.source_end"
                        )
                        require(sourceEnd > sourceStart) {
                            "$segmentPath.source_end must be greater than source_start"
                        }
                        require(sourceEnd <= compiledDuration + EPSILON) {
                            "$segmentPath.source_end exceeds compiled animation duration"
                        }
                        cueStart + (sourceEnd - sourceStart)
                    }

                    kind == "visual" && type == "hold" -> {
                        cueStart + finitePositive(
                            segment,
                            "duration",
                            "$segmentPath.duration"
                        )
                    }

                    kind == "audio" && type == "audio_clip" -> {
                        val asset = segment.optString("asset", "").trim()
                        require(asset.isNotBlank()) {
                            "$segmentPath.asset must be non-empty"
                        }
                        require(!File(asset).isAbsolute) {
                            "$segmentPath.asset must be game-relative"
                        }
                        require(File(gameRoot, asset).isFile) {
                            "$segmentPath.asset references missing audio file: $asset"
                        }
                        val sourceStart = finiteNonNegative(
                            segment,
                            "source_start",
                            "$segmentPath.source_start"
                        )
                        val sourceEnd = finitePositive(
                            segment,
                            "source_end",
                            "$segmentPath.source_end"
                        )
                        require(sourceEnd > sourceStart) {
                            "$segmentPath.source_end must be greater than source_start"
                        }
                        val hasStartSample = segment.has("source_start_sample")
                        val hasEndSample = segment.has("source_end_sample")
                        require(hasStartSample == hasEndSample) {
                            "$segmentPath sample fields must both be present or absent"
                        }
                        if (hasStartSample) {
                            require(segment.optLong("source_start_sample", -1L) >= 0L) {
                                "$segmentPath.source_start_sample must be non-negative"
                            }
                            require(segment.optLong("source_end_sample", -1L) >= 0L) {
                                "$segmentPath.source_end_sample must be non-negative"
                            }
                        }
                        cueStart + (sourceEnd - sourceStart)
                    }

                    kind == "audio" && type == "silence" -> {
                        cueStart + finitePositive(
                            segment,
                            "duration",
                            "$segmentPath.duration"
                        )
                    }

                    kind == "subtitle" && type == "subtitle" -> {
                        require(segment.has("text")) {
                            "$segmentPath.text is required"
                        }
                        cueStart + finitePositive(
                            segment,
                            "duration",
                            "$segmentPath.duration"
                        )
                    }

                    else -> throw IllegalArgumentException(
                        "$segmentPath.type '$type' is invalid for kind '$kind'"
                    )
                }

                require(cueEnd <= cueDuration + EPSILON) {
                    "$segmentPath ends at $cueEnd, beyond cue duration $cueDuration"
                }
                intervals += cueStart to cueEnd
            }

            intervals.sortWith(compareBy<Pair<Float, Float>> { it.first }.thenBy { it.second })
            for (i in 1 until intervals.size) {
                require(intervals[i].first >= intervals[i - 1].second - EPSILON) {
                    "$trackPath has overlapping segments"
                }
            }
        }
    }

    private fun loadCompiledAnimationDurations(
        gameRoot: File,
        track: String
    ): Map<String, Float> {
        val path = File(gameRoot, "tutorial/anim/v2/$track.compiled.json")
        if (!path.isFile) return emptyMap()
        val root = try {
            JSONObject(path.readText(Charsets.UTF_8))
        } catch (error: Exception) {
            throw IllegalArgumentException(
                "invalid compiled animation JSON at ${path.absolutePath}: " +
                    error.message,
                error
            )
        }
        val cues = root.optJSONArray("cues") ?: return emptyMap()
        val result = LinkedHashMap<String, Float>()
        for (i in 0 until cues.length()) {
            val cue = cues.optJSONObject(i) ?: continue
            val id = cue.optString("id", "").trim()
            if (id.isBlank()) continue
            val duration = cue.optDouble("duration", Double.NaN)
            require(duration.isFinite() && duration >= 0.0) {
                "compiled animation '$id' has invalid duration"
            }
            result[id] = duration.toFloat()
        }
        return result
    }

    private fun finiteNonNegative(obj: JSONObject, key: String, path: String): Float {
        val value = obj.optDouble(key, Double.NaN)
        require(value.isFinite() && value >= 0.0) {
            "$path must be a finite non-negative number"
        }
        return value.toFloat()
    }

    private fun finitePositive(obj: JSONObject, key: String, path: String): Float {
        val value = obj.optDouble(key, Double.NaN)
        require(value.isFinite() && value > 0.0) {
            "$path must be a finite positive number"
        }
        return value.toFloat()
    }
}

private fun JSONArray?.toStringList(): List<String> {
    if (this == null) return emptyList()
    val result = ArrayList<String>(length())
    for (i in 0 until length()) {
        val value = optString(i, "").trim()
        if (value.isNotBlank()) result += value
    }
    return result
}
