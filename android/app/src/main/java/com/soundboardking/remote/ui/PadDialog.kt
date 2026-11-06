package com.soundboardking.remote.ui

import androidx.compose.foundation.Canvas
import androidx.compose.foundation.background
import androidx.compose.foundation.gestures.awaitEachGesture
import androidx.compose.foundation.gestures.awaitFirstDown
import androidx.compose.foundation.gestures.waitForUpOrCancellation
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.FilterChip
import androidx.compose.material3.FilterChipDefaults
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.Stable
import androidx.compose.runtime.compositionLocalOf
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableDoubleStateOf
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.rememberUpdatedState
import androidx.compose.runtime.setValue
import androidx.compose.runtime.snapshotFlow
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.PathEffect
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.hapticfeedback.HapticFeedback
import androidx.compose.ui.hapticfeedback.HapticFeedbackType
import androidx.compose.ui.input.pointer.AwaitPointerEventScope
import androidx.compose.ui.input.pointer.PointerEventPass
import androidx.compose.ui.input.pointer.PointerId
import androidx.compose.ui.input.pointer.PointerInputChange
import androidx.compose.ui.input.pointer.changedToDownIgnoreConsumed
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.platform.LocalHapticFeedback
import androidx.compose.ui.semantics.onClick
import androidx.compose.ui.semantics.onLongClick
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.soundboardking.remote.data.SoundEffect
import com.soundboardking.remote.ui.theme.Gold
import com.soundboardking.remote.ui.theme.GoldSoft
import com.soundboardking.remote.ui.theme.Background
import com.soundboardking.remote.ui.theme.Panel
import com.soundboardking.remote.ui.theme.TextDim
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.drop
import kotlinx.coroutines.launch
import kotlin.math.log2
import kotlin.math.pow
import kotlin.math.roundToInt

/** Speed or pitch 0.5..2 as -1..1 on the pad (0 = normal), so lower and higher take the same room. */
private fun factorToAxis(factor: Double) = log2(factor).coerceIn(-1.0, 1.0)
private fun axisToFactor(axis: Double) = (2.0.pow(axis) * 100).roundToInt() / 100.0

/** The pitch in semitones: "tono +3", "tono 0". */
fun pitchText(pitch: Double): String {
    val semitones = (12 * log2(pitch)).roundToInt()
    return "tono ${if (semitones > 0) "+" else ""}$semitones"
}

private fun padText(pitch: Double, speed: Double) = "×${"%.2f".format(speed)} · ${pitchText(pitch)}"

private const val PadAspect = 1f // square: both axes take the same room
private val HeldPadWidth = 240.dp

/**
 * Sound pad: across = speed (left slower, right faster), up = higher, down = lower; each keeps the
 * other as it is. The point stays where it is let go; Reset brings back normal speed and pitch.
 */
@Composable
fun PadDialog(
    pitch: Double,
    speed: Double,
    onChange: (pitch: Double, speed: Double) -> Unit,
    onDismiss: () -> Unit,
) {
    // While the finger is down the pad shows its own point: echoes from the PC would make it jump back.
    var touching by remember { mutableStateOf(false) }
    var localX by remember { mutableStateOf(factorToAxis(speed).toFloat()) }
    var localY by remember { mutableStateOf(factorToAxis(pitch).toFloat()) }
    val x = if (touching) localX else factorToAxis(speed).toFloat()
    val y = if (touching) localY else factorToAxis(pitch).toFloat()

    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text("Pad dei suoni") },
        text = {
            Column {
                Text(
                    padText(axisToFactor(y.toDouble()), axisToFactor(x.toDouble())),
                    color = TextDim,
                    fontSize = 13.sp,
                )
                PadSurface(
                    x,
                    y,
                    Modifier
                        .padding(top = 10.dp)
                        .fillMaxWidth()
                        .aspectRatio(PadAspect)
                        .pointerInput(Unit) {
                            fun send(position: Offset) {
                                localX = (position.x / size.width * 2f - 1f).coerceIn(-1f, 1f)
                                localY = (1f - position.y / size.height * 2f).coerceIn(-1f, 1f)
                                onChange(axisToFactor(localY.toDouble()), axisToFactor(localX.toDouble()))
                            }
                            awaitEachGesture {
                                val down = awaitFirstDown()
                                touching = true
                                send(down.position)
                                while (true) {
                                    val event = awaitPointerEvent()
                                    val change = event.changes.firstOrNull { it.id == down.id } ?: break
                                    if (!change.pressed) break
                                    change.consume()
                                    send(change.position)
                                }
                                touching = false
                            }
                        },
                )
            }
        },
        confirmButton = { TextButton(onClick = onDismiss) { Text("Chiudi", color = Gold) } },
        dismissButton = { TextButton(onClick = { onChange(1.0, 1.0) }) { Text("Reset", color = TextDim) } },
    )
}

