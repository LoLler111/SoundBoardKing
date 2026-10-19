package com.soundboardking.remote.ui

import androidx.compose.foundation.text.KeyboardActions
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.compositionLocalOf
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.focus.FocusRequester
import androidx.compose.ui.focus.focusRequester
import androidx.compose.ui.text.TextRange
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.input.KeyboardCapitalization
import androidx.compose.ui.text.input.TextFieldValue

typealias OnRename = (soundId: String, title: String) -> Unit

/** Renames a sound on the PC; null where renaming makes no sense (previews, tests). */
val LocalRenameSound = compositionLocalOf<OnRename?> { null }

/** Same limit as the PC. */
const val MaxTitleLength = 200

/** Asks for a sound's new title; only the name shown changes, the file on the PC keeps its own. */
@Composable
fun RenameDialog(title: String, onConfirm: (String) -> Unit, onDismiss: () -> Unit) {
    var value by rememberSaveable(stateSaver = TextFieldValue.Saver) {
        mutableStateOf(TextFieldValue(title, selection = TextRange(0, title.length)))
    }
    val focus = remember { FocusRequester() }
    LaunchedEffect(Unit) { focus.requestFocus() }
    val newTitle = value.text.trim()
    val canConfirm = newTitle.isNotEmpty() && newTitle != title
    val confirm = {
        if (canConfirm) onConfirm(newTitle)
        onDismiss()
    }
    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text("Rinomina suono") },
        text = {
            OutlinedTextField(
                value = value,
                onValueChange = { if (it.text.length <= MaxTitleLength) value = it },
                singleLine = true,
                label = { Text("Titolo") },
                supportingText = { Text("Il file sul PC non cambia nome.") },
                keyboardOptions = KeyboardOptions(capitalization = KeyboardCapitalization.Sentences, imeAction = ImeAction.Done),
                keyboardActions = KeyboardActions(onDone = { confirm() }),
                modifier = Modifier.focusRequester(focus),
            )
        },
        confirmButton = { TextButton(onClick = confirm, enabled = canConfirm) { Text("Rinomina") } },
        dismissButton = { TextButton(onClick = onDismiss) { Text("Annulla") } },
    )
}
