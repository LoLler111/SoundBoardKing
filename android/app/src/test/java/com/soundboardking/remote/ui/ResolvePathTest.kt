package com.soundboardking.remote.ui

import com.soundboardking.remote.data.CategoryDto
import org.junit.Assert.assertEquals
import org.junit.Test

class ResolvePathTest {
    private val tree = listOf(
        CategoryDto("voice", "Voice", children = listOf(CategoryDto("teorema", "Teorema"))),
        CategoryDto("music", "Musica"),
    )

    @Test
    fun followsThePathDownTheTree() {
        assertEquals(listOf("Voice", "Teorema"), resolvePath(tree, listOf("voice", "teorema")).map { it.name })
    }

    @Test
    fun stopsWhereACategoryNoLongerExists() {
        assertEquals(listOf("Voice"), resolvePath(tree, listOf("voice", "deleted", "x")).map { it.name })
        assertEquals(emptyList<CategoryDto>(), resolvePath(tree, listOf("deleted")))
    }
}
