package com.soundboardking.remote.ui

import com.soundboardking.remote.data.DiscoveredPc

/** What the pairing screen can ask for. Implemented by [AppViewModel]; screens stay free of it. */
interface ConnectActions {
    fun discover()
    fun pairWithLink(text: String)
    fun pairWithDiscovered(pc: DiscoveredPc, code: String)
    fun pairManually(address: String, code: String)
    fun showMessage(message: String)
}

/** What the remote screen can ask for. */
interface RemoteActions {
    fun play(soundId: String)
    fun stop()
    fun togglePause()
    fun setMic(enabled: Boolean)
    fun reconnect()
    fun forgetServer()
}
