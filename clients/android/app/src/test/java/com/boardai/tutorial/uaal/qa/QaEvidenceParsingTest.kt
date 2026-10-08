package com.boardai.tutorial.uaal.qa

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class QaEvidenceParsingTest {
    @Test
    fun parsesPartialEvidenceWithMissingQuery() {
        val result = parseQaChatResponse(PARTIAL_EVIDENCE_BODY)

        assertEquals("已查到部分规则。", result.reply)
        val evidence = result.evidence
        requireNotNull(evidence)
        assertEquals("partial", evidence.tier)
        assertTrue(evidence.hasData)
        assertFalse(evidence.isComplete)
        assertEquals("rules-v1", evidence.rulesVersion)
        assertEquals(2, evidence.queryCount)
        assertEquals(1, evidence.noMatchCount)
        assertEquals(1, evidence.missingQueries.size)

        val missing = evidence.missingQueries.single()
        assertEquals("explain", missing.relation)
        assertEquals("火星规则", missing.entity)
        assertEquals("no_match", missing.status)
        assertTrue(missing.isMissing)
        assertEquals("火星规则（no_match）", missing.displayLabel)
        assertEquals("gem", evidence.queries.first().matched.single().id)
    }

    @Test
    fun recoveredCandidateKeepsHistoryWithoutMissingNotice() {
        val body = """{"reply":"激活骰","evidence":{"tier":"tier1","isComplete":true,"pendingCount":0,
            "unresolvedCount":1,"queries":[
              {"relation":"identify","status":"unresolved","resolvedByQueryIndex":1,"candidates":[{"id":"activation_die"}]},
              {"relation":"explain","status":"ok","hasData":true,"matched":[{"id":"activation_die"}]}
            ]}}"""
        val evidence = parseQaChatResponse(body).evidence!!
        assertEquals("unresolved", evidence.queries[0].status)
        assertEquals(1, evidence.queries[0].resolvedByQueryIndex)
        assertTrue(evidence.missingQueries.isEmpty())
        assertNull(qaEvidenceNotice(evidence))
    }

    @Test
    fun invalidRecoveryIndexDoesNotHideMissingQuery() {
        for (index in listOf(-1, 0, 99)) {
            val body = """{"reply":"回复","evidence":{"tier":"partial","queries":[
                {"relation":"identify","entity":"白色骰子","status":"unresolved","resolvedByQueryIndex":$index,
                 "candidates":[{"id":"activation_die"}]}
            ]}}"""
            val evidence = parseQaChatResponse(body).evidence!!
            assertEquals(1, evidence.missingQueries.size)
            assertTrue(qaEvidenceNotice(evidence)!!.contains("白色骰子"))
        }
    }

    @Test
    fun missingEvidenceKeepsReplyOnly() {
        val result = parseQaChatResponse("""{"reply":"旧后端回答"}""")

        assertEquals("旧后端回答", result.reply)
        assertNull(result.evidence)
        assertNull(qaEvidenceNotice(result.evidence))
    }

    @Test
    fun malformedEvidenceKeepsReplyUsable() {
        val malformedEvidence = parseQaChatResponse(
            """{"reply":"正常回复","evidence":"not-an-object"}"""
        )
        assertEquals("正常回复", malformedEvidence.reply)
        assertNull(malformedEvidence.evidence)

        val malformedQueries = parseQaChatResponse(
            """{"reply":"正常回复","evidence":{"tier":"partial","queries":"broken"}}"""
        )
        assertEquals("正常回复", malformedQueries.reply)
        assertEquals("partial", malformedQueries.evidence?.tier)
        assertTrue(malformedQueries.evidence?.queries.orEmpty().isEmpty())
    }

    @Test
    fun evidenceNoticeListsMissingQueriesAndHandlesTier2Tier3() {
        val partial = parseQaChatResponse(PARTIAL_EVIDENCE_BODY).evidence
        assertEquals("本次回答未覆盖：火星规则（no_match）", qaEvidenceNotice(partial))

        val tier2 = QaAnswerEvidence(tier = "tier2", hasCandidates = true)
        assertEquals("本次回答只有候选，请确认具体对象。", qaEvidenceNotice(tier2))

        val tier3 = QaAnswerEvidence(tier = "tier3", queryCount = 0)
        assertEquals("本次回答未使用规则库数据。", qaEvidenceNotice(tier3))
    }

    private companion object {
        const val PARTIAL_EVIDENCE_BODY = """
        {
          "reply": "已查到部分规则。",
          "evidence": {
            "tier": "partial",
            "isComplete": false,
            "hasData": true,
            "hasCandidates": false,
            "rulesVersion": "rules-v1",
            "queryCount": 2,
            "okCount": 1,
            "unresolvedCount": 0,
            "noMatchCount": 1,
            "unsupportedCount": 0,
            "queries": [
              {
                "relation": "explain",
                "entity": "宝石",
                "status": "ok",
                "source": "exact_name_zh",
                "hasData": true,
                "matched": [ { "id": "gem", "name": "宝石" } ],
                "candidates": [],
                "references": []
              },
              {
                "relation": "explain",
                "entity": "火星规则",
                "status": "no_match",
                "source": "",
                "message": "规则库里没有这个概念",
                "hasData": false,
                "matched": [],
                "candidates": [],
                "references": []
              }
            ]
          }
        }
        """
    }
}
