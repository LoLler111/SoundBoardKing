package com.soundboardking.remote.data

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class ModelsTest {
    // Shape produced by the Windows app (System.Text.Json, web defaults).
    private val sampleState = """
        {"serverId":"abc","name":"PC","protocol":1,"libraryVersion":7,
         "sounds":[{"id":"s1","title":"airhorn","artist":"","durationMs":3000,"missing":false,"hotkey":null}],
         "categories":[{"id":"c1","name":"Voice","soundIds":["s1"],"children":[{"id":"c2","name":"Teorema","soundIds":[],"children":[]}]}],
         "hotbar":{"pages":5,"slotsPerPage":20,"slots":[{"page":0,"slot":12,"soundId":"s1"}]},
         "playback":{"state":"stopped","soundId":null,"positionMs":0,"durationMs":0},
         "mic":{"state":"notInstalled","enabled":true},
         "someFutureField":42}
    """.trimIndent()

    @Test
    fun decodesTheFullState() {
        val state = ProtocolJson.decodeFromString(StateDto.serializer(), sampleState)
        assertEquals(7, state.libraryVersion)
        assertEquals("airhorn", state.sounds.single().title)
        assertNull(state.sounds.single().hotkey)
        assertEquals("Teorema", state.categories.single().children.single().name)
        assertEquals(12, state.hotbar.slots.single().slot)
        assertEquals("notInstalled", state.mic.state)
    }

    @Test
    fun parsesServerEvents() {
        assertEquals(ServerEvent.Hello(1), ServerEvent.parse("""{"type":"hello","libraryVersion":1}"""))
        assertEquals(ServerEvent.LibraryChanged(8), ServerEvent.parse("""{"type":"libraryChanged","libraryVersion":8}"""))
        assertEquals(
            ServerEvent.Playback(PlaybackDto("playing", "s1", 0, 3000)),
            ServerEvent.parse("""{"type":"playback","state":"playing","soundId":"s1","positionMs":0,"durationMs":3000}"""),
        )
        assertEquals(
            ServerEvent.Mic(MicDto("idle", false)),
            ServerEvent.parse("""{"type":"mic","state":"idle","enabled":false}"""),
        )
        // The PC escapes quotes as " (a JSON escape, written out here with a literal backslash).
        assertEquals(
            ServerEvent.Error("Impossibile riprodurre \"x\""),
            ServerEvent.parse("{\"type\":\"error\",\"message\":\"Impossibile riprodurre \\u0022x\\u0022\"}"),
        )
    }

    @Test
    fun readsTheVoiceEffect() {
        assertEquals(ServerEvent.Effect("echo", 30), ServerEvent.parse("""{"type":"effect","effect":"echo","amount":30}"""))
        assertEquals(SoundEffect.Echo, SoundEffect.fromWire("echo"))
        // Older PCs send no effect, and an unknown name falls back to none.
        assertEquals(SoundEffect.None, SoundEffect.fromWire("somethingNew"))
    }

    @Test
    fun ignoresUnknownOrBrokenMessages() {
        assertNull(ServerEvent.parse("""{"type":"somethingNew"}"""))
        assertNull(ServerEvent.parse("not json"))
        assertTrue(PlaybackDto("paused").isPaused)
    }

    @Test
    fun listsEverySoundPlayingTogether() {
        val event = ServerEvent.parse(
            """{"type":"playback","state":"playing","soundId":"s2","positionMs":0,"durationMs":900,""" +
                """"playing":[{"id":"p1","soundId":"s1","positionMs":500,"durationMs":3000},{"id":"p2","soundId":"s2","positionMs":0,"durationMs":900}]}""",
        ) as ServerEvent.Playback
        assertEquals(listOf("p1", "p2"), event.playback.sounds.map { it.id })

        // A PC with an older version reports only the latest sound, without an id.
        val old = PlaybackDto("playing", "s1", 100, 3000)
        assertEquals(listOf(PlayingDto("", "s1", 100, 3000)), old.sounds)
        assertTrue(PlaybackDto().sounds.isEmpty())
    }
}
