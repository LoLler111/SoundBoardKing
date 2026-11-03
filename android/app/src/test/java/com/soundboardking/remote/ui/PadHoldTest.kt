package com.soundboardking.remote.ui

import androidx.compose.ui.input.pointer.PointerId
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Test

class PadHoldTest {

    @Test
    fun `moves from where the point was`() {
        val hold = PadHold()
        hold.start(PointerId(1), 1.0, 1.0)
        hold.move(-0.25f, 0.25f) // a quarter of the pad is half the way to an octave, or to double speed
        assertEquals(1.41, hold.pitch, 0.0)
        assertEquals(1.41, hold.speed, 0.0)
        hold.move(-2f, -2f)
        assertEquals(2.0, hold.pitch, 0.0)
        assertEquals(0.5, hold.speed, 0.0)
        hold.move(0.5f, 0f) // to the right is lower
        assertEquals(0.5, hold.pitch, 0.0)
    }

    @Test
    fun `letting go puts pitch and speed back`() {
        val hold = PadHold()
        hold.start(PointerId(1), 0.8, 1.0)
        hold.move(0.1f, 0.2f)
        assertEquals(0.8 to 1.0, hold.finish())
        assertFalse(hold.active)
    }

    @Test
    fun `another finger keeps them, a second one unlocks`() {
        val hold = PadHold()
        hold.start(PointerId(1), 1.0, 1.0)
        hold.move(0.1f, 0.2f)
        hold.toggleLock()
        assertNull(hold.finish())

        hold.start(PointerId(2), 0.9, 1.15)
        hold.move(0.1f, 0.2f)
        hold.toggleLock()
        hold.toggleLock()
        assertEquals(0.9 to 1.15, hold.finish())
    }

    @Test
    fun `nothing to put back when nothing moved`() {
        val hold = PadHold()
        hold.start(PointerId(1), 1.0, 1.0)
        assertNull(hold.finish())
    }

    @Test
    fun `pitch reads in semitones`() {
        assertEquals("tono 0", pitchText(1.0))
        assertEquals("tono +12", pitchText(2.0))
        assertEquals("tono -5", pitchText(0.75))
    }
}
