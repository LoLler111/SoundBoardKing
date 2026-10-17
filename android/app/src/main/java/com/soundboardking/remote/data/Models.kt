package com.soundboardking.remote.data

import kotlinx.serialization.Serializable
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.contentOrNull
import kotlinx.serialization.json.jsonPrimitive
import kotlinx.serialization.json.longOrNull

// Wire format of docs/PROTOCOLLO_REMOTO.md (protocol 1).

const val PROTOCOL_VERSION = 1

val ProtocolJson = Json {
    ignoreUnknownKeys = true
    explicitNulls = false
}

@Serializable
data class SoundDto(
    val id: String,
    val title: String,
    val artist: String = "",
    val durationMs: Long = 0,
    val missing: Boolean = false,
    val hotkey: String? = null,
)

@Serializable
data class CategoryDto(
    val id: String,
    val name: String,
    val soundIds: List<String> = emptyList(),
    val children: List<CategoryDto> = emptyList(),
)

@Serializable
data class HotbarSlotDto(val page: Int, val slot: Int, val soundId: String)

@Serializable
data class HotbarDto(
    val pages: Int = 5,
    val slotsPerPage: Int = 20,
    val slots: List<HotbarSlotDto> = emptyList(),
)

/** [state] is "stopped", "playing" or "paused". */
@Serializable
data class PlaybackDto(
    val state: String = "stopped",
    val soundId: String? = null,
    val positionMs: Long = 0,
    val durationMs: Long = 0,
) {
    val isActive get() = state != "stopped"
    val isPaused get() = state == "paused"
}

/** [state] is "notInstalled", "idle", "active", "incompatible" or "accessDenied". */
@Serializable
data class MicDto(val state: String = "notInstalled", val enabled: Boolean = false)

@Serializable
data class StateDto(
    val serverId: String,
    val name: String,
    val protocol: Int,
    val libraryVersion: Long,
    val sounds: List<SoundDto> = emptyList(),
    val categories: List<CategoryDto> = emptyList(),
    val hotbar: HotbarDto = HotbarDto(),
    val playback: PlaybackDto = PlaybackDto(),
    val mic: MicDto = MicDto(),
)

@Serializable
data class InfoDto(val name: String, val protocol: Int, val serverId: String)

@Serializable
data class PairResponse(val token: String, val deviceId: String)

@Serializable
data class DiscoveryReply(
    val service: String,
    val protocol: Int,
    val name: String,
    val port: Int,
    val serverId: String,
)

/** Messages the PC pushes over the WebSocket. */
sealed interface ServerEvent {
    data class Hello(val libraryVersion: Long) : ServerEvent
    data class Playback(val playback: PlaybackDto) : ServerEvent
    data class LibraryChanged(val libraryVersion: Long) : ServerEvent
    data class Mic(val mic: MicDto) : ServerEvent
    data class Error(val message: String) : ServerEvent

    companion object {
        /** Returns null for messages this version does not understand. */
        fun parse(text: String): ServerEvent? {
            val json = runCatching { ProtocolJson.parseToJsonElement(text) as? JsonObject }.getOrNull() ?: return null
            fun long(name: String) = json[name]?.jsonPrimitive?.longOrNull ?: 0
            return when (json["type"]?.jsonPrimitive?.contentOrNull) {
                "hello" -> Hello(long("libraryVersion"))
                "libraryChanged" -> LibraryChanged(long("libraryVersion"))
                // The other fields of these events sit next to "type", in the DTO's shape.
                "playback" -> Playback(ProtocolJson.decodeFromJsonElement(PlaybackDto.serializer(), json))
                "mic" -> Mic(ProtocolJson.decodeFromJsonElement(MicDto.serializer(), json))
                "error" -> Error(json["message"]?.jsonPrimitive?.contentOrNull ?: "Errore sconosciuto")
                else -> null
            }
        }
    }
}
