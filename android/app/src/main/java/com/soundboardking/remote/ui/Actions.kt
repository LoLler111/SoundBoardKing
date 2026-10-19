package com.soundboardking.remote.ui

import com.soundboardking.remote.data.DiscoveredPc
import com.soundboardking.remote.data.FavoritesLook
import com.soundboardking.remote.data.PlayTarget
import com.soundboardking.remote.data.SoundEffect

/** What the pairing screen can ask for. Implemented by [AppViewModel]; screens stay free of it. */
interface ConnectActions {
    fun discover()
    fun pairWithLink(text: String)
    fun pairWithDiscovered(pc: DiscoveredPc, code: String)
    fun pairManually(address: String, code: String)
    fun showMessage(message: String)
}

/** The in-app update from the GitHub releases, reachable from both screens. */
interface UpdateActions {
    val appVersion: String

    /** Opens the version dialog (and checks again when nothing is known yet). */
    fun showUpdate()
    fun dismissUpdate()
    fun checkForUpdate(manual: Boolean)
    fun downloadUpdate()
    fun installUpdate()
}

/** What the remote screen can ask for. */
interface RemoteActions {
    fun play(soundId: String, target: PlayTarget)
    fun stop()
    fun stopOne(playbackId: String)
    fun togglePauseOne(playbackId: String)
    fun togglePause()
    fun setMic(enabled: Boolean)
    fun setEffect(effect: SoundEffect)
    fun setEffectAmount(percent: Int)
    fun reconnect()
    fun forgetServer()
    fun setLook(look: FavoritesLook)
}
