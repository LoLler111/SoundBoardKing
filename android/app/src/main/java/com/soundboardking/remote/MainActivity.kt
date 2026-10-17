package com.soundboardking.remote

import android.content.Intent
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.activity.viewModels
import com.soundboardking.remote.ui.App
import com.soundboardking.remote.ui.AppViewModel
import com.soundboardking.remote.ui.theme.SoundBoardTheme

class MainActivity : ComponentActivity() {
    private val viewModel: AppViewModel by viewModels()

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        enableEdgeToEdge()
        if (savedInstanceState == null) handleLink(intent)
        setContent {
            SoundBoardTheme {
                App(viewModel)
            }
        }
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        handleLink(intent)
    }

    override fun onStart() {
        super.onStart()
        viewModel.onForeground()
    }

    override fun onStop() {
        viewModel.onBackground()
        super.onStop()
    }

    /** Opened from the PC's pairing QR with the camera app: sbk://pair?... */
    private fun handleLink(intent: Intent?) {
        val link = intent?.dataString ?: return
        if (link.startsWith("sbk://", ignoreCase = true)) viewModel.pairWithLink(link)
    }
}
