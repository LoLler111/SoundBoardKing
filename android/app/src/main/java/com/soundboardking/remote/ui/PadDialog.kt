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
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.PathEffect
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.soundboardking.remote.data.SoundEffect
import com.soundboardking.remote.ui.theme.Gold
import com.soundboardking.remote.ui.theme.Background
import com.soundboardking.remote.ui.theme.TextDim
import kotlin.math.log2
import kotlin.math.pow
import kotlin.math.roundToInt

/** Speed 0.5..2 as the pad height -1..1 (0 = normal), so slower and faster take the same room. */
private fun speedToY(speed: Double) = log2(speed).coerceIn(-1.0, 1.0)
private fun yToSpeed(y: Double) = (2.0.pow(y) * 100).roundToInt() / 100.0

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
                Box(
                    Modifier
                        .padding(top = 10.dp)
                        .fillMaxWidth()
                        .aspectRatio(1.4f)
                        .clip(RoundedCornerShape(10.dp))
                        .background(Background)
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
                ) {
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
        },
        confirmButton = { TextButton(onClick = onDismiss) { Text("Chiudi", color = Gold) } },
        dismissButton = { TextButton(onClick = { onChange(70, 1.0) }) { Text("Reset", color = TextDim) } },
    )
}
