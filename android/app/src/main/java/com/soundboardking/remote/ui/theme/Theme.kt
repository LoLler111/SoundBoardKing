package com.soundboardking.remote.ui.theme

import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.darkColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.ui.graphics.Color

// Same palette as the Windows app.
val Background = Color(0xFF17171B)
val Panel = Color(0xFF202026)
val Panel2 = Color(0xFF2A2A31)
val Line = Color(0xFF34343D)
val TextMain = Color(0xFFECECF1)
val TextDim = Color(0xFF9A9AA6)
val Gold = Color(0xFFF2B33D)
val GoldSoft = Color(0xFF4A3B1C)
val OnGold = Color(0xFF1A1A1A)
val Danger = Color(0xFFF0716A)
val Online = Color(0xFF5BC97F)

private val Colors = darkColorScheme(
    primary = Gold,
    onPrimary = OnGold,
    primaryContainer = GoldSoft,
    onPrimaryContainer = TextMain,
    secondary = Gold,
    onSecondary = OnGold,
    secondaryContainer = GoldSoft,
    onSecondaryContainer = TextMain,
    background = Background,
    onBackground = TextMain,
    surface = Background,
    onSurface = TextMain,
    surfaceVariant = Panel2,
    onSurfaceVariant = TextDim,
    surfaceContainer = Panel,
    surfaceContainerHigh = Panel2,
    surfaceContainerHighest = Panel2,
    surfaceContainerLow = Panel,
    surfaceContainerLowest = Background,
    outline = Line,
    outlineVariant = Line,
    error = Danger,
)

@Composable
fun SoundBoardTheme(content: @Composable () -> Unit) {
    MaterialTheme(colorScheme = Colors, content = content)
}
