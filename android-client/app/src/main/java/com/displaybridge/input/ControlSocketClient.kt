// ControlSocketClient.kt — wiring-E2E (session 3). Session 2 left the
// control socket (port 29501) as a protocol *definition* only on the
// Android side too -- ControlChannel.kt's own doc comment says "Wire it
// up by implementing this against whatever control-socket client class
// the M4 connection work lands", and that class never got written. This
// is that class: connects to the PC host's control port, performs the
// CAPS -> CONFIG handshake (sending real tablet caps read via the
// Display API, not a placeholder), then keeps reading CONFIG_UPDATE/
// MODE_CHANGE/etc from the PC while implementing ControlChannel so
// TouchCaptureView (M3) can send TOUCH_EVENT/TOUCH_BATCH straight
// through the same socket.
package com.displaybridge.input

import android.content.Context
import android.hardware.display.DisplayManager
import android.os.Build
import android.util.Log
import android.view.Display
import com.displaybridge.protocol.generated.CapsMessage
import com.displaybridge.protocol.generated.ConfigMessage
import com.displaybridge.protocol.generated.ConfigUpdateMessage
import com.displaybridge.protocol.generated.MessageFraming
import com.displaybridge.protocol.generated.ModeChangeMessage
import com.displaybridge.protocol.generated.ProtocolMessage
import com.displaybridge.settings.SettingsPrefs
import java.io.DataInputStream
import java.io.IOException
import java.net.InetSocketAddress
import java.net.Socket
import java.util.concurrent.LinkedBlockingQueue
import java.util.concurrent.atomic.AtomicBoolean

const val DEFAULT_CONTROL_PORT = 29501

/** Callback surface for the handshake result + any PC-pushed message the Activity may care about. */
interface ControlSocketListener {
    /** Fired once CONFIG is received in reply to our CAPS -- decoder should (re)configure to match. */
    fun onConfig(config: ConfigMessage) {}
    fun onConfigUpdate(update: ConfigUpdateMessage) {}
    fun onModeChange(modeChange: ModeChangeMessage) {}
    fun onControlDisconnected(cause: Throwable?, willRetry: Boolean) {}
}

/**
 * Connects to the PC host's control socket (default 29501, reached via
 * `adb forward tcp:29501 tcp:29501` the same way the video port is),
 * sends CAPS built from real Display API values, and implements
 * [ControlChannel] so [com.displaybridge.input.TouchCaptureView] can push
 * TOUCH_EVENT/TOUCH_BATCH frames through the same connection.
 */
