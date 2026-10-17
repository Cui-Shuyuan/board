package com.boardai.tutorial.uaal.qa

import org.json.JSONArray
import org.json.JSONObject
import java.util.Locale

/**
 * One successful chat response. [evidence] is optional so an old backend that
 * only returns [reply] keeps working unchanged.
 */
data class QaChatResult(
    val reply: String,
    val evidence: QaAnswerEvidence? = null,
    val orchestration: QaOrchestrationSummary? = null
)

/** Minimal /api/chat orchestration metadata used by the debug summary line. */
data class QaOrchestrationSummary(
    val modelCallCount: Int = 0,
    val protocolRepairCount: Int = 0
)

/** Evidence summary parsed from ChatResponse.evidence. */
data class QaAnswerEvidence(
    val tier: String = "tier3",
    val isComplete: Boolean = false,
    val hasData: Boolean = false,
    val hasCandidates: Boolean = false,
    val rulesVersion: String? = null,
    val queryCount: Int = 0,
    val okCount: Int = 0,
    val unresolvedCount: Int = 0,
    val noMatchCount: Int = 0,
    val unsupportedCount: Int = 0,
    val pendingCount: Int = 0,
    /** Program-controlled orchestration outcome. Evidence read is not answer correctness. */
    val orchestrationOutcome: String? = null,
    val evidenceReadCount: Int = 0,
    val citedEvidenceCount: Int = 0,
    val modelCallCount: Int = 0,
    val repairCount: Int = 0,
    val omittedEvidenceCount: Int = 0,
    /** Raw backend-confirmed candidate selections, before client recovery validation. */
    val candidateSelectionCount: Int = 0,
    val queries: List<QaEvidenceQuery> = emptyList()
) {
    val missingQueries: List<QaEvidenceQuery>
        get() = queries.filter { it.isMissing }
}

/** Per-query evidence. Missing/unresolved items are the ones a guest can act on. */
data class QaEvidenceQuery(
    val relation: String = "",
    val entity: String = "",
    val status: String = "",
    val source: String = "",
    val message: String? = null,
    val hasData: Boolean = false,
    val resolvedByQueryIndex: Int? = null,
    val matched: List<QaEvidenceConcept> = emptyList(),
    val candidates: List<QaEvidenceConcept> = emptyList(),
    val references: List<QaEvidenceConcept> = emptyList()
) {
    val isMissing: Boolean
        get() = if (resolvedByQueryIndex != null) false else when (status.lowercase()) {
            "unresolved", "no_match", "unsupported" -> true
            "ok" -> !hasData && candidates.isEmpty()
            else -> true
        }

    val displayLabel: String
        get() {
            val subject = entity.ifBlank { relation.ifBlank { "未命名查询" } }
            val statusText = status.ifBlank { "unknown" }
            return "$subject（$statusText）"
        }
}

data class QaEvidenceConcept(
    val id: String = "",
    val name: String = "",
    val type: String? = null,
    val description: String? = null,
    val score: Float? = null
)

/**
 * Builds the debug-only answer completion line.  The elapsed time is measured
 * by the client so it includes transport time; all counters come from the
 * backend evidence/orchestration metadata.
 */
internal fun qaAnswerDebugSummary(result: QaChatResult, elapsedMs: Long): String {
    val seconds = String.format(Locale.US, "%.2f", elapsedMs.coerceAtLeast(0L) / 1000.0)
    val evidence = result.evidence ?: return "回答完成｜$seconds 秒"
    val modelCalls = maxOf(evidence.modelCallCount, result.orchestration?.modelCallCount ?: 0)
    val protocolRepairs = maxOf(
        evidence.repairCount,
        result.orchestration?.protocolRepairCount ?: 0
    )
    val summary = "回答完成｜$seconds 秒" +
        "｜模型调用 $modelCalls 次" +
        "｜规则查询 ${evidence.queryCount} 次" +
        "｜正式证据 ${evidence.citedEvidenceCount} 条" +
        "｜协议纠偏 $protocolRepairs 次"
    return if (evidence.candidateSelectionCount > 0) {
        summary + "｜经过${chineseNumber(evidence.candidateSelectionCount)}次候选选择"
    } else {
        summary
    }
}

