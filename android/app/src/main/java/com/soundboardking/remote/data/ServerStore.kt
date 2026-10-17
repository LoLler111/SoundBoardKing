package com.soundboardking.remote.data

import android.content.Context
import kotlinx.serialization.Serializable

/** The PC this phone is paired with. [hosts] are tried in order; the last one that worked comes first. */
@Serializable
data class SavedServer(
    val serverId: String,
    val name: String,
    val hosts: List<String>,
    val port: Int,
    val token: String,
)

/** Keeps the paired PC in app-private storage. */
class ServerStore(context: Context) {
    private val prefs = context.getSharedPreferences("server", Context.MODE_PRIVATE)

    fun load(): SavedServer? = prefs.getString(KEY, null)?.let {
        runCatching { ProtocolJson.decodeFromString(SavedServer.serializer(), it) }.getOrNull()
    }

    fun save(server: SavedServer) {
        prefs.edit().putString(KEY, ProtocolJson.encodeToString(SavedServer.serializer(), server)).apply()
    }

    fun clear() {
        prefs.edit().remove(KEY).apply()
    }

    private companion object {
        const val KEY = "saved"
    }
}
