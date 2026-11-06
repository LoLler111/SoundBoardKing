package com.soundboardking.remote.ui

import androidx.compose.material3.SnackbarHostState
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.test.click
import androidx.compose.ui.test.junit4.createComposeRule
import androidx.compose.ui.test.onNodeWithText
import androidx.compose.ui.test.performTouchInput
import com.soundboardking.remote.data.FavoritesLook
import com.soundboardking.remote.data.HotbarDto
import com.soundboardking.remote.data.HotbarSlotDto
import com.soundboardking.remote.data.PlayTarget
import com.soundboardking.remote.data.SavedServer
import com.soundboardking.remote.data.SoundDto
import com.soundboardking.remote.data.SoundEffect
import com.soundboardking.remote.data.EffectTarget
import com.soundboardking.remote.data.StateDto
import com.soundboardking.remote.ui.theme.SoundBoardTheme
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config

/** A favourite tapped twice with the finger kept down works the pad, as its chip does when held. */
@RunWith(RobolectricTestRunner::class)
@Config(sdk = [35], qualifiers = "w411dp-h891dp-xxhdpi")
class SoundPadGestureTest {

    @get:Rule
    val compose = createComposeRule()

    private val plays = mutableListOf<String>()
    private val pads = mutableListOf<Pair<Double, Double>>()

    private val actions = object : RemoteActions {
        override fun play(soundId: String, target: PlayTarget) {
            plays += soundId
        }
        override fun stop() {}
        override fun stopOne(playbackId: String) {}
        override fun togglePauseOne(playbackId: String) {}
        override fun togglePause() {}
        override fun setMic(enabled: Boolean) {}
        override fun setEffect(effect: SoundEffect) {}
        override fun setEffectAmount(percent: Int) {}
        override fun setPad(pitch: Double, speed: Double) {
            pads += pitch to speed
        }
        override fun setEffectTarget(target: EffectTarget) {}
        override fun renameSound(soundId: String, title: String) {}
        override fun reconnect() {}
        override fun forgetServer() {}
        override fun setLook(look: FavoritesLook) {}
    }

    private val state = UiState(
        server = SavedServer("pc", "PC", listOf("192.168.1.10"), 8765, "t"),
        connection = ConnectionState.Online,
        library = StateDto(
            serverId = "pc",
            name = "PC",
            protocol = 1,
            libraryVersion = 1,
            sounds = listOf(SoundDto("s0", "airhorn", durationMs = 3_000)),
            hotbar = HotbarDto(slotsPerPage = 100, slots = listOf(HotbarSlotDto(0, 0, "s0"))),
        ),
        effect = SoundEffect.Robot,
        playbackPitch = 0.8,
    )

    private fun show() = compose.setContent {
        SoundBoardTheme { RemoteScreen(state, actions, SnackbarHostState(), Tab.Favorites) }
    }

    private val hint = "Tocca con un altro dito: resta così"

    @Test
    fun `a tap plays at once`() {
        show()
        compose.onNodeWithText("airhorn").performTouchInput { click() }
        compose.waitForIdle()
        assertEquals(listOf("s0"), plays)
        assertTrue(pads.isEmpty())
        compose.onNodeWithText(hint).assertDoesNotExist()
    }

    @Test
    fun `held, the pad shows and the sound starts a moment later, already changed`() {
        show()
        compose.mainClock.autoAdvance = false
        compose.onNodeWithText("airhorn").performTouchInput { down(center) }
        compose.mainClock.advanceTimeBy(300)
        compose.onNodeWithText(hint).assertExists()
        assertTrue(plays.isEmpty()) // not yet: there is time to move the point

        compose.onNodeWithText("airhorn").performTouchInput { moveBy(Offset(width / 2f, -height / 2f)) }
        compose.mainClock.advanceTimeBy(100)
        assertTrue(pads.last().first > 0.8) // up is higher
        assertTrue(pads.last().second > 1.0) // to the right is faster
        assertTrue(plays.isEmpty())

        compose.mainClock.advanceTimeBy(500)
        assertEquals(listOf("s0"), plays)

        compose.onNodeWithText("airhorn").performTouchInput { up() }
        compose.mainClock.advanceTimeBy(100)
        assertEquals(listOf("s0"), plays) // started once
        assertEquals(0.8 to 1.0, pads.last())
        compose.mainClock.autoAdvance = true
        compose.waitForIdle()
        compose.onNodeWithText(hint).assertDoesNotExist()
    }

    @Test
    fun `let go before the sound started, it starts then`() {
        show()
        compose.mainClock.autoAdvance = false
        compose.onNodeWithText("airhorn").performTouchInput { down(center) }
        compose.mainClock.advanceTimeBy(300)
        assertTrue(plays.isEmpty())
        compose.onNodeWithText("airhorn").performTouchInput { up() }
        compose.mainClock.advanceTimeBy(100)
        assertEquals(listOf("s0"), plays)
        compose.mainClock.advanceTimeBy(1000)
        assertEquals(listOf("s0"), plays)
    }

    @Test
    fun `two fingers open the menu`() {
        show()
        compose.onNodeWithText("airhorn").performTouchInput {
            down(0, center - Offset(width / 6f, 0f))
            down(1, center + Offset(width / 6f, 0f))
            up(0)
            up(1)
        }
        compose.waitForIdle()
        compose.onNodeWithText("Solo nel microfono").assertExists()
        assertTrue(plays.isEmpty())
    }

    @Test
    fun `a finger that moves first does nothing`() {
        show()
        compose.onNodeWithText("airhorn").performTouchInput {
            down(center)
            moveBy(Offset(width.toFloat(), 0f))
            up()
        }
        compose.waitForIdle()
        assertTrue(plays.isEmpty())
        assertTrue(pads.isEmpty())
    }
}
