package com.boardai.tutorial.uaal.qa

import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.async
import kotlinx.coroutines.test.runTest
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertTrue
import org.junit.Test
import java.net.ServerSocket
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import kotlin.concurrent.thread

class QaRepositoryTest {
    @Test
    fun cancelActiveRequestsDisconnectsBlockedChatRequest() = runTest {
        val server = ServerSocket(0)
        val requestReceived = CountDownLatch(1)
        val releaseServer = CountDownLatch(1)
        val serverThread = thread(isDaemon = true, name = "qa-repository-test-server") {
            try {
                server.accept().use { socket ->
                    socket.getInputStream().read(ByteArray(1024))
                    requestReceived.countDown()
                    releaseServer.await(5, TimeUnit.SECONDS)
                }
            } catch (_: Throwable) {
                requestReceived.countDown()
            }
        }

        try {
            val repository = QaRepository("http://127.0.0.1:${server.localPort}")
            val send = async(Dispatchers.IO) {
                repository.send(
                    gameId = "testgame",
                    messages = emptyList(),
                    context = QaContext(
                        gameId = "testgame",
                        gameName = "Test",
                        cueId = "",
                        cueIndex = -1,
                        cueText = "",
                        groupPath = emptyList(),
                        positionInCue = 0f
                    )
                )
            }

            assertTrue("server did not receive request", requestReceived.await(5, TimeUnit.SECONDS))
            repository.cancelActiveRequests()

            val error = runCatching { send.await() }.exceptionOrNull()
            assertTrue("expected cancellation, got $error", error is CancellationException)
        } finally {
            releaseServer.countDown()
            server.close()
            serverThread.join(1_000)
        }
    }
    @Test
    fun sendParsesPartialEvidenceWithoutDroppingReply() = runTest {
        val body = """
        {
          "reply": "已查到部分规则。",
          "evidence": {
            "tier": "partial",
            "hasData": true,
            "isComplete": false,
            "queryCount": 2,
            "queries": [
              {
                "relation": "explain",
                "entity": "宝石",
                "status": "ok",
                "hasData": true,
                "matched": [ { "id": "gem", "name": "宝石" } ]
              },
              {
                "relation": "explain",
                "entity": "火星规则",
                "status": "no_match",
                "hasData": false,
                "matched": [],
                "candidates": []
              }
            ]
          }
        }
        """.trimIndent()
        val bytes = body.toByteArray(Charsets.UTF_8)
        val server = ServerSocket(0)
        val serverThread = thread(isDaemon = true, name = "qa-repository-evidence-test-server") {
            try {
                server.accept().use { socket ->
                    socket.getInputStream().read(ByteArray(4096))
                    val header = "HTTP/1.1 200 OK\r\n" +
                        "Content-Type: application/json; charset=utf-8\r\n" +
                        "Content-Length: ${bytes.size}\r\n" +
                        "Connection: close\r\n\r\n"
                    socket.getOutputStream().apply {
                        write(header.toByteArray(Charsets.UTF_8))
                        write(bytes)
                        flush()
                    }
                }
            } catch (_: Throwable) {
                // The test assertion below reports the failure if the response never arrives.
            }
        }

        try {
            val repository = QaRepository("http://127.0.0.1:${server.localPort}")
            val result = repository.send(
                gameId = "testgame",
                messages = emptyList(),
                context = QaContext(
                    gameId = "testgame",
                    gameName = "Test",
                    cueId = "",
                    cueIndex = -1,
                    cueText = "",
                    groupPath = emptyList(),
                    positionInCue = 0f
                )
            ).getOrThrow()

            assertEquals("已查到部分规则。", result.reply)
            assertNotNull(result.evidence)
            assertEquals("partial", result.evidence?.tier)
            assertEquals(1, result.evidence?.missingQueries?.size)
            assertEquals("火星规则（no_match）", result.evidence?.missingQueries?.single()?.displayLabel)
        } finally {
            server.close()
            serverThread.join(1_000)
        }
    }

}
