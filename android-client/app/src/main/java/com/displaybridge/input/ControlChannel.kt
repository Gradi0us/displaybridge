package com.displaybridge.input

/**
 * Minimal abstraction over "send framed bytes on the control socket
 * (port 29501)". Deliberately does NOT own socket connect/reconnect
 * lifecycle here — that belongs to the M4 connection module. This
 * package only needs a place to hand off already-framed bytes.
 *
 * Wire it up by implementing this against whatever control-socket
 * client class the M4 connection work lands (e.g. a thin adapter over
 * an OutputStream). Keeping it as an interface also makes
 * TouchCaptureView trivially unit-testable without a real socket.
 */
fun interface ControlChannel {
    fun send(framedBytes: ByteArray)
}
