package com.soundboardking.remote.ui

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import com.soundboardking.remote.ui.theme.Danger
import com.soundboardking.remote.ui.theme.Panel
import com.soundboardking.remote.ui.theme.TextDim

/** Version of the app and, when there is one, the newer release with its download and install. */
@Composable
fun UpdateDialog(state: UpdateState, actions: UpdateActions) {
    AlertDialog(
        onDismissRequest = actions::dismissUpdate,
        containerColor = Panel,
        title = {
            Text(
                when (state) {
                    is UpdateState.Available, is UpdateState.Downloading, is UpdateState.Ready ->
                        "Aggiornamento disponibile"
                    else -> "SoundBoardKing ${actions.appVersion}"
                },
            )
        },
        text = {
            Column(verticalArrangement = Arrangement.spacedBy(10.dp)) {
                when (state) {
                    UpdateState.Idle, UpdateState.Checking -> {
                        Text("Controllo degli aggiornamenti…", color = TextDim)
                        LinearProgressIndicator(Modifier.fillMaxWidth())
                    }
                    UpdateState.UpToDate -> Text("Hai già l'ultima versione.", color = TextDim)
                    is UpdateState.Failed -> Text(state.message, color = Danger)
                    is UpdateState.Available -> ReleaseText(state.release.version, state.release.notes, actions.appVersion)
                    is UpdateState.Downloading -> {
                        ReleaseText(state.release.version, state.release.notes, actions.appVersion)
                        LinearProgressIndicator(progress = { state.progress }, modifier = Modifier.fillMaxWidth())
                    }
                    is UpdateState.Ready -> {
                        ReleaseText(state.release.version, state.release.notes, actions.appVersion)
                        Text(
                            "Scaricato. Conferma l'installazione nella finestra di Android: la prima volta ti chiede " +
                                "di consentire a SoundBoardKing di installare app.",
                            color = TextDim,
                        )
                    }
                }
            }
        },
        confirmButton = {
            when (state) {
                is UpdateState.Available -> Button(onClick = actions::downloadUpdate) { Text("Aggiorna") }
                is UpdateState.Ready -> Button(onClick = actions::installUpdate) { Text("Installa") }
                is UpdateState.Failed, UpdateState.UpToDate ->
                    TextButton(onClick = { actions.checkForUpdate(manual = true) }) { Text("Controlla di nuovo") }
                else -> {}
            }
        },
        dismissButton = {
            TextButton(onClick = actions::dismissUpdate) {
                Text(if (state is UpdateState.Available) "Più tardi" else "Chiudi")
            }
        },
    )
}

@Composable
private fun ReleaseText(version: String, notes: String, current: String) {
    Text("Versione $version (ora hai la $current).")
    if (notes.isNotBlank()) {
        Text(
            notes.trim(),
            color = TextDim,
            modifier = Modifier.heightIn(max = 200.dp).verticalScroll(rememberScrollState()),
        )
    }
}
