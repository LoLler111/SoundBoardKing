package com.soundboardking.remote.data

import java.net.URLDecoder

/** Content of the PC's pairing QR: sbk://pair?h=ip1,ip2&p=port&c=code&n=name */
data class PairingLink(
    val hosts: List<String>,
    val port: Int,
    val code: String,
    val name: String,
) {
    companion object {
        private const val PREFIX = "sbk://pair?"

        fun parse(text: String): PairingLink? {
            if (!text.startsWith(PREFIX, ignoreCase = true)) return null
            val query = text.substring(PREFIX.length)
                .split('&')
                .mapNotNull { part ->
                    val index = part.indexOf('=')
                    if (index <= 0) null
                    else part.substring(0, index) to URLDecoder.decode(part.substring(index + 1), "UTF-8")
                }
                .toMap()

            val hosts = query["h"].orEmpty().split(',').map { it.trim() }.filter { it.isNotEmpty() }
            val port = query["p"]?.toIntOrNull()
            val code = query["c"]?.filter { it.isDigit() }
            if (hosts.isEmpty() || port == null || port !in 1..65535 || code?.length != 6) return null
            return PairingLink(hosts, port, code, query["n"].orEmpty())
        }
    }
}

/** "192.168.1.10" or "192.168.1.10:8765" typed by hand. */
fun parseHostAndPort(text: String, defaultPort: Int = DEFAULT_PORT): Pair<String, Int>? {
    val trimmed = text.trim().removePrefix("http://").trimEnd('/')
    if (trimmed.isEmpty()) return null
    val colon = trimmed.lastIndexOf(':')
    if (colon < 0) return trimmed to defaultPort
    val port = trimmed.substring(colon + 1).toIntOrNull()?.takeIf { it in 1..65535 } ?: return null
    val host = trimmed.substring(0, colon)
    return if (host.isEmpty()) null else host to port
}

const val DEFAULT_PORT = 8765
