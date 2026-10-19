package com.soundboardking.remote.data

import kotlinx.coroutines.runBlocking
import okhttp3.mockwebserver.MockResponse
import okhttp3.mockwebserver.MockWebServer
import okio.Buffer
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder

class AppUpdaterTest {
    @get:Rule
    val temp = TemporaryFolder()

    private val server = MockWebServer()
    private lateinit var updater: AppUpdater

    @Before
    fun start() {
        server.start()
        updater = AppUpdater(server.url("/releases/latest").toString())
    }

    @After
    fun stop() {
        server.shutdown()
    }

    private fun release(tag: String, apk: String? = "SoundBoardKing-android.apk", prerelease: Boolean = false): String {
        val assets = listOfNotNull(
            """{"name":"SoundBoardKing-win-Setup.exe","size":10,"browser_download_url":"${server.url("/setup.exe")}"}""",
            apk?.let { """{"name":"$it","size":5,"browser_download_url":"${server.url("/app.apk")}"}""" },
        )
        return """{"tag_name":"$tag","draft":false,"prerelease":$prerelease,"body":"Novità","assets":[${assets.joinToString()}],"extra":1}"""
    }

    @Test
    fun newerReleaseWithApkIsOffered() = runBlocking {
        server.enqueue(MockResponse().setBody(release("v0.3.0")))
        val update = updater.findUpdate("0.2.0")!!
        assertEquals("0.3.0", update.version)
        assertEquals(server.url("/app.apk").toString(), update.apkUrl)
        assertEquals("Novità", update.notes)
        assertEquals("application/vnd.github+json", server.takeRequest().getHeader("Accept"))
    }

    @Test
    fun sameOlderOrApklessReleasesAreIgnored() = runBlocking {
        server.enqueue(MockResponse().setBody(release("v0.2.0")))
        server.enqueue(MockResponse().setBody(release("v0.1.9")))
        server.enqueue(MockResponse().setBody(release("v0.3.0", apk = null)))
        server.enqueue(MockResponse().setBody(release("v0.3.0", prerelease = true)))
        server.enqueue(MockResponse().setResponseCode(404))
        repeat(5) { assertNull(updater.findUpdate("0.2.0")) }
    }

    @Test
    fun downloadWritesTheApk() = runBlocking {
        server.enqueue(MockResponse().setBody(release("v0.3.0")))
        server.enqueue(MockResponse().setBody(Buffer().write(ByteArray(5) { it.toByte() })))
        val update = updater.findUpdate("0.2.0")!!
        var last = 0f
        val apk = updater.download(update, temp.newFolder("updates")) { last = it }
        assertEquals("SoundBoardKing-0.3.0.apk", apk.name)
        assertEquals(5L, apk.length())
        assertEquals(1f, last)
    }

    @Test
    fun versionsCompareNumberByNumber() {
        assertTrue(AppUpdater.isNewer("0.10.0", "0.9.3"))
        assertTrue(AppUpdater.isNewer("1.0", "0.99.99"))
        assertTrue(AppUpdater.isNewer("0.2.1", "0.2.0"))
        assertFalse(AppUpdater.isNewer("0.2.0", "0.2.0"))
        assertFalse(AppUpdater.isNewer("0.2.0-beta", "0.2.0"))
        assertFalse(AppUpdater.isNewer("0.1.9", "0.2.0"))
    }
}