private fun chineseNumber(value: Int): String = when (value) {
    1 -> "一"
    2 -> "两"
    3 -> "三"
    4 -> "四"
    5 -> "五"
    6 -> "六"
    7 -> "七"
    8 -> "八"
    9 -> "九"
    10 -> "十"
    else -> value.toString()
}

/**
 * Parses the /api/chat response without making evidence a hard dependency.
 * The backend serializes camelCase in production; PascalCase keys are accepted
 * as a defensive fallback for older or test configurations.
 */
internal fun parseQaChatResponse(body: String): QaChatResult {
    val json = JSONObject(body)
    val reply = json.stringValue("reply", "Reply").orEmpty()
    val evidence = runCatching {
        json.objectValue("evidence", "Evidence")?.let(::parseAnswerEvidence)
    }.getOrNull()
    val orchestration = runCatching {
        json.objectValue("orchestration", "Orchestration")?.let(::parseOrchestrationSummary)
    }.getOrNull()
    return QaChatResult(reply = reply, evidence = evidence, orchestration = orchestration)
}

private fun parseOrchestrationSummary(json: JSONObject): QaOrchestrationSummary =
    QaOrchestrationSummary(
        modelCallCount = json.intValue("modelCallCount", "ModelCallCount"),
        protocolRepairCount = json.intValue(
            "protocolRepairModelCallCount",
            "ProtocolRepairModelCallCount"
        )
    )

private fun parseAnswerEvidence(json: JSONObject): QaAnswerEvidence {
    val queriesJson = json.arrayValue("queries", "Queries")
    val queries = buildList {
        if (queriesJson != null) {
            for (index in 0 until queriesJson.length()) {
                val item = queriesJson.optJSONObject(index) ?: continue
                add(parseEvidenceQuery(item))
            }
        }
    }
    val rawCandidateSelectionCount = queries.count { it.resolvedByQueryIndex != null }
    val candidateSelectionCount = if (json.hasAnyValue(
            "candidateSelectionCount",
            "CandidateSelectionCount"
        )
    ) {
        json.intValue("candidateSelectionCount", "CandidateSelectionCount")
    } else {
        rawCandidateSelectionCount
    }

    return QaAnswerEvidence(
        tier = json.stringValue("tier", "Tier").orEmpty().ifBlank { "tier3" },
        isComplete = json.booleanValue("isComplete", "IsComplete"),
        hasData = json.booleanValue("hasData", "HasData"),
        hasCandidates = json.booleanValue("hasCandidates", "HasCandidates"),
        rulesVersion = json.stringValue("rulesVersion", "RulesVersion"),
        queryCount = json.intValue("queryCount", "QueryCount"),
        okCount = json.intValue("okCount", "OkCount"),
        unresolvedCount = json.intValue("unresolvedCount", "UnresolvedCount"),
        noMatchCount = json.intValue("noMatchCount", "NoMatchCount"),
        unsupportedCount = json.intValue("unsupportedCount", "UnsupportedCount"),
        pendingCount = json.intValue("pendingCount", "PendingCount"),
        orchestrationOutcome = json.stringValue("orchestrationOutcome", "OrchestrationOutcome"),
        evidenceReadCount = json.intValue("evidenceReadCount", "EvidenceReadCount"),
        citedEvidenceCount = json.intValue("citedEvidenceCount", "CitedEvidenceCount"),
        modelCallCount = json.intValue("modelCallCount", "ModelCallCount"),
        repairCount = json.intValue("repairCount", "RepairCount"),
        omittedEvidenceCount = json.intValue("omittedEvidenceCount", "OmittedEvidenceCount"),
        candidateSelectionCount = candidateSelectionCount,
        queries = queries.mapIndexed { index, query ->
            val later = query.resolvedByQueryIndex?.let { queries.getOrNull(it) }
            val validRecovery = query.resolvedByQueryIndex?.let { it > index } == true
                && query.status == "unresolved" && later?.status == "ok" && later.hasData
                && (later.relation == query.relation || (query.relation == "identify" && later.relation == "explain"))
                && query.candidates.map { it.id }.distinct().size == 1
                && later.matched.any { it.id == query.candidates.singleOrNull()?.id }
            if (validRecovery) query else query.copy(resolvedByQueryIndex = null)
        }
    )
}

