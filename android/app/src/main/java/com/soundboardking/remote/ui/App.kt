package com.soundboardking.remote.ui

import androidx.compose.material3.SnackbarHostState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.remember
import androidx.lifecycle.compose.collectAsStateWithLifecycle

@Composable
fun App(viewModel: AppViewModel) {
    val state by viewModel.state.collectAsStateWithLifecycle()
    val update by viewModel.update.collectAsStateWithLifecycle()
    val updateDialog by viewModel.updateDialog.collectAsStateWithLifecycle()
    val snackbar = remember { SnackbarHostState() }
    LaunchedEffect(Unit) {
        viewModel.messages.collect { snackbar.showSnackbar(it) }
    }

    if (state.server == null)
        ConnectScreen(state, viewModel, snackbar, onUpdates = viewModel::showUpdate)
    else
        RemoteScreen(state, viewModel, snackbar, onUpdates = viewModel::showUpdate)

    if (updateDialog) UpdateDialog(update, viewModel)
}
