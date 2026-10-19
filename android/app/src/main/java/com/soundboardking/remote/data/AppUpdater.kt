package com.soundboardking.remote.data

import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable
import kotlinx.serialization.json.Json
import okhttp3.OkHttpClient
import okhttp3.Request
import java.io.File
import java.io.IOException
import java.util.concurrent.TimeUnit

/** A GitHub release newer than this app, with the APK attached to it. */
data class AppRelease(val version: String, val apkUrl: String, val apkSize: Long, val notes: String)

/**
 * Finds the latest release of the public repository and downloads its APK. The repository is the
 * same one the Windows app updates from, and both carry the same version number.
 */
class AppUpdater(
    private val latestReleaseUrl: String = LATEST_RELEASE_URL,
    private val client: OkHttpClient = updateClient,
) {
    /** The latest release when it is newer than [currentVersion] and has an APK; otherwise null. */
    suspend fun findUpdate(currentVersion: String): AppRelease? = withContext(Dispatchers.IO) {
        val request = Request.Builder()
            .url(latestReleaseUrl)
            .header("Accept", "application/vnd.github+json")
            .build()
        client.newCall(request).execute().use { response ->
            // 404: no release published yet.
            if (response.code == 404) return@withContext null
            if (!response.isSuccessful) throw IOException("GitHub ha risposto ${response.code}.")
            val release = json.decodeFromString(GithubRelease.serializer(), response.body!!.string())
            val version = release.tagName.removePrefix("v")
            val apk = release.assets.firstOrNull { it.name.endsWith(".apk", ignoreCase = true) }
            if (release.draft || release.prerelease || apk == null || !isNewer(version, currentVersion)) null
            else AppRelease(version, apk.downloadUrl, apk.size, release.body.orEmpty())
        }
    }

    /** Downloads the APK into [folder] (emptied first), reporting progress from 0 to 1. */
    suspend fun download(release: AppRelease, folder: File, onProgress: (Float) -> Unit): File = withContext(Dispatchers.IO) {
        folder.deleteRecursively()
        folder.mkdirs()
        val target = File(folder, "SoundBoardKing-${release.version}.apk")
        val partial = File(folder, target.name + ".part")
        client.newCall(Request.Builder().url(release.apkUrl).build()).execute().use { response ->
            if (!response.isSuccessful) throw IOException("Download non riuscito (${response.code}).")
            val body = response.body!!
            val total = body.contentLength().takeIf { it > 0 } ?: release.apkSize
            body.byteStream().use { input ->
                partial.outputStream().use { output ->
                    val buffer = ByteArray(64 * 1024)
                    var done = 0L
                    while (true) {
                        val read = input.read(buffer)
                        if (read < 0) break
                        output.write(buffer, 0, read)
                        done += read
                        if (total > 0) onProgress((done.toFloat() / total).coerceIn(0f, 1f))
                    }
                }
            }
        }
        if (!partial.renameTo(target)) throw IOException("Impossibile salvare l'aggiornamento.")
        target
    }

    @Serializable
    private data class GithubRelease(
        @SerialName("tag_name") val tagName: String,
        val draft: Boolean = false,
        val prerelease: Boolean = false,
        val body: String? = null,
        val assets: List<GithubAsset> = emptyList(),
    )

    @Serializable
    private data class GithubAsset(
        val name: String,
        val size: Long = 0,
        @SerialName("browser_download_url") val downloadUrl: String,
    )

    companion object {
        const val REPOSITORY = "LoLler111/SoundBoardKing"
        const val LATEST_RELEASE_URL = "https://api.github.com/repos/$REPOSITORY/releases/latest"

        private val json = Json { ignoreUnknownKeys = true }

        // Release downloads redirect to GitHub's file host; okhttp follows redirects by default.
        private val updateClient = OkHttpClient.Builder()
            .connectTimeout(15, TimeUnit.SECONDS)
            .readTimeout(60, TimeUnit.SECONDS)
            .build()

        /** Compares dotted versions number by number: "0.10.0" is newer than "0.9.3". */
        fun isNewer(candidate: String, current: String): Boolean {
            val a = parts(candidate)
            val b = parts(current)
            for (i in 0 until maxOf(a.size, b.size)) {
                val diff = a.getOrElse(i) { 0 } - b.getOrElse(i) { 0 }
                if (diff != 0) return diff > 0
            }
            return false
        }

        // "1.2.3-beta" -> [1, 2, 3]; anything after a '-' or '+' is ignored.
        private fun parts(version: String): List<Int> =
            version.substringBefore('-').substringBefore('+').split('.').map { it.toIntOrNull() ?: 0 }
    }
}
