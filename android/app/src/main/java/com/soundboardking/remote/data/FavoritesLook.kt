package com.soundboardking.remote.data

import android.content.Context
import kotlinx.serialization.Serializable

/** How the favourites grid looks: fewer [columns] make bigger buttons; [fontSp] is the title size. */
@Serializable
data class FavoritesLook(val columns: Int = 4, val fontSp: Int = 12, val shape: Int = 0) {
    /** Width / height of a tile: wider tiles take less room and still fit a readable title. */
    val aspect get() = Aspects[shape.coerceIn(Aspects.indices)]

    /** Tiny tiles need tighter gaps and margins, or the title has no room left. */
    val compact get() = columns >= 8

    companion object {
        val ColumnRange = 2..12
        val FontRange = 6..24
        val Aspects = listOf(1f, 1.3f, 1.7f, 2.2f, 3f)
        val ShapeNames = listOf("Quadrati", "Poco rettangolari", "Rettangolari", "Larghi", "Molto larghi")
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
