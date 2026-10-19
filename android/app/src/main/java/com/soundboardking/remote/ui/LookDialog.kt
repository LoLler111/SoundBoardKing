package com.soundboardking.remote.ui

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Slider
import androidx.compose.material3.SliderDefaults
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalConfiguration
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.soundboardking.remote.data.FavoritesLook
import com.soundboardking.remote.ui.theme.Background
import com.soundboardking.remote.ui.theme.Gold
import com.soundboardking.remote.ui.theme.GoldSoft
import com.soundboardking.remote.ui.theme.Panel2
import com.soundboardking.remote.ui.theme.TextDim
import kotlin.math.roundToInt

private fun sizeName(columns: Int) = when {
    columns >= 10 -> "Minuscoli"
    columns >= 7 -> "Molto piccoli"
    columns >= 5 -> "Piccoli"
    columns == 4 -> "Normali"
    columns == 3 -> "Grandi"
    else -> "Molto grandi"
}

/** Lets the user pick the favourites' button and text size; changes apply (and are saved) at once. */
@Composable
fun LookDialog(look: FavoritesLook, onChange: (FavoritesLook) -> Unit, onDismiss: () -> Unit) {
    val columns = FavoritesLook.ColumnRange
    val fonts = FavoritesLook.FontRange
    val sliderColors = SliderDefaults.colors(thumbColor = Gold, activeTrackColor = Gold, inactiveTrackColor = Panel2)

    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text("Aspetto dei preferiti") },
        text = {
            Column {
                Text("Pulsanti: ${sizeName(look.columns)} (${look.columns} per riga)")
                // Left = small, right = big: the slider runs over the columns backwards.
                Slider(
                    value = (columns.last - look.columns).toFloat(),
                    onValueChange = { onChange(look.copy(columns = columns.last - it.roundToInt())) },
                    valueRange = 0f..(columns.last - columns.first).toFloat(),
                    steps = columns.last - columns.first - 1,
                    colors = sliderColors,
                )
                Spacer(Modifier.height(8.dp))
                Text("Testo: ${look.fontSp}")
                Slider(
                    value = look.fontSp.toFloat(),
                    onValueChange = { onChange(look.copy(fontSp = it.roundToInt())) },
                    valueRange = fonts.first.toFloat()..fonts.last.toFloat(),
                    steps = fonts.last - fonts.first - 1,
                    colors = sliderColors,
                )
                Spacer(Modifier.height(8.dp))
                Text("Forma: ${FavoritesLook.ShapeNames[look.shape.coerceIn(FavoritesLook.Aspects.indices)]}")
                Slider(
                    value = look.shape.toFloat(),
                    onValueChange = { onChange(look.copy(shape = it.roundToInt())) },
                    valueRange = 0f..FavoritesLook.Aspects.lastIndex.toFloat(),
                    steps = FavoritesLook.Aspects.size - 2,
                    colors = sliderColors,
                )
                Spacer(Modifier.height(12.dp))
                Text("Anteprima", color = TextDim, fontSize = 12.sp)
                Spacer(Modifier.height(6.dp))
                LookPreview(look)
            }
        },
        confirmButton = { TextButton(onClick = onDismiss) { Text("Fatto", color = Gold) } },
        dismissButton = { TextButton(onClick = { onChange(FavoritesLook()) }) { Text("Predefinito", color = TextDim) } },
    )
}

/** One row of tiles as wide as they will be on the favourites screen. */
@Composable
private fun LookPreview(look: FavoritesLook) {
    // The dialog is narrower than the screen: scale the tiles to match the real proportion.
    val screenWidth = LocalConfiguration.current.screenWidthDp.dp
    val gap = look.tileGap
    val tileWidth = (screenWidth - look.gridMargin * 2 - gap * (look.columns - 1)) / look.columns
    val shown = minOf(look.columns, if (look.compact) 6 else 3)
    BoxWithConstraints {
        val fit = (maxWidth - gap * (shown - 1)) / shown
        Row(horizontalArrangement = Arrangement.spacedBy(gap)) {
            listOf("airhorn", "Tokyo Drift Teriyaki Boyz", "applauso", "rutto", "dramatic", "Canto del gallo")
                .take(shown).forEachIndexed { i, title ->
                    Surface(
                        shape = RoundedCornerShape(look.tileCorner),
                        color = if (i == 0) GoldSoft else Background,
                        modifier = Modifier.width(minOf(tileWidth, fit)).aspectRatio(look.aspect),
                    ) { TileContent(i + 1, title, look, if (i == 1) "Musica" else "Voice") }
                }
        }
    }
}

// Shared with the favourites grid so the preview matches it exactly.
internal val FavoritesLook.tileGap get() = if (compact) 3.dp else 6.dp
internal val FavoritesLook.gridMargin get() = if (compact) 4.dp else 8.dp
internal val FavoritesLook.tileCorner get() = if (compact) 6.dp else 10.dp

/** "bruh_sound-effect" → "bruh sound effect": with spaces a long name can wrap inside the tile. */
internal fun tileTitle(title: String) = title.replace('_', ' ').replace('-', ' ').replace(Regex(" {2,}"), " ").trim()

/** Slot number in the corner, the category small at the top and the title in the middle of a favourite tile. */
@Composable
internal fun TileContent(number: Int, title: String, look: FavoritesLook, category: String = "") {
    val pad = if (look.compact) 2.dp else 6.dp
    val small = if (look.compact) 7.sp else 10.sp
    Box(Modifier.padding(pad)) {
        // Top line: the slot number, then the category; centred on roomy tiles, right after the number on tiny ones.
        Row(Modifier.align(Alignment.TopStart).fillMaxWidth(), verticalAlignment = Alignment.Top) {
            Text("$number", fontSize = small, lineHeight = 8.sp, color = TextDim)
            if (category.isNotEmpty()) {
                Text(
                    category,
                    fontSize = small,
                    lineHeight = 8.sp,
                    color = TextDim,
                    maxLines = 1,
                    overflow = TextOverflow.Clip,
                    softWrap = false,
                    textAlign = if (look.compact) TextAlign.Start else TextAlign.Center,
                    // On roomy tiles as much space on the right as the number takes on the left, so it stays centred.
                    modifier = Modifier.weight(1f).padding(start = 3.dp, end = if (look.compact) 0.dp else 14.dp),
                )
            }
        }
        Text(
            tileTitle(title),
            fontSize = look.fontSp.sp,
            lineHeight = (look.fontSp + 2).sp,
            textAlign = TextAlign.Center,
            maxLines = 3,
            overflow = TextOverflow.Ellipsis,
            modifier = Modifier.align(Alignment.Center).fillMaxWidth().padding(top = if (look.compact) 8.dp else pad),
        )
    }
}
