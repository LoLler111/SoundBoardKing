package com.soundboardking.remote.data

import kotlinx.serialization.Serializable
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.contentOrNull
import kotlinx.serialization.json.intOrNull
import kotlinx.serialization.json.doubleOrNull
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

/** One of the sounds playing at the same time; [id] stops just this one. */
@Serializable
data class PlayingDto(
    val id: String = "",
    val soundId: String? = null,
    val positionMs: Long = 0,
    val durationMs: Long = 0,
    val paused: Boolean = false,
)

/**
 * [state] is "stopped", "playing" or "paused" (all sounds pause together). [soundId] and the times
 * describe the most recent sound; [playing] lists every sound, oldest first.
 */
@Serializable
data class PlaybackDto(
    val state: String = "stopped",
    val soundId: String? = null,
    val positionMs: Long = 0,
    val durationMs: Long = 0,
    val playing: List<PlayingDto> = emptyList(),
) {
    val isActive get() = state != "stopped"
    val isPaused get() = state == "paused"

    /** Every sound playing; a PC with an older version only reports the latest, without an id. */
    val sounds: List<PlayingDto>
        get() = playing.ifEmpty {
            if (isActive) listOf(PlayingDto("", soundId, positionMs, durationMs)) else emptyList()
        }
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
    /** Wire name of the voice effect; PCs without effects never send it. */
    val effect: String = "none",
    /** Effect strength 0-100. */
    val effectAmount: Int = 70,
    /** Playback speed of every sound, like a record, 0.5-2. */
    val playbackSpeed: Double = 1.0,
    /** "sounds", "voice", "both" or "off". */
    val effectTarget: String = "sounds",
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
    data class Effect(val effect: String, val amount: Int, val speed: Double = 1.0, val target: String = "sounds") : ServerEvent
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
                "effect" -> Effect(json["effect"]?.jsonPrimitive?.contentOrNull ?: "none", json["amount"]?.jsonPrimitive?.intOrNull ?: 70, json["speed"]?.jsonPrimitive?.doubleOrNull ?: 1.0, json["target"]?.jsonPrimitive?.contentOrNull ?: "sounds")
                "error" -> Error(json["message"]?.jsonPrimitive?.contentOrNull ?: "Errore sconosciuto")
                else -> null
            }
        }
    }
}

/** Where a sound goes on the PC; [wire] is the protocol's `target`, absent for the usual. */
enum class PlayTarget(val wire: String?) {
    Default(null),

    /** Preview: only in the PC's headphones, never in the microphone. */
    Speakers("speakers"),

    /** Only in the microphone, even when it is switched off. */
    Mic("mic"),
}

/** Voice effects the PC can play the sounds through; [wire] is the protocol name. */
enum class SoundEffect(val wire: String, val label: String) {
    None("none", "Nessun effetto"),
    Chipmunk("chipmunk", "Scoiattolo"),
    Deep("deep", "Voce profonda"),
    Robot("robot", "Robot"),
    Radio("radio", "Radio"),
    Echo("echo", "Eco"),
    ;

    companion object {
        fun fromWire(wire: String?): SoundEffect = entries.firstOrNull { it.wire == wire } ?: None
    }
}

/** What the voice effect is applied to. */
enum class EffectTarget(val wire: String, val label: String) {
    Sounds("sounds", "Solo suoni"),
    Voice("voice", "Solo microfono"),
    Both("both", "Suoni e microfono"),
    Off("off", "Nessuno"),
    ;

    companion object {
        fun fromWire(wire: String?): EffectTarget = entries.firstOrNull { it.wire == wire } ?: Sounds
    }
}
