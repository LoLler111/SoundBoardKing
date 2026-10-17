package com.soundboardking.remote.data

import kotlinx.coroutines.runBlocking
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Assume.assumeTrue
import org.junit.Test
import java.util.concurrent.LinkedBlockingQueue
import java.util.concurrent.TimeUnit

/**
 * Runs the phone's network code against a real SoundBoardKing on this PC. Skipped unless the
 * pairing code currently shown in the PC's Phone window is passed in SBK_PAIR_CODE:
 *   set SBK_PAIR_CODE=123456 && gradlew testDebugUnitTest --tests *RealPcTest*
 */
class RealPcTest {
    @Test
    fun pairsDiscoversAndControlsTheRealPc(): Unit = runBlocking {
        val code = System.getenv("SBK_PAIR_CODE")
        assumeTrue("SBK_PAIR_CODE non impostato", !code.isNullOrBlank())
        val api = SoundBoardApi()

        val discovered = Discovery.find()
        println("scoperta: ${discovered.map { "${it.reply.name}@${it.host}:${it.reply.port}" }}")
        assertTrue("nessun PC trovato con il broadcast", discovered.isNotEmpty())

        val baseUrl = SoundBoardApi.baseUrl("127.0.0.1", DEFAULT_PORT)
        val info = api.info(baseUrl)
        val token = api.pair(baseUrl, code!!, "Test JVM").token
        val state = api.state(baseUrl, token)
        println("stato: ${state.sounds.size} suoni, ${state.categories.size} categorie, ${state.hotbar.slots.size} preferiti")
        assertEquals(info.serverId, discovered.first { it.reply.serverId == info.serverId }.reply.serverId)

        val events = LinkedBlockingQueue<ServerEvent>()
        val socket = api.connect(baseUrl, token, object : SoundBoardApi.Listener {
            override fun onOpen() {}
            override fun onEvent(event: ServerEvent) {
                events.add(event)
            }
            override fun onClosed(error: Throwable?) {}
        })
        assertEquals(ServerEvent.Hello(state.libraryVersion), events.poll(5, TimeUnit.SECONDS))

        val soundId = state.hotbar.slots.first().soundId
        socket.send(SoundBoardApi.playCommand(soundId))
        val reply = events.poll(5, TimeUnit.SECONDS)
        println("risposta al play: $reply")
        assertTrue(reply is ServerEvent.Playback || reply is ServerEvent.Error)

        socket.send(SoundBoardApi.micCommand(state.mic.enabled))
        socket.send(SoundBoardApi.simpleCommand("stop"))
        socket.close(1000, null)
    }
}
