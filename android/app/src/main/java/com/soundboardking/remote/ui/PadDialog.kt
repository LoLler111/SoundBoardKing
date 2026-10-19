package com.soundboardking.remote.ui

import androidx.compose.foundation.Canvas
import androidx.compose.foundation.background
import androidx.compose.foundation.gestures.awaitEachGesture
import androidx.compose.foundation.gestures.awaitFirstDown
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
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableDoubleStateOf
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
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
import androidx.compose.ui.hapticfeedback.HapticFeedbackType
import androidx.compose.ui.input.pointer.PointerEventPass
import androidx.compose.ui.input.pointer.PointerId
import androidx.compose.ui.input.pointer.changedToDownIgnoreConsumed
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.platform.LocalHapticFeedback
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.soundboardking.remote.data.SoundEffect
import com.soundboardking.remote.ui.theme.Gold
import com.soundboardking.remote.ui.theme.GoldSoft
import com.soundboardking.remote.ui.theme.Background
import com.soundboardking.remote.ui.theme.Panel
import com.soundboardking.remote.ui.theme.TextDim
import kotlinx.coroutines.flow.drop
import kotlin.math.log2
import kotlin.math.pow
import kotlin.math.roundToInt

/** Speed 0.5..2 as the pad height -1..1 (0 = normal), so slower and faster take the same room. */
private fun speedToY(speed: Double) = log2(speed).coerceIn(-1.0, 1.0)
private fun yToSpeed(y: Double) = (2.0.pow(y) * 100).roundToInt() / 100.0

private const val PadAspect = 1.4f
private val HeldPadWidth = 280.dp

/**
 * Effect pad: across = strength 0-100, up = faster, down = slower (like a record).
 * The point stays where it is let go; Reset brings back normal speed and the default strength.
 */
