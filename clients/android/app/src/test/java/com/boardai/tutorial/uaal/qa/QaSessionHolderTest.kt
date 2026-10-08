package com.boardai.tutorial.uaal.qa

import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotEquals
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test

class QaSessionHolderTest {
    @Before
    fun setUp() {
        QaSessionHolder.clear()
    }

    @After
    fun tearDown() {
        QaSessionHolder.clear()
    }

    @Test
    fun ensureSessionSameGameKeepsGenerationAndUpdatesContext() {
        val first = QaSessionHolder.ensureSession(context(gameId = "a", cueId = "cue.1"))
        val second = QaSessionHolder.ensureSession(context(gameId = "a", cueId = "cue.2"))

        assertEquals(first.generation, second.generation)
        assertEquals("cue.2", second.context.cueId)
        assertTrue(QaSessionHolder.isCurrent(second.generation))
    }

    @Test
    fun newSessionChangesGenerationAndClearsMessages() {
        val first = QaSessionHolder.ensureSession(context())
        assertTrue(QaSessionHolder.appendMessage(message("hello"), first.context, first.generation))

        val second = QaSessionHolder.startNewSession(context())

        assertNotEquals(first.generation, second.generation)
        assertFalse(QaSessionHolder.isCurrent(first.generation))
        assertTrue(QaSessionHolder.isCurrent(second.generation))
        assertTrue(second.messages.isEmpty())
    }

    @Test
    fun staleAppendIsIgnoredAfterNewSession() {
        val first = QaSessionHolder.ensureSession(context())
        val second = QaSessionHolder.startNewSession(context())

        val accepted = QaSessionHolder.appendMessage(message("late"), first.context, first.generation)

        assertFalse(accepted)
        assertTrue(QaSessionHolder.session?.messages.orEmpty().isEmpty())
        assertEquals(second.generation, QaSessionHolder.session?.generation)
    }

    @Test
    fun switchingGameCreatesNewGenerationAndInvalidatesOldOwner() {
        val first = QaSessionHolder.ensureSession(context(gameId = "a"))

        val second = QaSessionHolder.ensureSession(context(gameId = "b"))

        assertNotEquals(first.generation, second.generation)
        assertFalse(QaSessionHolder.isCurrent(first.generation))
        assertEquals("b", second.gameId)
    }

    private fun context(
        gameId: String = "testgame",
        cueId: String = "cue.1"
    ): QaContext = QaContext(
        gameId = gameId,
        gameName = gameId,
        cueId = cueId,
        cueIndex = 0,
        cueText = "",
        groupPath = emptyList(),
        positionInCue = 0f
    )

    private fun message(content: String): QaMessage = QaMessage(
        role = QaMessage.ROLE_USER,
        content = content,
        timestamp = 1L
    )
}
