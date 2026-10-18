package com.soundboardking.remote.data

import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.put
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody
import okhttp3.Response
import okhttp3.WebSocket
import okhttp3.WebSocketListener
import java.io.IOException
import java.util.concurrent.TimeUnit

/** The PC answered 401: the pairing was revoked or never existed. */
class UnauthorizedException : IOException("Il PC non riconosce più questo telefono.")

/** The pairing code was refused (wrong, expired or already used). */
class PairingRefusedException : IOException("Codice non valido o scaduto. Controlla quello mostrato sul PC.")

/** REST and WebSocket client for one PC. [baseUrl] is like "http://192.168.1.10:8765". */
class SoundBoardApi(
    private val client: OkHttpClient = defaultClient,
) {
    suspend fun info(baseUrl: String): InfoDto = get(baseUrl, "/api/info", token = null) {
        ProtocolJson.decodeFromString(InfoDto.serializer(), it)
    }

    suspend fun pair(baseUrl: String, code: String, deviceName: String): PairResponse = withContext(Dispatchers.IO) {
        val body = buildJsonObject {
            put("code", code)
            put("deviceName", deviceName)
        }.toString().toRequestBody(JSON)
        client.newCall(Request.Builder().url("$baseUrl/api/pair").post(body).build()).execute().use { response ->
            if (response.code == 403) throw PairingRefusedException()
            ProtocolJson.decodeFromString(PairResponse.serializer(), response.bodyOrThrow())
        }
    }

    suspend fun state(baseUrl: String, token: String): StateDto = get(baseUrl, "/api/state", token) {
        ProtocolJson.decodeFromString(StateDto.serializer(), it)
    }

    /** Opens the event channel. Commands are sent with [sendCommand] on the returned socket. */
    fun connect(baseUrl: String, token: String, listener: Listener): WebSocket {
        val url = baseUrl.replaceFirst("http", "ws") + "/ws?token=$token"
        return client.newWebSocket(Request.Builder().url(url).build(), object : WebSocketListener() {
            override fun onOpen(webSocket: WebSocket, response: Response) = listener.onOpen()

            override fun onMessage(webSocket: WebSocket, text: String) {
                ServerEvent.parse(text)?.let(listener::onEvent)
            }

            override fun onFailure(webSocket: WebSocket, t: Throwable, response: Response?) =
                listener.onClosed(if (response?.code == 401) UnauthorizedException() else t)

            override fun onClosed(webSocket: WebSocket, code: Int, reason: String) = listener.onClosed(null)
        })
    }

    interface Listener {
        fun onOpen()
        fun onEvent(event: ServerEvent)

        /** [error] is null for a normal close. */
        fun onClosed(error: Throwable?)
    }

    private suspend fun <T> get(baseUrl: String, path: String, token: String?, parse: (String) -> T): T =
        withContext(Dispatchers.IO) {
            val request = Request.Builder().url(baseUrl + path)
            if (token != null) request.header("Authorization", "Bearer $token")
            client.newCall(request.build()).execute().use { parse(it.bodyOrThrow()) }
        }

    private fun Response.bodyOrThrow(): String {
        if (code == 401) throw UnauthorizedException()
        if (!isSuccessful) throw IOException("Il PC ha risposto $code")
        return body?.string() ?: throw IOException("Risposta vuota dal PC")
    }

    companion object {
        private val JSON = "application/json".toMediaType()

        val defaultClient: OkHttpClient = OkHttpClient.Builder()
            .connectTimeout(3, TimeUnit.SECONDS)
            .readTimeout(10, TimeUnit.SECONDS)
            .pingInterval(15, TimeUnit.SECONDS)
            .build()

        fun baseUrl(host: String, port: Int) = "http://$host:$port"

        fun playCommand(soundId: String, target: PlayTarget = PlayTarget.Default) = buildJsonObject {
            put("type", "play")
            put("soundId", soundId)
            target.wire?.let { put("target", it) }
        }.toString()

        fun simpleCommand(type: String) = buildJsonObject { put("type", type) }.toString()

        fun stopOneCommand(playbackId: String) = buildJsonObject {
            put("type", "stopOne")
            put("playbackId", playbackId)
        }.toString()

        fun micCommand(enabled: Boolean) = buildJsonObject {
            put("type", "setMic")
            put("enabled", enabled)
        }.toString()
    }
}
