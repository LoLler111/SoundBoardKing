package com.soundboardking.remote.data

import android.content.Context
import kotlinx.serialization.Serializable

/** How the favourites grid looks: fewer [columns] make bigger buttons; [fontSp] is the title size. */
@Serializable
data class FavoritesLook(val columns: Int = 4, val fontSp: Int = 12) {
    companion object {
        val ColumnRange = 2..6
        val FontRange = 9..24
    }
}

/** Keeps the favourites look on this phone; it does not depend on the PC. */
class LookStore(context: Context) {
    private val prefs = context.getSharedPreferences("look", Context.MODE_PRIVATE)

    fun load(): FavoritesLook = prefs.getString(KEY, null)?.let {
        runCatching { ProtocolJson.decodeFromString(FavoritesLook.serializer(), it) }.getOrNull()
    } ?: FavoritesLook()

    fun save(look: FavoritesLook) {
        prefs.edit().putString(KEY, ProtocolJson.encodeToString(FavoritesLook.serializer(), look)).apply()
    }

    private companion object {
        const val KEY = "favorites"
    }
}
