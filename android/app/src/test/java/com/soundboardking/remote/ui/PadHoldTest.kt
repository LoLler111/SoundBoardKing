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
        hold.start(PointerId(1), 40, 1.0)
        hold.move(0.25f, 0.25f) // a quarter of the height is half the way to double speed
        assertEquals(65, hold.amount)
        assertEquals(1.41, hold.speed, 0.0)
        hold.move(2f, -2f)
        assertEquals(100, hold.amount)
        assertEquals(0.5, hold.speed, 0.0)
    }

    @Test
    fun `letting go puts strength and speed back`() {
        val hold = PadHold()
        hold.start(PointerId(1), 40, 1.0)
        hold.move(0.1f, 0.2f)
        assertEquals(40 to 1.0, hold.finish())
        assertFalse(hold.active)
    }

    @Test
    fun `another finger keeps them, a second one unlocks`() {
        val hold = PadHold()
        hold.start(PointerId(1), 40, 1.0)
        hold.move(0.1f, 0.2f)
        hold.toggleLock()
        assertNull(hold.finish())

        hold.start(PointerId(2), 50, 1.15)
        hold.move(0.1f, 0.2f)
        hold.toggleLock()
        hold.toggleLock()
        assertEquals(50 to 1.15, hold.finish())
    }

    @Test
    fun `nothing to put back when nothing moved`() {
        val hold = PadHold()
        hold.start(PointerId(1), 40, 1.0)
        assertNull(hold.finish())
    }
}