private fun parseEvidenceQuery(json: JSONObject): QaEvidenceQuery {
    val status = json.stringValue("status", "Status").orEmpty()
    val matched = parseConceptArray(json.arrayValue("matched", "Matched"), rawMatched = true)
    val candidates = parseConceptArray(json.arrayValue("candidates", "Candidates"), rawMatched = false)
    val references = parseConceptArray(json.arrayValue("references", "References"), rawMatched = true)
    val hasData = if (json.hasAnyValue("hasData", "HasData")) {
        json.booleanValue("hasData", "HasData")
    } else {
        status == "ok" && (matched.isNotEmpty() || json.hasAnyValue("catalog", "Catalog"))
    }

    return QaEvidenceQuery(
        relation = json.stringValue("relation", "Relation").orEmpty(),
        entity = json.stringValue("entity", "Entity").orEmpty(),
        status = status,
        source = json.stringValue("source", "Source").orEmpty(),
        message = json.stringValue("message", "Message"),
        hasData = hasData,
        resolvedByQueryIndex = (json.valueOfAny("resolvedByQueryIndex", "ResolvedByQueryIndex") as? Number)
            ?.toInt()?.takeIf { it >= 0 },
        matched = matched,
        candidates = candidates,
        references = references
    )
}

private fun parseConceptArray(array: JSONArray?, rawMatched: Boolean): List<QaEvidenceConcept> {
    if (array == null) return emptyList()
    val result = ArrayList<QaEvidenceConcept>(array.length())
    for (index in 0 until array.length()) {
        val item = array.optJSONObject(index) ?: continue
        val id = item.stringValue("id", "Id") ?: continue
        val name = if (rawMatched) {
            item.rawNameValue()
        } else {
            item.stringValue("name", "Name").orEmpty()
        }
        result.add(
            QaEvidenceConcept(
                id = id,
                name = name,
                type = item.stringValue("type", "Type"),
                description = item.stringValue("description", "Description"),
                score = item.floatValue("score", "Score")
            )
        )
    }
    return result
}

private fun JSONObject.rawNameValue(): String {
    val direct = stringValue("name", "Name")
    if (!direct.isNullOrBlank()) return direct
    val nameObject = objectValue("name", "Name") ?: return ""
    return nameObject.stringValue("zh", "en") ?: ""
}

private fun JSONObject.hasAnyValue(vararg names: String): Boolean = names.any { has(it) && !isNull(it) }

private fun JSONObject.valueOfAny(vararg names: String): Any? {
    for (name in names) {
        if (has(name) && !isNull(name)) return get(name)
    }
    return null
}

private fun JSONObject.stringValue(vararg names: String): String? =
    valueOfAny(*names) as? String

private fun JSONObject.objectValue(vararg names: String): JSONObject? =
    valueOfAny(*names) as? JSONObject

private fun JSONObject.arrayValue(vararg names: String): JSONArray? =
    valueOfAny(*names) as? JSONArray

private fun JSONObject.booleanValue(vararg names: String): Boolean =
    when (val value = valueOfAny(*names)) {
        is Boolean -> value
        is Number -> value.toInt() != 0
        is String -> value.equals("true", ignoreCase = true) || value == "1"
        else -> false
    }

private fun JSONObject.intValue(vararg names: String): Int =
    when (val value = valueOfAny(*names)) {
        is Number -> value.toInt()
        is String -> value.toIntOrNull() ?: 0
        else -> 0
    }

private fun JSONObject.floatValue(vararg names: String): Float? =
    when (val value = valueOfAny(*names)) {
        is Number -> value.toFloat()
        is String -> value.toFloatOrNull()
        else -> null
    }