/** The pad's face: grid, point and the hints at the edges. [x] -1..1 from left to right, [y] -1..1 from bottom to top. */
@Composable
private fun PadSurface(x: Float, y: Float, modifier: Modifier = Modifier) {
    Box(modifier.clip(RoundedCornerShape(10.dp)).background(Background)) {
        Canvas(Modifier.matchParentSize()) {
            val dash = PathEffect.dashPathEffect(floatArrayOf(6f, 8f))
            for (i in listOf(1, 3)) {
                val gx = size.width * i / 4
                val gy = size.height * i / 4
                drawLine(TextDim.copy(alpha = 0.25f), Offset(gx, 0f), Offset(gx, size.height), pathEffect = dash)
                drawLine(TextDim.copy(alpha = 0.25f), Offset(0f, gy), Offset(size.width, gy), pathEffect = dash)
            }
            // The axes through the middle, with a ring where they cross: speed and pitch as they are.
            val axis = TextDim.copy(alpha = 0.8f)
            val middle = Offset(size.width / 2, size.height / 2)
            val axisWidth = 1.5.dp.toPx()
            drawLine(axis, Offset(middle.x, 0f), Offset(middle.x, size.height), strokeWidth = axisWidth)
            drawLine(axis, Offset(0f, middle.y), Offset(size.width, middle.y), strokeWidth = axisWidth)
            drawCircle(axis, radius = 5.dp.toPx(), center = middle, style = Stroke(axisWidth))
            val point = Offset((x + 1f) / 2f * size.width, (1f - (y + 1f) / 2f) * size.height)
            drawLine(Gold.copy(alpha = 0.5f), Offset(point.x, 0f), Offset(point.x, size.height), pathEffect = dash)
            drawLine(Gold.copy(alpha = 0.5f), Offset(0f, point.y), Offset(size.width, point.y), pathEffect = dash)
            drawCircle(Gold, radius = 13.dp.toPx(), center = point)
            drawCircle(Color.White, radius = 13.dp.toPx(), center = point, style = Stroke(2.dp.toPx()))
        }
        Text("↑ più acuto", color = TextDim, fontSize = 11.sp, modifier = Modifier.align(Alignment.TopStart).padding(8.dp))
        Text("↓ più grave", color = TextDim, fontSize = 11.sp, modifier = Modifier.align(Alignment.BottomStart).padding(8.dp))
        // The speed at the sides, just above the middle line.
        Text("← più lento", color = TextDim, fontSize = 11.sp, modifier = Modifier.align(Alignment.CenterStart).padding(start = 8.dp, bottom = 22.dp))
        Text("più veloce →", color = TextDim, fontSize = 11.sp, modifier = Modifier.align(Alignment.CenterEnd).padding(end = 8.dp, bottom = 22.dp))
    }
}

/**
 * The pad worked straight from its chip, finger still down: pitch and speed while held, put back
 * as they were when let go, unless another finger tapped the screen meanwhile (then they stay).
 */
@Stable
class PadHold {
    var active by mutableStateOf(false)
        private set
    var locked by mutableStateOf(false)
        private set
    var pitch by mutableDoubleStateOf(1.0)
        private set
    var speed by mutableDoubleStateOf(1.0)
        private set

    /** The finger on the chip: any other finger that lands while held is the "keep it" tap. */
    internal var finger: PointerId? = null
        private set
    private var startPitch = 1.0
    private var startSpeed = 1.0

    fun start(finger: PointerId, pitch: Double, speed: Double) {
        this.finger = finger
        startPitch = pitch
        startSpeed = speed
        this.pitch = pitch
        this.speed = speed
        locked = false
        active = true
    }

    /** Moves the point from where it was by [across] and [up], as fractions of the pad. */
    fun move(across: Float, up: Float) {
        speed = axisToFactor((factorToAxis(startSpeed) + across * 2).coerceIn(-1.0, 1.0))
        pitch = axisToFactor((factorToAxis(startPitch) + up * 2).coerceIn(-1.0, 1.0))
    }

    fun toggleLock() {
        if (active) locked = !locked
    }

