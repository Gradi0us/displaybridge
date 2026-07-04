// PcPresenceProbeTest.kt -- JVM unit tests for the presence probe against a
// REAL localhost ServerSocket (java.net is fully functional under the unit
// test runner, only android.* is stubbed -- same approach as
// MessagesRoundtripTest's socket-free style but here the socket IS the thing
// under test). Each scenario recreates the adb-reverse behaviors documented
// in PcPresenceProbe's class comment.
package com.displaybridge.presence

import org.junit.Assert.assertEquals
import org.junit.Test
import java.net.ServerSocket
import kotlin.concurrent.thread

class PcPresenceProbeTest {

    /** Nothing listening at all (no tunnel / refused) -> ABSENT. */
    @Test
    fun connectRefused_isAbsent() {
        // Grab a free port, then close the listener so connect() is refused.
        val port = ServerSocket(0).use { it.localPort }
        assertEquals(
            PcPresenceProbe.Presence.ABSENT,
            PcPresenceProbe.probePcPresence("127.0.0.1", port, connectTimeoutMs = 300, readTimeoutMs = 200)
        )
    }

    /**
     * Accept-then-immediate-close = adbd relay with no PC-side listener
     * (the false-positive case the probe exists to catch) -> ABSENT.
     */
    @Test
    fun acceptThenInstantClose_isAbsent() {
        ServerSocket(0).use { server ->
            val accepter = thread {
                try { server.accept().close() } catch (_: Exception) { }
            }
            val result = PcPresenceProbe.probePcPresence(
                "127.0.0.1", server.localPort, connectTimeoutMs = 500, readTimeoutMs = 400
            )
            accepter.join(2000)
            assertEquals(PcPresenceProbe.Presence.ABSENT, result)
        }
    }

    /**
     * Accept-and-hold-silently = ControlSocketServer waiting for CAPS
     * (the real "PC present" signature) -> PRESENT via read timeout.
     */
    @Test
    fun acceptAndHoldSilently_isPresent() {
        ServerSocket(0).use { server ->
            val held = mutableListOf<java.net.Socket>()
            val accepter = thread {
                try { held.add(server.accept()) } catch (_: Exception) { }
            }
            val result = PcPresenceProbe.probePcPresence(
                "127.0.0.1", server.localPort, connectTimeoutMs = 500, readTimeoutMs = 300
            )
            accepter.join(2000)
            held.forEach { runCatching { it.close() } }
            assertEquals(PcPresenceProbe.Presence.PRESENT, result)
        }
    }

    /** A server that writes a byte immediately is also PRESENT (live data). */
    @Test
    fun acceptAndWrite_isPresent() {
        ServerSocket(0).use { server ->
            val accepter = thread {
                try {
                    val s = server.accept()
                    s.getOutputStream().write(1)
                    s.getOutputStream().flush()
                    Thread.sleep(500)
                    s.close()
                } catch (_: Exception) { }
            }
            val result = PcPresenceProbe.probePcPresence(
                "127.0.0.1", server.localPort, connectTimeoutMs = 500, readTimeoutMs = 400
            )
            accepter.join(2000)
            assertEquals(PcPresenceProbe.Presence.PRESENT, result)
        }
    }
}
