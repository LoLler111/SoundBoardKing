package com.soundboardking.remote.ui

import com.soundboardking.remote.BuildConfig
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Scaffold
import androidx.compose.material3.SnackbarHost
import androidx.compose.material3.SnackbarHostState
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.SpanStyle
import androidx.compose.ui.text.buildAnnotatedString
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.withStyle
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.google.mlkit.vision.barcode.common.Barcode
import com.google.mlkit.vision.codescanner.GmsBarcodeScannerOptions
import com.google.mlkit.vision.codescanner.GmsBarcodeScanning
import com.soundboardking.remote.data.DiscoveredPc
import com.soundboardking.remote.ui.theme.Danger
import com.soundboardking.remote.ui.theme.Gold
import com.soundboardking.remote.ui.theme.Panel
import com.soundboardking.remote.ui.theme.TextDim

/** Shown until the phone is paired with a PC. */
@Composable
fun ConnectScreen(state: UiState, actions: ConnectActions, snackbar: SnackbarHostState, onUpdates: () -> Unit = {}) {
    val context = LocalContext.current
    var codeFor by remember { mutableStateOf<DiscoveredPc?>(null) }
    var manual by remember { mutableStateOf(false) }

    fun scanQr() {
        val options = GmsBarcodeScannerOptions.Builder().setBarcodeFormats(Barcode.FORMAT_QR_CODE).build()
        GmsBarcodeScanning.getClient(context, options).startScan()
            .addOnSuccessListener { barcode -> barcode.rawValue?.let(actions::pairWithLink) }
            .addOnFailureListener {
                actions.showMessage("Scanner non disponibile: inquadra il QR con la fotocamera del telefono, oppure inserisci il codice.")
            }
    }

    Scaffold(snackbarHost = { SnackbarHost(snackbar) }) { padding ->
        Column(
            Modifier
                .padding(padding)
                .fillMaxSize()
                .verticalScroll(rememberScrollState())
                .padding(20.dp),
            verticalArrangement = Arrangement.spacedBy(16.dp),
        ) {
            Text(
                buildAnnotatedString {
                    append("SoundBoard")
                    withStyle(SpanStyle(color = Gold)) { append("King") }
                },
                fontSize = 28.sp,
                fontWeight = FontWeight.Bold,
            )
            Text("Abbina il telefono al PC: sul PC apri SoundBoardKing e premi il pulsante Telefono in alto.", color = TextDim)

            Button(onClick = ::scanQr, enabled = !state.pairingBusy, modifier = Modifier.fillMaxWidth().height(52.dp)) {
                Text("Scansiona il QR")
            }

            if (state.pairingBusy) LinearProgressIndicator(Modifier.fillMaxWidth())
            state.pairingError?.let { Text(it, color = Danger) }
            state.connectionMessage?.let { Text(it, color = Danger) }

            Card(colors = CardDefaults.cardColors(containerColor = Panel), modifier = Modifier.fillMaxWidth()) {
                Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
                    Row(verticalAlignment = Alignment.CenterVertically) {
                        Text("PC trovati sulla rete", fontWeight = FontWeight.SemiBold, modifier = Modifier.weight(1f))
                        if (state.discovering)
                            CircularProgressIndicator(Modifier.size(20.dp), strokeWidth = 2.dp)
                        else
                            IconButton(onClick = actions::discover) { Icon(Icons.Filled.Refresh, contentDescription = "Cerca di nuovo") }
                    }
                    if (state.discovered.isEmpty() && !state.discovering)
                        Text("Nessun PC trovato. Controlla che telefono e PC siano sulla stessa rete Wi-Fi.", color = TextDim)
                    state.discovered.forEach { pc ->
                        Column(
                            Modifier
                                .fillMaxWidth()
                                .clickable(enabled = !state.pairingBusy) { codeFor = pc }
                                .padding(vertical = 8.dp),
                        ) {
                            Text(pc.reply.name, fontWeight = FontWeight.SemiBold)
                            Text("${pc.host}:${pc.reply.port}", color = TextDim, style = MaterialTheme.typography.bodySmall)
                        }
                    }
                }
            }

            TextButton(onClick = { manual = true }, enabled = !state.pairingBusy) {
                Text("Inserisci indirizzo e codice a mano")
            }
            TextButton(onClick = onUpdates) {
                Text("Versione ${BuildConfig.VERSION_NAME} · aggiornamenti", color = TextDim)
            }
            Spacer(Modifier.height(8.dp))
        }
    }

    codeFor?.let { pc ->
        PairDialog(
            title = "Abbina \"${pc.reply.name}\"",
            askAddress = false,
            onDismiss = { codeFor = null },
            onConfirm = { _, code ->
                codeFor = null
                actions.pairWithDiscovered(pc, code)
            },
        )
    }
    if (manual) {
        PairDialog(
            title = "Abbina un PC",
            askAddress = true,
            onDismiss = { manual = false },
            onConfirm = { address, code ->
                manual = false
                actions.pairManually(address, code)
            },
        )
    }
}

@Composable
private fun PairDialog(
    title: String,
    askAddress: Boolean,
    onDismiss: () -> Unit,
    onConfirm: (address: String, code: String) -> Unit,
) {
    var address by remember { mutableStateOf("") }
    var code by remember { mutableStateOf("") }
    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text(title) },
        text = {
            Column(verticalArrangement = Arrangement.spacedBy(12.dp)) {
                if (askAddress) {
                    OutlinedTextField(
                        value = address,
                        onValueChange = { address = it },
                        label = { Text("Indirizzo del PC") },
                        placeholder = { Text("192.168.1.10") },
                        singleLine = true,
                        keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Uri),
                    )
                }
                OutlinedTextField(
                    value = code,
                    onValueChange = { value -> code = value.filter(Char::isDigit).take(6) },
                    label = { Text("Codice mostrato sul PC") },
                    singleLine = true,
                    keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.NumberPassword),
                )
            }
        },
        confirmButton = {
            TextButton(onClick = { onConfirm(address, code) }, enabled = code.length == 6 && (!askAddress || address.isNotBlank())) {
                Text("Abbina")
            }
        },
        dismissButton = { TextButton(onClick = onDismiss) { Text("Annulla") } },
    )
}
