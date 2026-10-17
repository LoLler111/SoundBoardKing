package com.soundboardking.remote.data

import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.SocketTimeoutException

data class DiscoveredPc(val host: String, val reply: DiscoveryReply)

/** Finds PCs running SoundBoardKing on the local network with a UDP broadcast. */
object Discovery {
    private const val REQUEST = "SBK_DISCOVER_V1"

    suspend fun find(port: Int = DEFAULT_PORT, durationMs: Long = 1500): List<DiscoveredPc> = withContext(Dispatchers.IO) {
        val found = linkedMapOf<String, DiscoveredPc>()
        DatagramSocket().use { socket ->
            socket.broadcast = true
            socket.soTimeout = 250
            val request = REQUEST.toByteArray(Charsets.US_ASCII)
            // Twice, in case the first packet is lost while the Wi-Fi radio wakes up.
            repeat(2) {
                socket.send(DatagramPacket(request, request.size, InetAddress.getByName("255.255.255.255"), port))
            }

            val buffer = ByteArray(2048)
            val deadline = System.currentTimeMillis() + durationMs
            while (System.currentTimeMillis() < deadline) {
                val packet = DatagramPacket(buffer, buffer.size)
                try {
                    socket.receive(packet)
                } catch (_: SocketTimeoutException) {
                    continue
                }
                val text = String(packet.data, 0, packet.length, Charsets.UTF_8)
                val reply = runCatching { ProtocolJson.decodeFromString(DiscoveryReply.serializer(), text) }.getOrNull()
                if (reply?.service == "SoundBoardKing") {
                    found.putIfAbsent(reply.serverId, DiscoveredPc(packet.address.hostAddress ?: continue, reply))
                }
            }
        }
        found.values.toList()
    }
}
