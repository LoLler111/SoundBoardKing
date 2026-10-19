package com.soundboardking.remote.data

import kotlinx.coroutines.runBlocking
import okhttp3.Response
import okhttp3.WebSocket
import okhttp3.WebSocketListener
import okhttp3.mockwebserver.MockResponse
import okhttp3.mockwebserver.MockWebServer
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test
import java.util.concurrent.LinkedBlockingQueue
import java.util.concurrent.TimeUnit

class SoundBoardApiTest {
    private val server = MockWebServer()
    private val api = SoundBoardApi()
    private lateinit var baseUrl: String

    @Before
    fun start() {
        server.start()
        baseUrl = server.url("/").toString().trimEnd('/')
    }

    @After
    fun stop() {
        server.shutdown()
    }

    @Test
    fun pairSendsCodeAndDeviceName() = runBlocking {
        server.enqueue(MockResponse().setBody("""{"token":"t0k","deviceId":"d1"}"""))
        val response = api.pair(baseUrl, "123456", "Pixel 8")
        assertEquals("t0k", response.token)
        val request = server.takeRequest()
        assertEquals("/api/pair", request.path)
        val body = request.body.readUtf8()
        assertTrue(body.contains("\"code\":\"123456\"") && body.contains("\"deviceName\":\"Pixel 8\""))
    }

    @Test(expected = PairingRefusedException::class)
    fun refusedCodeIsReported(): Unit = runBlocking {
        server.enqueue(MockResponse().setResponseCode(403).setBody("""{"error":"Codice non valido o scaduto."}"""))
        api.pair(baseUrl, "000000", "x")
    }

    @Test
    fun stateSendsTheTokenAndRevokedTokensAreReported() = runBlocking {
        server.enqueue(MockResponse().setBody("""{"serverId":"s","name":"PC","protocol":1,"libraryVersion":3}"""))
        server.enqueue(MockResponse().setResponseCode(401))

        assertEquals(3, api.state(baseUrl, "abc").libraryVersion)
        assertEquals("Bearer abc", server.takeRequest().getHeader("Authorization"))

        var revoked = false
        try {
            api.state(baseUrl, "abc")
        } catch (_: UnauthorizedException) {
            revoked = true
        }
        assertTrue(revoked)
    }

    @Test
    fun websocketDeliversEventsAndCommands() {
        val received = LinkedBlockingQueue<String>()
        server.enqueue(MockResponse().withWebSocketUpgrade(object : WebSocketListener() {
            override fun onOpen(webSocket: WebSocket, response: Response) {
                webSocket.send("""{"type":"hello","libraryVersion":5}""")
            }

            override fun onMessage(webSocket: WebSocket, text: String) {
                received.add(text)
                webSocket.send("""{"type":"playback","state":"playing","soundId":"s1","positionMs":0,"durationMs":1000}""")
            }
        }))

        val events = LinkedBlockingQueue<ServerEvent>()
        val socket = api.connect(baseUrl, "tok", object : SoundBoardApi.Listener {
            override fun onOpen() {}
            override fun onEvent(event: ServerEvent) {
                events.add(event)
            }
            override fun onClosed(error: Throwable?) {}
        })

        assertEquals(ServerEvent.Hello(5), events.poll(5, TimeUnit.SECONDS))
        socket.send(SoundBoardApi.playCommand("s1"))
        assertEquals("""{"type":"play","soundId":"s1"}""", received.poll(5, TimeUnit.SECONDS))
        assertEquals("s1", (events.poll(5, TimeUnit.SECONDS) as ServerEvent.Playback).playback.soundId)
        assertEquals("/ws?token=tok", server.takeRequest().path)
        socket.close(1000, null)
    }

    @Test
    fun play_command_carries_the_target_only_when_not_the_usual() {
        assertEquals("""{"type":"stopOne","playbackId":"p1"}""", SoundBoardApi.stopOneCommand("p1"))
        assertEquals("""{"type":"togglePauseOne","playbackId":"p1"}""", SoundBoardApi.togglePauseOneCommand("p1"))
        assertEquals("""{"type":"setEffect","effect":"robot"}""", SoundBoardApi.effectCommand(SoundEffect.Robot))
        assertEquals("""{"type":"setEffect","amount":40}""", SoundBoardApi.effectAmountCommand(40))
        assertEquals("""{"type":"setEffect","amount":40,"speed":1.5}""", SoundBoardApi.padCommand(40, 1.5))
        assertEquals("""{"type":"setEffect","target":"voice"}""", SoundBoardApi.effectTargetCommand(EffectTarget.Voice))
        assertEquals("""{"type":"play","soundId":"s1","target":"mic"}""", SoundBoardApi.playCommand("s1", PlayTarget.Mic))
        assertEquals("""{"type":"play","soundId":"s1","target":"speakers"}""", SoundBoardApi.playCommand("s1", PlayTarget.Speakers))
        assertEquals("""{"type":"play","soundId":"s1"}""", SoundBoardApi.playCommand("s1", PlayTarget.Default))
    }
}
