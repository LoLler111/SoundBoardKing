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

private val SizeNames = mapOf(6 to "Molto piccoli", 5 to "Piccoli", 4 to "Normali", 3 to "Grandi", 2 to "Molto grandi")

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
                Text("Pulsanti: ${SizeNames[look.columns]} (${look.columns} per riga)")
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
    val screenWidth = LocalConfiguration.current.screenWidthDp
    val tileWidth = (screenWidth - 16 - 6 * (look.columns - 1)) / look.columns
    val shown = minOf(look.columns, 3)
    BoxWithConstraints {
    val fit = (maxWidth - 6.dp * (shown - 1)) / shown
    Row(horizontalArrangement = Arrangement.spacedBy(6.dp)) {
        listOf("airhorn", "Tokyo Drift Teriyaki Boyz", "applauso").take(shown).forEachIndexed { i, title ->
            Surface(
                shape = RoundedCornerShape(10.dp),
                color = if (i == 0) GoldSoft else Background,
                modifier = Modifier.width(minOf(tileWidth.dp, fit)).aspectRatio(1f),
            ) {
                Box(Modifier.padding(6.dp)) {
                    Text("${i + 1}", fontSize = 10.sp, color = TextDim, modifier = Modifier.align(Alignment.TopStart))
                    Text(
                        title,
                        fontSize = look.fontSp.sp,
                        lineHeight = (look.fontSp + 2).sp,
                        textAlign = TextAlign.Center,
                        maxLines = 3,
                        overflow = TextOverflow.Ellipsis,
                        modifier = Modifier.align(Alignment.Center).fillMaxWidth().padding(top = 6.dp),
                    )
                }
            }
        }
    }
    }
}
