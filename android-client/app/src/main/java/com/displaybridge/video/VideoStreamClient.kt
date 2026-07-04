// M1.2/M1.3: TCP client that connects to the PC host's video socket
// (reached via `adb forward tcp:29500 tcp:29500`, per
// CONTEXT-BRIEF-v2-m1-m3-m4-tasks.md §6 M1.3) and streams decoded
// VideoFrame objects (see VideoFrameParser.kt) to a listener.
//
// This is deliberately separate from the control-socket protocol in
// com.displaybridge.protocol.generated -- the video socket is a raw,
// not-type-prefixed byte stream per schema.yaml's "Video Frame Header",
// while the generated MessageFraming/WireIO classes serialize the
// type-prefixed control messages (CAPS/CONFIG/STATS/...). We only import
// from that package, never modify it (M0.4 owns codegen).
package com.displaybridge.video

import android.util.Log
import java.io.IOException
import java.net.InetSocketAddress
import java.net.Socket
import java.net.SocketTimeoutException
import java.util.concurrent.atomic.AtomicBoolean

/** Callback surface for consumers (the decoder) of the video stream. */
interface VideoStreamListener {
    /** Called once the TCP connection to the PC host succeeds. */
    fun onConnected() {}

    /** Called for every fully-assembled frame, on the reader thread. */
    fun onFrame(frame: VideoFrame)

    /**
     * Called when the connection drops or a connect attempt fails.
     * [willRetry] indicates the client is about to attempt a reconnect
     * (per [VideoStreamClient]'s retry policy) rather than giving up.
     */
    fun onDisconnected(cause: Throwable?, willRetry: Boolean) {}
}

/**
 * Default host/port for the local PC-host video socket, reached through
 * `adb forward tcp:29500 tcp:29500` per the M1 task brief. Overridable via
 * constructor or Activity Intent extra (see VideoDecoderActivity).
 */
const val DEFAULT_VIDEO_STREAM_PORT = 29500
const val DEFAULT_VIDEO_STREAM_HOST = "127.0.0.1"

/**
 * Blocking TCP reader that runs its own background thread. Not an
 * Android Service by itself -- VideoStreamService wraps this to survive
 * screen-off on HONOR MagicOS (see CONTEXT-BRIEF §5, §M4.5).
 *
 * Connection-refused (server not up yet) is treated as a normal,
 * retryable condition -- it must never crash the caller; see [start]'s
 * retry loop and [VideoDecoderActivity]'s expectation that launching
 * before the PC host is ready should not crash.
 */
class VideoStreamClient(
    private val host: String = DEFAULT_VIDEO_STREAM_HOST,
    private val port: Int = DEFAULT_VIDEO_STREAM_PORT,
    private val listener: VideoStreamListener,
    private val retryDelayMs: Long = 1000L,
    private val connectTimeoutMs: Int = 3000,
    private val socketReadTimeoutMs: Int = 5000
) {
    companion object {
        private const val TAG = "VideoStreamClient"
        private const val READ_CHUNK_SIZE = 64 * 1024
    }

    private val running = AtomicBoolean(false)
    private var thread: Thread? = null
    @Volatile private var socket: Socket? = null

    /** Starts the background connect+read loop. Idempotent while running. */
    fun start() {
        if (!running.compareAndSet(false, true)) {
            Log.w(TAG, "start() called while already running -- ignoring")
            return
        }
        thread = Thread({ runLoop() }, "VideoStreamClient").also { it.start() }
    }

    /** Stops the client and closes the socket. Safe to call multiple times. */
    fun stop() {
        running.set(false)
        closeSocketQuietly()
        thread?.interrupt()
        thread = null
    }

    private fun runLoop() {
        val parser = VideoFrameParser()
        val buffer = ByteArray(READ_CHUNK_SIZE)

        while (running.get()) {
            try {
                Log.i(TAG, "Connecting to $host:$port ...")
                val sock = Socket()
                socket = sock
                sock.connect(InetSocketAddress(host, port), connectTimeoutMs)
                sock.soTimeout = socketReadTimeoutMs
                sock.tcpNoDelay = true // latency-first per settings catalog §3.2 "Priority"
                Log.i(TAG, "Connected to $host:$port")
                listener.onConnected()

                val input = sock.getInputStream()
                while (running.get()) {
                    val n = try {
                        input.read(buffer)
                    } catch (e: SocketTimeoutException) {
                        // No data within timeout -- normal while PC is idle/paused;
                        // loop again rather than treating it as a fatal error.
                        continue
                    }
                    if (n < 0) {
                        throw IOException("Server closed connection (EOF)")
                    }
                    if (n == 0) continue
                    val frames = parser.feed(buffer, 0, n)
                    for (frame in frames) listener.onFrame(frame)
                }
            } catch (e: IOException) {
                Log.w(TAG, "Video stream connection error: ${e.message}")
                val willRetry = running.get()
                listener.onDisconnected(e, willRetry)
            } catch (e: IllegalArgumentException) {
                // VideoFrameParser desync guard -- reconnect fresh rather than
                // trying to resync mid-stream.
                Log.e(TAG, "Frame parse error, reconnecting: ${e.message}", e)
                listener.onDisconnected(e, running.get())
            } finally {
                closeSocketQuietly()
            }

            if (running.get()) {
                try {
                    Thread.sleep(retryDelayMs)
                } catch (ie: InterruptedException) {
                    Thread.currentThread().interrupt()
                    break
                }
            }
        }
        Log.i(TAG, "VideoStreamClient loop exiting")
    }

    private fun closeSocketQuietly() {
        try {
            socket?.close()
        } catch (e: IOException) {
            // ignore -- best-effort cleanup
        }
        socket = null
    }
}
