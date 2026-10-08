package com.boardai.tutorial.uaal.qa

import org.json.JSONArray
import org.json.JSONObject

/**
 * One successful chat response. [evidence] is optional so an old backend that
 * only returns [reply] keeps working unchanged.
 */
data class QaChatResult(
    val reply: String,
    val evidence: QaAnswerEvidence? = null
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
    val matched: List<QaEvidenceConcept> = emptyList(),
    val candidates: List<QaEvidenceConcept> = emptyList(),
    val references: List<QaEvidenceConcept> = emptyList()
) {
    val isMissing: Boolean
        get() = when (status.lowercase()) {
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
 * Builds a lightweight, user-visible notice. Returns null when the backend did
 * not report any missing query; tier2/tier3 receive a short explanation even
 * though they have no per-query unresolved item.
 */
internal fun qaEvidenceNotice(evidence: QaAnswerEvidence?): String? {
    if (evidence == null) return null

    val missing = evidence.missingQueries
    if (missing.isNotEmpty()) {
        return "本次回答未覆盖：" + missing.joinToString("、") { it.displayLabel }
    }

    return when (evidence.tier.lowercase()) {
        "tier2" -> "本次回答只有候选，请确认具体对象。"
        "tier3" -> if (evidence.queryCount == 0) "本次回答未使用规则库数据。" else "本次回答未找到可用的规则依据。"
        else -> null
    }
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
    return QaChatResult(reply = reply, evidence = evidence)
}

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
        queries = queries
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
