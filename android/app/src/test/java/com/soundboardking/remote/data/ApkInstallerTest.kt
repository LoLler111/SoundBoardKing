package com.soundboardking.remote.data

import android.app.Application
import android.content.Intent
import android.net.Uri
import android.provider.Settings
import androidx.test.core.app.ApplicationProvider
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Assume.assumeFalse
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.Shadows.shadowOf
import org.robolectric.annotation.Config
import java.io.File

@RunWith(RobolectricTestRunner::class)
@Config(sdk = [35])
class ApkInstallerTest {
    private val context = ApplicationProvider.getApplicationContext<Application>()

    @Test
    fun installIntentHandsTheApkToThePackageInstaller() {
        val uri = Uri.parse("content://${context.packageName}.updates/updates/SoundBoardKing-0.3.0.apk")
        val intent = ApkInstaller.installIntent(uri)
        assertEquals(Intent.ACTION_VIEW, intent.action)
        assertEquals("application/vnd.android.package-archive", intent.type)
        assertEquals(uri, intent.data)
        assertTrue(intent.flags and Intent.FLAG_GRANT_READ_URI_PERMISSION != 0)
    }

    @Test
    fun installOpensThePackageInstallerThroughTheFileProvider() {
        // FileProvider checks paths with '/', so on a Windows PC the cache folder never matches.
        assumeFalse(File.separatorChar == '\\')
        val apk = ApkInstaller.updatesFolder(context).apply { mkdirs() }.resolve("SoundBoardKing-0.3.0.apk")
        apk.writeBytes(byteArrayOf(1, 2, 3))

        assertTrue(ApkInstaller.install(context, apk))

        val intent = shadowOf(context).nextStartedActivity
        assertEquals(Intent.ACTION_VIEW, intent.action)
        assertEquals("application/vnd.android.package-archive", intent.type)
        assertEquals("content", intent.data!!.scheme)
        assertEquals("${context.packageName}.updates", intent.data!!.authority)
        assertEquals("/updates/SoundBoardKing-0.3.0.apk", intent.data!!.path)
        assertTrue(intent.flags and Intent.FLAG_GRANT_READ_URI_PERMISSION != 0)
    }

    @Test
    fun permissionPageIsTheOneForThisApp() {
        ApkInstaller.openInstallPermission(context)

        val intent = shadowOf(context).nextStartedActivity
        assertEquals(Settings.ACTION_MANAGE_UNKNOWN_APP_SOURCES, intent.action)
        assertEquals("package:${context.packageName}", intent.dataString)
    }
}