    /** Ends the hold; returns the pitch and speed to put back, or null when they stay as they are. */
    fun finish(): Pair<Double, Double>? {
        active = false
        finger = null
        return if (locked || (pitch == startPitch && speed == startSpeed)) null else startPitch to startSpeed
    }
}

/**
 * The pad's chip: a tap opens the pad dialog. Held still for a moment or dragged, it works the pad
 * on the fly, moving the point from where it is as if the finger were on [PadHoldOverlay].
 */
@Composable
fun PadChip(
    pitch: Double,
    speed: Double,
    hold: PadHold,
    onChange: (pitch: Double, speed: Double) -> Unit,
    modifier: Modifier = Modifier,
) {
    var dialog by remember { mutableStateOf(false) }
    val current by rememberUpdatedState(pitch to speed)
    val send by rememberUpdatedState(onChange)
    val haptic = LocalHapticFeedback.current
    Box(
        modifier.pointerInput(hold) {
            // Before the chip itself (and the scrolling row), which would click or scroll otherwise.
            awaitEachGesture {
                val down = awaitFirstDown(requireUnconsumed = false, pass = PointerEventPass.Initial)
                down.consume()
                val tapped = holdPad(down, hold, viewConfiguration.longPressTimeoutMillis, haptic, { current }) { p, s -> send(p, s) }
                if (tapped) dialog = true
            }
        },
    ) {
        FilterChip(
            selected = speed != 1.0 || pitch != 1.0,
            onClick = { dialog = true }, // TalkBack; touches are handled above
            label = { Text("Pad ${padText(pitch, speed)}") },
            colors = FilterChipDefaults.filterChipColors(selectedContainerColor = GoldSoft, selectedLabelColor = Gold),
        )
    }
    if (dialog) PadDialog(pitch, speed, onChange = onChange, onDismiss = { dialog = false })
}

/**
 * Follows the [down] finger until it lifts, consuming it: once it stayed down [holdAfterMillis] or
 * moved, it works [hold] as on [PadHoldOverlay]. True when it lifted before that, a plain tap.
 */
private suspend fun AwaitPointerEventScope.holdPad(
    down: PointerInputChange,
    hold: PadHold,
    holdAfterMillis: Long,
    haptic: HapticFeedback,
    current: () -> Pair<Double, Double>,
    send: (pitch: Double, speed: Double) -> Unit,
): Boolean {
    while (true) {
        val event = withTimeoutOrNull(holdAfterMillis) { awaitPointerEvent(PointerEventPass.Initial) } ?: break
        val change = event.changes.firstOrNull { it.id == down.id } ?: return false
        change.consume()
        if (!change.pressed) return true
        if ((change.position - down.position).getDistance() > viewConfiguration.touchSlop) break
    }
    startHold(down, hold, haptic, current)
    followHold(down, hold, send)
    return false
}

private fun startHold(down: PointerInputChange, hold: PadHold, haptic: HapticFeedback, current: () -> Pair<Double, Double>) {
    val (pitch, speed) = current()
    hold.start(down.id, pitch, speed)
    haptic.performHapticFeedback(HapticFeedbackType.LongPress)
}

/** Moves [hold]'s point with the [down] finger, consuming it, until it lifts; then ends the hold. */
private suspend fun AwaitPointerEventScope.followHold(
    down: PointerInputChange,
    hold: PadHold,
    send: (pitch: Double, speed: Double) -> Unit,
) {
    val padWidth = HeldPadWidth.toPx()
    while (true) {
        val change = awaitPointerEvent(PointerEventPass.Initial).changes.firstOrNull { it.id == down.id } ?: break
        change.consume()
        if (!change.pressed) break
        val delta = change.position - down.position
        hold.move(delta.x / padWidth, -delta.y / (padWidth / PadAspect))
        send(hold.pitch, hold.speed)
    }
    hold.finish()?.let { (p, s) -> send(p, s) }
}

/** The pad as the sounds reach it: [current] pitch and speed, and where changes go. */
@Stable
class SoundPad(val hold: PadHold, val current: () -> Pair<Double, Double>, val onChange: (pitch: Double, speed: Double) -> Unit)

/** The pad for a sound held down; null where there is none (previews, tests). */
val LocalSoundPad = compositionLocalOf<SoundPad?> { null }

// Kept down this long on a sound, the pad shows; a tap lifts sooner.
private const val SoundPadHoldMillis = 250L

// Then the sound waits this long, to leave time to move the point before it starts.
private const val SoundStartMillis = 400L