@Composable
fun PadDialog(
    effect: SoundEffect,
    amount: Int,
    speed: Double,
    onChange: (amount: Int, speed: Double) -> Unit,
    onDismiss: () -> Unit,
) {
    // While the finger is down the pad shows its own point: echoes from the PC would make it jump back.
    var touching by remember { mutableStateOf(false) }
    var localX by remember { mutableStateOf(amount / 100f) }
    var localY by remember { mutableStateOf(speedToY(speed).toFloat()) }
    val x = if (touching) localX else amount / 100f
    val y = if (touching) localY else speedToY(speed).toFloat()

    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text("Pad degli effetti") },
        text = {
            Column {
                Text(
                    "${effect.label} · ${(x * 100).roundToInt()}% · ×${"%.2f".format(yToSpeed(y.toDouble()))}",
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
                                localX = (position.x / size.width).coerceIn(0f, 1f)
                                localY = (1f - position.y / size.height * 2f).coerceIn(-1f, 1f)
                                onChange((localX * 100).roundToInt(), yToSpeed(localY.toDouble()))
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
        dismissButton = { TextButton(onClick = { onChange(70, 1.0) }) { Text("Reset", color = TextDim) } },
    )
}

/** The pad's face: grid, point and the hints at the edges. [x] 0..1 across, [y] -1..1 from bottom to top. */
@Composable
private fun PadSurface(x: Float, y: Float, modifier: Modifier = Modifier) {
    Box(modifier.clip(RoundedCornerShape(10.dp)).background(Background)) {
        Canvas(Modifier.matchParentSize()) {
            val dash = PathEffect.dashPathEffect(floatArrayOf(6f, 8f))
            for (i in 1..3) {
                val gx = size.width * i / 4
                val gy = size.height * i / 4
                val middle = i == 2
                drawLine(TextDim.copy(alpha = if (middle) 0.5f else 0.25f), Offset(gx, 0f), Offset(gx, size.height),
                    pathEffect = if (middle) null else dash)
                drawLine(TextDim.copy(alpha = if (middle) 0.5f else 0.25f), Offset(0f, gy), Offset(size.width, gy),
                    pathEffect = if (middle) null else dash)
            }
            val point = Offset(x * size.width, (1f - (y + 1f) / 2f) * size.height)
            drawLine(Gold.copy(alpha = 0.5f), Offset(point.x, 0f), Offset(point.x, size.height), pathEffect = dash)
            drawLine(Gold.copy(alpha = 0.5f), Offset(0f, point.y), Offset(size.width, point.y), pathEffect = dash)
            drawCircle(Gold, radius = 13.dp.toPx(), center = point)
            drawCircle(Color.White, radius = 13.dp.toPx(), center = point, style = Stroke(2.dp.toPx()))
        }
        Text("↑ più veloce", color = TextDim, fontSize = 11.sp, modifier = Modifier.align(Alignment.TopStart).padding(8.dp))
        Text("↓ più lento", color = TextDim, fontSize = 11.sp, modifier = Modifier.align(Alignment.BottomStart).padding(8.dp))
        Text("intensità →", color = TextDim, fontSize = 11.sp, modifier = Modifier.align(Alignment.BottomEnd).padding(8.dp))
    }
}

/**
 * The pad worked straight from its chip, finger still down: strength and speed while held, put back
 * as they were when let go, unless another finger tapped the screen meanwhile (then they stay).
 */
@Stable
class PadHold {
    var active by mutableStateOf(false)
        private set
    var locked by mutableStateOf(false)
        private set
    var amount by mutableIntStateOf(0)
        private set
    var speed by mutableDoubleStateOf(1.0)
        private set

    /** The finger on the chip: any other finger that lands while held is the "keep it" tap. */
    internal var finger: PointerId? = null
        private set
    private var startAmount = 0
    private var startSpeed = 1.0

    fun start(finger: PointerId, amount: Int, speed: Double) {
        this.finger = finger
        startAmount = amount
        startSpeed = speed
        this.amount = amount
        this.speed = speed
        locked = false
        active = true
    }

    /** Moves the point from where it was by [across] and [up], as fractions of the pad. */
    fun move(across: Float, up: Float) {
        amount = (startAmount + across * 100).roundToInt().coerceIn(0, 100)
        speed = yToSpeed((speedToY(startSpeed) + up * 2).coerceIn(-1.0, 1.0))
    }

    fun toggleLock() {
        if (active) locked = !locked
    }

    /** Ends the hold; returns the strength and speed to put back, or null when they stay as they are. */
    fun finish(): Pair<Int, Double>? {
        active = false
        finger = null
        return if (locked || (amount == startAmount && speed == startSpeed)) null else startAmount to startSpeed
    }
}

/**
 * The pad's chip: a tap opens the pad dialog. Held still for a moment or dragged, it works the pad
 * on the fly, moving the point from where it is as if the finger were on [PadHoldOverlay].
 */
@Composable
fun PadChip(
    effect: SoundEffect,
    amount: Int,
    speed: Double,
    hold: PadHold,
    onChange: (amount: Int, speed: Double) -> Unit,
    modifier: Modifier = Modifier,
) {
    var dialog by remember { mutableStateOf(false) }
    val current by rememberUpdatedState(amount to speed)
    val send by rememberUpdatedState(onChange)
    val haptic = LocalHapticFeedback.current
    Box(
        modifier.pointerInput(hold) {
            // Before the chip itself (and the scrolling row), which would click or scroll otherwise.
            awaitEachGesture {
                val down = awaitFirstDown(requireUnconsumed = false, pass = PointerEventPass.Initial)
                down.consume()
                val padWidth = HeldPadWidth.toPx()
                var holding = false
                var tapped = false
                fun startHold() {
                    holding = true
                    hold.start(down.id, current.first, current.second)
                    haptic.performHapticFeedback(HapticFeedbackType.LongPress)
                }
                while (true) {
                    val event = if (holding) awaitPointerEvent(PointerEventPass.Initial)
                    else withTimeoutOrNull(viewConfiguration.longPressTimeoutMillis) { awaitPointerEvent(PointerEventPass.Initial) }
                    if (event == null) {
                        startHold()
                        continue
                    }
                    val change = event.changes.firstOrNull { it.id == down.id } ?: break
                    change.consume()
                    if (!change.pressed) {
                        tapped = !holding
                        break
                    }
                    val delta = change.position - down.position
                    if (!holding && delta.getDistance() > viewConfiguration.touchSlop) startHold()
                    if (holding) {
                        hold.move(delta.x / padWidth, -delta.y / (padWidth / PadAspect))
                        send(hold.amount, hold.speed)
                    }
                }
                if (holding) hold.finish()?.let { (a, s) -> send(a, s) }
                else if (tapped) dialog = true
            }
        },
    ) {
        FilterChip(
            selected = speed != 1.0,
            onClick = { dialog = true }, // TalkBack; touches are handled above
            label = { Text("Pad ×${"%.2f".format(speed)}") },
            colors = FilterChipDefaults.filterChipColors(selectedContainerColor = GoldSoft, selectedLabelColor = Gold),
        )
    }
    if (dialog) PadDialog(effect, amount, speed, onChange = onChange, onDismiss = { dialog = false })
}

/** The pad shown while its chip is held, following the finger; it takes no touches of its own. */
@Composable
fun PadHoldOverlay(effect: SoundEffect, hold: PadHold, modifier: Modifier = Modifier) {
    val haptic = LocalHapticFeedback.current
    LaunchedEffect(hold) {
        snapshotFlow { hold.locked }.drop(1).collect { haptic.performHapticFeedback(HapticFeedbackType.LongPress) }
    }
    Surface(modifier, color = Panel, shape = RoundedCornerShape(16.dp), shadowElevation = 8.dp) {
        Column(Modifier.padding(12.dp)) {
            Text("${effect.label} · ${hold.amount}% · ×${"%.2f".format(hold.speed)}", color = TextDim, fontSize = 13.sp)
            Text(
                if (hold.locked) "Resta così quando lasci" else "Tocca con un altro dito: resta così",
                color = Gold,
                fontSize = 12.sp,
                modifier = Modifier.padding(top = 2.dp),
            )
            PadSurface(
                hold.amount / 100f,
                speedToY(hold.speed).toFloat(),
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
