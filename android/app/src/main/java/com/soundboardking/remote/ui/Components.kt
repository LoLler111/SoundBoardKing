package com.soundboardking.remote.ui

import androidx.compose.foundation.Canvas
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.size
import androidx.compose.material3.ListItem
import androidx.compose.material3.ListItemDefaults
import androidx.compose.material3.LocalContentColor
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.geometry.CornerRadius
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.hapticfeedback.HapticFeedbackType
import androidx.compose.ui.platform.LocalHapticFeedback
import androidx.compose.ui.platform.LocalView
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import com.soundboardking.remote.data.SoundDto
import com.soundboardking.remote.ui.theme.Gold
import com.soundboardking.remote.ui.theme.TextDim

/** The phone is used as a button panel: don't let the screen turn off while connected. */
@Composable
fun KeepScreenOn() {
    val view = LocalView.current
    DisposableEffect(view) {
        view.keepScreenOn = true
        onDispose { view.keepScreenOn = false }
    }
}

@Composable
fun SoundRow(sound: SoundDto, playing: Boolean, onPlay: (String) -> Unit) {
    val haptic = LocalHapticFeedback.current
    val subtitle = when {
        sound.missing -> "File mancante sul PC"
        sound.artist.isNotBlank() -> sound.artist
        else -> null
    }
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
            .clickable {
                haptic.performHapticFeedback(HapticFeedbackType.TextHandleMove)
                onPlay(sound.id)
            },
    )
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
fun PauseIcon(modifier: Modifier = Modifier) {
    val color = LocalContentColor.current
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
