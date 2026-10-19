package com.soundboardking.remote.data

import android.content.ActivityNotFoundException
import android.content.Context
import android.content.Intent
import android.net.Uri
import android.provider.Settings
import androidx.core.content.FileProvider
import java.io.File

/** Opens Android's package installer on a downloaded APK. */
object ApkInstaller {
    /** Where [AppUpdater] puts the APK; must match res/xml/update_paths.xml. */
    fun updatesFolder(context: Context) = File(context.cacheDir, "updates")

    /** False until the user allows this app to install apps (asked once, in the system settings). */
    fun canInstall(context: Context): Boolean = context.packageManager.canRequestPackageInstalls()

    /** Opens the system page where the user allows installing from this app. */
    fun openInstallPermission(context: Context) {
        val intent = Intent(Settings.ACTION_MANAGE_UNKNOWN_APP_SOURCES, Uri.parse("package:${context.packageName}"))
            .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
        context.startActivity(intent)
    }

    /** Returns false when no installer could be opened. */
    fun install(context: Context, apk: File): Boolean {
        val uri = FileProvider.getUriForFile(context, "${context.packageName}.updates", apk)
        return try {
            context.startActivity(installIntent(uri))
            true
        } catch (e: ActivityNotFoundException) {
            false
        }
    }

    /** The installer reads the APK through [uri], a content:// address from the FileProvider. */
    fun installIntent(uri: Uri): Intent = Intent(Intent.ACTION_VIEW)
        .setDataAndType(uri, "application/vnd.android.package-archive")
        .addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_ACTIVITY_NEW_TASK)
}