private enum class Press { Tap, Menu, Hold, None }

/**
 * How a sound's tile or row answers touches: a tap plays it at once ([onPlay]); held still a moment,
 * the pad shows as when its chip is held and the sound starts a little later, already changed by
 * it (at once if the finger lifts first); two fingers together open its menu ([onMenu]). A finger
 * that moves before the hold scrolls the list as usual.
 */
@Composable
fun Modifier.soundPress(enabled: Boolean, onPlay: () -> Unit, onMenu: () -> Unit): Modifier {
    val pad = LocalSoundPad.current
    val play by rememberUpdatedState(onPlay)
    val menu by rememberUpdatedState(onMenu)
    val haptic = LocalHapticFeedback.current
    val scope = rememberCoroutineScope()
    if (!enabled) return this
    return semantics {
        onClick("Riproduci") { play(); true }
        onLongClick("Altre azioni") { menu(); true }
    }.pointerInput(pad) {
        awaitEachGesture {
            val down = awaitFirstDown()
            // Before the hold the list still gets the moves: consumed only once the touch is ours.
            val press = withTimeoutOrNull(SoundPadHoldMillis) {
                var press: Press? = null
                while (press == null) {
                    val event = awaitPointerEvent()
                    val change = event.changes.firstOrNull { it.id == down.id }
                    press = when {
                        event.changes.count { it.pressed } >= 2 -> Press.Menu
                        change == null || change.isConsumed -> Press.None
                        !change.pressed -> Press.Tap.also { change.consume() }
                        (change.position - down.position).getDistance() > viewConfiguration.touchSlop -> Press.None
                        else -> null
                    }
                }
                press
            } ?: Press.Hold
            when (press) {
                Press.Tap -> play()
                Press.Menu -> {
                    haptic.performHapticFeedback(HapticFeedbackType.LongPress)
                    menu()
                    do {
                        val event = awaitPointerEvent()
                        event.changes.forEach { it.consume() }
                    } while (event.changes.any { it.pressed })
                }
                Press.Hold -> {
                    if (pad == null) {
                        // Nowhere to show the pad: a slow tap.
                        val up = waitForUpOrCancellation()
                        if (up != null) play()
                        return@awaitEachGesture
                    }
                    startHold(down, pad.hold, haptic, pad.current)
                    var played = false
                    val start = scope.launch {
                        delay(SoundStartMillis)
                        played = true
                        play()
                    }
                    followHold(down, pad.hold, pad.onChange)
                    start.cancel()
                    if (!played) play()
                }
                Press.None -> Unit
            }
        }
    }
}

/** The pad shown while its chip is held, following the finger; it takes no touches of its own. */
@Composable
fun PadHoldOverlay(hold: PadHold, modifier: Modifier = Modifier) {
    val haptic = LocalHapticFeedback.current
    LaunchedEffect(hold) {
        snapshotFlow { hold.locked }.drop(1).collect { haptic.performHapticFeedback(HapticFeedbackType.LongPress) }
    }
    Surface(modifier, color = Panel, shape = RoundedCornerShape(16.dp), shadowElevation = 8.dp) {
        Column(Modifier.padding(12.dp)) {
            Text(padText(hold.pitch, hold.speed), color = TextDim, fontSize = 13.sp)
            Text(
                if (hold.locked) "Resta così quando lasci" else "Tocca con un altro dito: resta così",
                color = Gold,
                fontSize = 12.sp,
                modifier = Modifier.padding(top = 2.dp),
            )
            PadSurface(
                factorToAxis(hold.speed).toFloat(),
                factorToAxis(hold.pitch).toFloat(),
                Modifier.padding(top = 8.dp).width(HeldPadWidth).aspectRatio(PadAspect),
            )
        }
    }
}

/**
 * Watches the whole screen for the other finger that keeps a held pad as it is when let go; that
 * finger touches nothing else (no sound starts under it). A second tap unlocks.
 */
fun Modifier.padHoldSecondFinger(hold: PadHold) = pointerInput(hold) {
    val others = mutableSetOf<PointerId>()
    awaitPointerEventScope {
        while (true) {
            val event = awaitPointerEvent(PointerEventPass.Initial)
            for (change in event.changes) {
                if (change.changedToDownIgnoreConsumed() && hold.active && change.id != hold.finger) {
                    others += change.id
                    hold.toggleLock()
                }
                if (change.id in others) {
                    change.consume()
                    if (!change.pressed) others -= change.id
                }
            }
        }
    }
}
