// PcPresenceProbe.kt -- pure JVM presence probe, deliberately ZERO android.*
// imports (only java.net/java.io) so this file and its unit test
// (app/src/test/java/com/displaybridge/presence/PcPresenceProbeTest.kt) run
// as plain JVM code under `./gradlew testDebugUnitTest`, no Android
// framework stubs / Robolectric needed -- same reasoning as
// ControlSocketClient's use of java.net.Socket (see that file's session-3
// comment: java.net classes are real on the host JVM, only android.*
// classes are stubbed-and-throw under the unit test runner).
package com.displaybridge.presence

import java.io.IOException
import java.net.InetSocketAddress
import java.net.Socket
import java.net.SocketTimeoutException

/**
 * See docs/RCA-v2-android-connect-adb-reverse-lifecycle.md for the
 * false-positive trap this object exists to avoid: a bare TCP connect() to
 * 127.0.0.1:<controlPort> through an `adb reverse` tunnel ALWAYS succeeds
 * once the tunnel exists, because the *local* adbd daemon on the tablet
 * accepts the TCP handshake itself before ever forwarding anything to the
 * PC side. "connect succeeded" is therefore worthless as a presence signal
 * -- it is true whenever a tunnel exists, whether or not a PC is actually
 * on the other end of it.
 *
 * The real signal is what happens to the connection AFTER connect()
 * returns:
 *  - PC ABSENT: nothing is listening PC-side, so adbd's local relay has
 *    nowhere to forward to and closes the socket almost immediately -- the
 *    very next read() returns EOF (-1) fast.
 *  - PC PRESENT: DisplayBridge.Core.Control.ControlSocketServer accepts the
 *    connection and holds it open (it is a long-lived control channel, not
 *    request/response) without writing anything back until the client sends
 *    CAPS. Our probe never sends CAPS, so the first read() just blocks
 *    until our own soTimeout fires SocketTimeoutException -- that timeout
 *    (i.e. "the connection is alive and silent") IS the presence signal,
 *    not a failure.
 */
object PcPresenceProbe {

    enum class Presence { PRESENT, ABSENT }

    private const val DEFAULT_CONNECT_TIMEOUT_MS = 1000
    private const val DEFAULT_READ_TIMEOUT_MS = 500

    /**
     * Connects to [host]:[port], then tries to read one byte to
     * disambiguate "adb tunnel with nobody PC-side" from "PC actually
     * holding the control socket open". Always closes the socket before
     * returning. Never throws -- any failure classifies as [Presence.ABSENT].
     */
    fun probePcPresence(
        host: String,
        port: Int,
        connectTimeoutMs: Int = DEFAULT_CONNECT_TIMEOUT_MS,
        readTimeoutMs: Int = DEFAULT_READ_TIMEOUT_MS
    ): Presence {
        var socket: Socket? = null
        return try {
            socket = Socket()
            socket.connect(InetSocketAddress(host, port), connectTimeoutMs)
            socket.soTimeout = readTimeoutMs
            val firstByte = socket.getInputStream().read()
            if (firstByte == -1) {
                // EOF: adb's local relay closed the connection immediately
                // because there is no PC-side listener behind the tunnel.
                Presence.ABSENT
            } else {
                // A real byte arrived before our soTimeout fired -- not the
                // expected wire behavior (the server stays silent until we
                // send CAPS), but any live data still proves something
                // PC-side is connected and talking.
                Presence.PRESENT
            }
        } catch (e: SocketTimeoutException) {
            // No EOF within readTimeoutMs: the server accepted the connect
            // and is holding it open without writing anything -- that
            // silence is the presence signal (see class doc above).
            Presence.PRESENT
        } catch (e: IOException) {
            // connect() itself failed (connection refused / host
            // unreachable / no tunnel at all) -- no PC reachable.
            Presence.ABSENT
        } finally {
            try {
                socket?.close()
            } catch (e: IOException) {
                // best-effort cleanup, nothing to do if close() itself fails
            }
        }
    }
}
