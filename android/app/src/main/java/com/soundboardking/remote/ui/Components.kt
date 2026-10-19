package com.soundboardking.remote.ui

import android.app.Activity
import android.content.ContextWrapper
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.gestures.detectTapGestures
import androidx.compose.foundation.gestures.detectVerticalDragGestures
import androidx.compose.foundation.ExperimentalFoundationApi
import androidx.compose.foundation.combinedClickable
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.size
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.ListItem
import androidx.compose.material3.ListItemDefaults
import androidx.compose.material3.LocalContentColor
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberUpdatedState
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.geometry.CornerRadius
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.StrokeCap
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.platform.LocalDensity
import kotlin.math.cos
import kotlin.math.roundToInt
import kotlin.math.sin
import androidx.compose.ui.hapticfeedback.HapticFeedbackType
import androidx.compose.ui.platform.LocalHapticFeedback
import androidx.compose.ui.platform.LocalView
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.core.view.WindowCompat
import androidx.core.view.WindowInsetsCompat
import androidx.core.view.WindowInsetsControllerCompat
import com.soundboardking.remote.data.PlayTarget
import com.soundboardking.remote.data.SoundDto
import com.soundboardking.remote.ui.theme.Gold
import com.soundboardking.remote.ui.theme.TextDim

/** Hides the status and navigation bars while [enabled]; a swipe from the edge shows them for a moment. */
@Composable
fun ImmersiveMode(enabled: Boolean) {
    val view = LocalView.current
    DisposableEffect(view, enabled) {
        val window = generateSequence(view.context) { (it as? ContextWrapper)?.baseContext }
            .filterIsInstance<Activity>().firstOrNull()?.window
        val controller = window?.let { WindowCompat.getInsetsController(it, view) }
        if (enabled && controller != null) {
            controller.systemBarsBehavior = WindowInsetsControllerCompat.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE
            controller.hide(WindowInsetsCompat.Type.systemBars())
        }
        onDispose { if (enabled) controller?.show(WindowInsetsCompat.Type.systemBars()) }
    }
}

/** The phone is used as a button panel: don't let the screen turn off while connected. */
@Composable
fun KeepScreenOn() {
    val view = LocalView.current
    DisposableEffect(view) {
        view.keepScreenOn = true
        onDispose { view.keepScreenOn = false }
    }
}

typealias OnPlay = (soundId: String, target: PlayTarget) -> Unit

/** Long-press menu of a sound: play as usual, preview in the PC's headphones only, or mic only. */
@Composable
fun PlayMenu(expanded: Boolean, onDismiss: () -> Unit, onRename: (() -> Unit)? = null, onPick: (PlayTarget) -> Unit) {
    DropdownMenu(expanded = expanded, onDismissRequest = onDismiss) {
        listOf(
            PlayTarget.Default to "Riproduci",
            PlayTarget.Speakers to "Anteprima (solo in cuffia sul PC)",
            PlayTarget.Mic to "Solo nel microfono",
        ).forEach { (target, label) ->
            DropdownMenuItem(text = { Text(label) }, onClick = {
                onDismiss()
                onPick(target)
            })
        }
        if (onRename != null) {
            HorizontalDivider()
            DropdownMenuItem(text = { Text("Rinomina") }, onClick = {
                onDismiss()
                onRename()
            })
        }
    }
}

/** The long-press menu of a sound, with "Rinomina" when the PC can rename it. */
@Composable
fun SoundMenu(sound: SoundDto, expanded: Boolean, onDismiss: () -> Unit, onPlay: OnPlay) {
    val rename = LocalRenameSound.current
    var renaming by remember { mutableStateOf(false) }
    PlayMenu(expanded, onDismiss, onRename = rename?.let { { renaming = true } }) { onPlay(sound.id, it) }
    if (renaming && rename != null) {
        RenameDialog(sound.title, onConfirm = { rename(sound.id, it) }, onDismiss = { renaming = false })
    }
}

@OptIn(ExperimentalFoundationApi::class)
@Composable
fun SoundRow(sound: SoundDto, playing: Boolean, onPlay: OnPlay) {
    val haptic = LocalHapticFeedback.current
    var menu by remember { mutableStateOf(false) }
    val subtitle = when {
        sound.missing -> "File mancante sul PC"
        sound.artist.isNotBlank() -> sound.artist
        else -> null
    }
    Box {
        ListItem(
            headlineContent = {
                Text(sound.title, maxLines = 2, overflow = TextOverflow.Ellipsis, color = if (playing) Gold else LocalContentColor.current)
            },
            supportingContent = if (subtitle == null) null else {
                { Text(subtitle, color = TextDim, maxLines = 1) }
            },
            trailingContent = { Text(formatDuration(sound.durationMs), color = TextDim) },
            colors = ListItemDefaults.colors(containerColor = androidx.compose.ui.graphics.Color.Transparent),
            modifier = Modifier
                .alpha(if (sound.missing) 0.5f else 1f)
                .combinedClickable(
                    onLongClick = {
                        haptic.performHapticFeedback(HapticFeedbackType.LongPress)
                        menu = true
                    },
                ) {
                    haptic.performHapticFeedback(HapticFeedbackType.TextHandleMove)
                    onPlay(sound.id, PlayTarget.Default)
                },
        )
        SoundMenu(sound, menu, onDismiss = { menu = false }, onPlay)
    }
}

