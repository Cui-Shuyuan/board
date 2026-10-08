package com.boardai.tutorial.uaal.qa

import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.async
import kotlinx.coroutines.test.runTest
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
}