class ControlSocketClient(
    private val context: Context,
    private val host: String = "127.0.0.1",
    private val port: Int = DEFAULT_CONTROL_PORT,
    private val listener: ControlSocketListener,
    private val retryDelayMs: Long = 1000L,
    private val connectTimeoutMs: Int = 3000
) : ControlChannel {

    companion object {
        private const val TAG = "ControlSocketClient"

        // CodecId per schema.yaml Config.codec doc: 0=H264, 1=HEVC.
        private const val CODEC_H264 = 0
        private const val CODEC_HEVC = 1
    }

    private val running = AtomicBoolean(false)
    private var thread: Thread? = null
    @Volatile private var socket: Socket? = null
    @Volatile private var outputStream: java.io.OutputStream? = null
    private val writeLock = Any()

    // Real-device bug (session 5, found only once run on an actual tablet --
    // fake-device/unit tests never caught it because StrictMode's
    // NetworkOnMainThreadException only fires on a real Android runtime):
    // TouchCaptureView.onTouchEvent() runs on the UI thread and used to call
    // send() -> outputStream.write() synchronously, i.e. a blocking socket
    // write on the main thread. Android throws NetworkOnMainThreadException
    // the moment that happens and crashes the whole Activity on the very
    // first touch. Fix: send() now only enqueues bytes; a single dedicated
    // writer thread drains the queue and does the actual blocking write.
    private val writeQueue = LinkedBlockingQueue<ByteArray>()
    private var writerThread: Thread? = null

    fun start() {
        if (!running.compareAndSet(false, true)) {
            Log.w(TAG, "start() called while already running -- ignoring")
            return
        }
        thread = Thread({ runLoop() }, "ControlSocketClient").also { it.start() }
        writerThread = Thread({ writerLoop() }, "ControlSocketClient-Writer").also { it.start() }
    }

    fun stop() {
        running.set(false)
        closeSocketQuietly()
        thread?.interrupt()
        thread = null
        writerThread?.interrupt()
        writerThread = null
        writeQueue.clear()
    }

    /** Drains [writeQueue] on a background thread so callers (incl. the UI thread) never block on socket I/O. */
    private fun writerLoop() {
        while (running.get()) {
            val bytes = try {
                writeQueue.take()
            } catch (ie: InterruptedException) {
                Thread.currentThread().interrupt()
                return
            }
            synchronized(writeLock) {
                try {
                    outputStream?.write(bytes)
                    outputStream?.flush()
                } catch (e: IOException) {
                    Log.w(TAG, "Failed to send control message (dropped): ${e.message}")
                }
            }
        }
    }

    private fun runLoop() {
        while (running.get()) {
            try {
                Log.i(TAG, "Connecting to control socket $host:$port ...")
                val sock = Socket()
                socket = sock
                sock.connect(InetSocketAddress(host, port), connectTimeoutMs)
                sock.tcpNoDelay = true
                outputStream = sock.getOutputStream()
                Log.i(TAG, "Connected to control socket $host:$port")

                sendCaps()

                val input = DataInputStream(sock.getInputStream())
                while (running.get()) {
                    val message = readOneMessage(input) ?: break
                    handleMessage(message)
                }
                throw IOException("Control socket read loop exited (server closed connection)")
            } catch (e: IOException) {
                Log.w(TAG, "Control socket error: ${e.message}")
                val willRetry = running.get()
                listener.onControlDisconnected(e, willRetry)
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
        Log.i(TAG, "ControlSocketClient loop exiting")
    }

    /**
     * Reads one [u8 type][u16 length][payload] framed message per
     * schema.yaml's control framing convention, then hands the whole
     * envelope to MessageFraming.readFramed (which re-parses the header --
     * slightly redundant but keeps this file from duplicating the
     * generated codec's header format). Returns null on clean EOF.
     */
    private fun readOneMessage(input: DataInputStream): ProtocolMessage? {
        val typeByte = try {
            input.readUnsignedByte()
        } catch (e: java.io.EOFException) {
            return null
        }
        val lengthHi = input.readUnsignedByte()
        val lengthLo = input.readUnsignedByte()
        // WireIO length is little-endian u16 (see schema.yaml byte_order) --
        // DataInputStream.readUnsignedByte() reads raw bytes individually so
        // we reconstruct LE ourselves rather than using readUnsignedShort
        // (which is big-endian).
        val length = lengthHi or (lengthLo shl 8)
        val payload = ByteArray(length)
        input.readFully(payload)

        val envelope = ByteArray(3 + length)
        envelope[0] = typeByte.toByte()
        envelope[1] = lengthHi.toByte()
        envelope[2] = lengthLo.toByte()
        System.arraycopy(payload, 0, envelope, 3, length)
        return MessageFraming.readFramed(envelope)
    }

    private fun handleMessage(message: ProtocolMessage) {
        when (message) {
            is ConfigMessage -> {
                Log.i(TAG, "CONFIG received: codec=${message.codec} ${message.width}x${message.height}@${message.hz}Hz ${message.bitrateKbps}kbps")
                listener.onConfig(message)
            }
            is ConfigUpdateMessage -> listener.onConfigUpdate(message)
            is ModeChangeMessage -> listener.onModeChange(message)
            else -> Log.d(TAG, "Unhandled control message from PC: ${message.type}")
        }
    }

    /**
     * Builds CAPS from real Android Display API values (panel resolution,
     * refresh rates, DPI) -- replaces the PC-side hardcoded 2560x1600
     * DeviceCaps.Placeholder the moment this handshake completes (see
     * StreamingCoordinator.OnCapsReceived on the PC side).
     */
    private fun sendCaps() {
        val displayManager = context.getSystemService(Context.DISPLAY_SERVICE) as DisplayManager
        val display = displayManager.getDisplay(Display.DEFAULT_DISPLAY)

        // Session 18: read the PHYSICAL panel size via Display.getRealMetrics,
        // NOT context.resources.displayMetrics. The resources metrics reflect
        // the current WINDOW size, so with the activity in a freeform/floating
        // window (Honor "App Multiplier") CAPS reported the window size (e.g.
        // 1519x1517) and the PC resized the virtual display to match -- the
        // session-17 workaround pinned the manifest to fullscreen-only
        // (resizeableActivity=false) to dodge this, which the user has now
        // asked to undo ("khong bat buoc full man hinh"). Real metrics are
        // window-mode independent, so CAPS stays correct in any window mode
        // and the manifest restriction could be lifted.
        val metrics = android.util.DisplayMetrics()
        @Suppress("DEPRECATION") // deprecated API 31+, still functional; simplest window-mode-independent source
        display.getRealMetrics(metrics)
        // Streaming/VDD is landscape (activity is landscape-locked): if the
        // device happens to be held portrait when this runs, getRealMetrics
        // returns the rotated (swapped) size -- normalize to landscape so the
        // PC never configures a portrait virtual display.
        val width = maxOf(metrics.widthPixels, metrics.heightPixels)
        val height = minOf(metrics.widthPixels, metrics.heightPixels)
        val dpi = metrics.densityDpi

        val deviceSupportedHz: List<Int> = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.M) {
            display.supportedModes
                .map { it.refreshRate.toInt() }
                .distinct()
                .filter { it in 1..120 } // 144Hz dropped from the system per decisions log 2026-07-03
                .sorted()
                .ifEmpty { listOf(60) }
        } else {
            listOf(display.refreshRate.toInt().coerceIn(1, 120))
        }

        // CodecId values the tablet's MediaCodec can decode -- MIMETYPE_VIDEO_AVC
        // is always attempted by VideoDecoderActivity; report HEVC too if any
        // installed decoder claims support, so the PC can prefer HEVC (Auto
        // codec setting default per catalog §3.2).
        val deviceSupportedCodecs = mutableListOf(CODEC_H264)
        if (deviceSupportsHevcDecode()) deviceSupportedCodecs.add(CODEC_HEVC)

        // Floating-button Settings (MobileSettingsDialog): these are USER
        // PREFERENCES, distinct from the DEVICE CAPABILITY lists above.
        // Rather than adding a new protocol field for "user wants X", we
        // filter the capability list down to just the user's choice before
        // sending CAPS -- the PC's existing ChooseHz/ChooseCodec selection
        // logic then has only one option to pick, achieving the same
        // outcome with zero protocol/schema changes (KISS). If the user's
        // choice isn't actually one the device supports (shouldn't happen,
        // dialog only offers supported-looking values, but guard anyway),
        // fall back to the unfiltered device list rather than lying about
        // capability.
        val fpsCapPref = SettingsPrefs.getFpsCap(context)
        val supportedHz = if (fpsCapPref != SettingsPrefs.FPS_CAP_UNSET && deviceSupportedHz.contains(fpsCapPref)) {
            listOf(fpsCapPref)
        } else {
            deviceSupportedHz
        }

        val encodePref = SettingsPrefs.getEncodePref(context)
        val supportedCodecs = if (encodePref != SettingsPrefs.ENCODE_PREF_AUTO && deviceSupportedCodecs.contains(encodePref)) {
            listOf(encodePref)
        } else {
            deviceSupportedCodecs
        }

        val maxTouchPoints = context.packageManager.let { pm ->
            when {
                pm.hasSystemFeature("android.hardware.touchscreen.multitouch.jazzhand") -> 10
                pm.hasSystemFeature("android.hardware.touchscreen.multitouch.distinct") -> 4
                pm.hasSystemFeature("android.hardware.touchscreen.multitouch") -> 2
                else -> 1
            }
        }

        val caps = CapsMessage(
            width = width,
            height = height,
            dpi = dpi,
            supportedHz = supportedHz,
            supportedCodecs = supportedCodecs,
            maxTouchPoints = maxTouchPoints
        )
        Log.i(TAG, "Sending CAPS: ${width}x${height}@${dpi}dpi hz=$supportedHz codecs=$supportedCodecs touch=$maxTouchPoints")
        send(MessageFraming.writeFramed(caps))
    }

    private fun deviceSupportsHevcDecode(): Boolean = try {
        val codecList = android.media.MediaCodecList(android.media.MediaCodecList.REGULAR_CODECS)
        codecList.codecInfos.any { info ->
            !info.isEncoder && info.supportedTypes.any { it.equals("video/hevc", ignoreCase = true) }
        }
    } catch (e: Exception) {
        false
    }

    /**
     * [ControlChannel] implementation -- lets TouchCaptureView push already-framed
     * bytes through this same connection. Non-blocking: enqueues onto [writeQueue]
     * for [writerLoop] to actually write, so calling this from the UI thread (which
     * is exactly what TouchCaptureView.onTouchEvent does) never risks
     * NetworkOnMainThreadException.
     */
    override fun send(framedBytes: ByteArray) {
        if (!running.get()) return
        writeQueue.offer(framedBytes)
    }

    private fun closeSocketQuietly() {
        try {
            socket?.close()
        } catch (e: IOException) {
            // ignore -- best-effort cleanup
        }
        socket = null
        outputStream = null
    }
}