fun formatDuration(ms: Long): String {
    if (ms <= 0) return ""
    val totalSeconds = ms / 1000
    val hours = totalSeconds / 3600
    val minutes = (totalSeconds % 3600) / 60
    val seconds = totalSeconds % 60
    return if (hours > 0) "%d:%02d:%02d".format(hours, minutes, seconds) else "%d:%02d".format(minutes, seconds)
}

// The core icon set has no pause or stop glyphs; these are simple enough to draw.

@Composable
fun PauseIcon(modifier: Modifier = Modifier, tint: Color = LocalContentColor.current) {
    val color = tint
    Canvas(modifier.size(24.dp)) {
        val barWidth = size.width * 0.22f
        val top = size.height * 0.18f
        val height = size.height * 0.64f
        drawRoundRect(color, Offset(size.width * 0.22f, top), Size(barWidth, height), CornerRadius(2f, 2f))
        drawRoundRect(color, Offset(size.width * 0.56f, top), Size(barWidth, height), CornerRadius(2f, 2f))
    }
}

@Composable
fun StopIcon(modifier: Modifier = Modifier) {
    val color = LocalContentColor.current
    Canvas(modifier.size(24.dp)) {
        val side = size.width * 0.56f
        val inset = (size.width - side) / 2
        drawRoundRect(color, Offset(inset, inset), Size(side, side), CornerRadius(3f, 3f))
    }
}

/** Four corner brackets: pointing out to enter full screen, pointing in ([exit]) to leave it. */
@Composable
fun FullScreenIcon(exit: Boolean, modifier: Modifier = Modifier) {
    val color = LocalContentColor.current
    Canvas(modifier.size(24.dp)) {
        val stroke = size.width * 0.09f
        val arm = size.width * 0.22f
        val reach = size.width * if (exit) 0.12f else 0.38f
        // Arms run towards the centre when entering, away from it when leaving.
        val dir = if (exit) 1f else -1f
        for (sx in listOf(-1f, 1f)) for (sy in listOf(-1f, 1f)) {
            val c = Offset(center.x + sx * reach, center.y + sy * reach)
            drawLine(color, c, Offset(c.x + sx * dir * arm, c.y), stroke, StrokeCap.Round)
            drawLine(color, c, Offset(c.x, c.y + sy * dir * arm), stroke, StrokeCap.Round)
        }
    }
}

/**
 * Round knob for 0-100: drag up or down to turn it (a drag of about 120 dp goes end to end),
 * double tap to go back to [default]. The arc covers 270 degrees like the one on the PC.
 */
@Composable
fun Knob(
    value: Int,
    enabled: Boolean,
    onChange: (Int) -> Unit,
    modifier: Modifier = Modifier,
    default: Int = 70,
) {
    val density = LocalDensity.current
    val dragRange = with(density) { 120.dp.toPx() }
    val track = TextDim.copy(alpha = 0.35f)
    val pointer = LocalContentColor.current
    var dragStart by remember { mutableStateOf(0f) }
    var dragged by remember { mutableStateOf(0f) }
    val current by rememberUpdatedState(value)
    Canvas(
        modifier
            .size(36.dp)
            .alpha(if (enabled) 1f else 0.4f)
            .pointerInput(enabled) {
                if (!enabled) return@pointerInput
                detectVerticalDragGestures(
                    onDragStart = {
                        dragStart = current.toFloat()
                        dragged = 0f
                    },
                ) { change, amount ->
                    change.consume()
                    dragged -= amount
                    val next = (dragStart + dragged / dragRange * 100).roundToInt().coerceIn(0, 100)
                    if (next != current) onChange(next)
                }
            }
            .pointerInput(enabled) {
                if (enabled) detectTapGestures(onDoubleTap = { onChange(default) })
            },
    ) {
        val stroke = size.minDimension * 0.12f
        val inset = stroke / 2
        val arcSize = Size(size.minDimension - stroke, size.minDimension - stroke)
        val topLeft = Offset(inset, inset)
        drawArc(track, 135f, 270f, false, topLeft, arcSize, style = Stroke(stroke, cap = StrokeCap.Round))
        if (value > 0)
            drawArc(Gold, 135f, 270f * value / 100f, false, topLeft, arcSize, style = Stroke(stroke, cap = StrokeCap.Round))
        val angle = Math.toRadians(135.0 + 270.0 * value / 100.0)
        val radius = arcSize.width / 2 * 0.55f
        drawLine(
            pointer,
            center,
            Offset(center.x + (cos(angle) * radius).toFloat(), center.y + (sin(angle) * radius).toFloat()),
            strokeWidth = size.minDimension * 0.07f,
            cap = StrokeCap.Round,
        )
    }
}
