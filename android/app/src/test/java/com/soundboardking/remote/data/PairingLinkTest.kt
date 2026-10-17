package com.soundboardking.remote.data

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

class PairingLinkTest {
    @Test
    fun parsesTheLinkTheWindowsAppPutsInTheQr() {
        val link = PairingLink.parse("sbk://pair?h=192.168.1.10,10.0.0.5&p=8765&c=123456&n=PC%20SALOTTO")!!
        assertEquals(listOf("192.168.1.10", "10.0.0.5"), link.hosts)
        assertEquals(8765, link.port)
        assertEquals("123456", link.code)
        assertEquals("PC SALOTTO", link.name)
    }

    @Test
    fun rejectsOtherQrCodes() {
        assertNull(PairingLink.parse("https://example.com"))
        assertNull(PairingLink.parse("sbk://pair?h=&p=8765&c=123456"))
        assertNull(PairingLink.parse("sbk://pair?h=1.2.3.4&p=0&c=123456"))
        assertNull(PairingLink.parse("sbk://pair?h=1.2.3.4&p=8765&c=12345"))
    }

    @Test
    fun parsesTypedAddresses() {
        assertEquals("192.168.1.10" to DEFAULT_PORT, parseHostAndPort(" 192.168.1.10 "))
        assertEquals("192.168.1.10" to 9000, parseHostAndPort("http://192.168.1.10:9000/"))
        assertNull(parseHostAndPort("192.168.1.10:abc"))
        assertNull(parseHostAndPort(""))
    }
}
