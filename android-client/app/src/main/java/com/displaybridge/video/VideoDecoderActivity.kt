// M1.2: fullscreen immersive Activity that decodes the incoming H.264
// stream (via MediaCodec, low-latency, decode-direct-to-Surface) and
// renders it on a SurfaceView. See CONTEXT-BRIEF-v2-m1-m3-m4-tasks.md
// §6 M1.2: "Kotlin: decoder setup, SurfaceView render, low-latency flag".
//
// Decode/render policy per task brief: drop-to-latest, no PTS-synced
// pacing -- releaseOutputBuffer(index, true) is called immediately so the
// newest decoded frame reaches the display as fast as possible, favoring
// latency over smoothness (matches Settings catalog §3.2 "Priority:
// Latency-first" default).
package com.displaybridge.video

import android.app.Activity
import android.content.Intent
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.Rect
import android.graphics.drawable.GradientDrawable
import android.media.MediaCodec
import android.media.MediaCodecInfo
import android.media.MediaFormat
import android.os.Build
import android.os.Bundle
import android.util.Log
import android.view.Gravity
import android.view.MotionEvent
import android.view.SurfaceHolder
import android.view.SurfaceView
import android.view.View
import android.view.WindowManager
import android.widget.FrameLayout
import android.widget.ImageButton
import android.widget.ImageView
import android.widget.LinearLayout
import android.widget.ProgressBar
import android.widget.TextView
import com.displaybridge.input.ControlSocketClient
import com.displaybridge.input.ControlSocketListener
import com.displaybridge.input.DEFAULT_CONTROL_PORT
import com.displaybridge.input.TouchCaptureView
import com.displaybridge.probe.R
import com.displaybridge.protocol.generated.ConfigMessage
import com.displaybridge.protocol.generated.ConfigUpdateMessage
import com.displaybridge.protocol.generated.ModeChangeMessage
import com.displaybridge.settings.MobileSettingsDialog
import kotlin.math.hypot

class VideoDecoderActivity : Activity(), SurfaceHolder.Callback, VideoStreamListener, ControlSocketListener {

    companion object {
        private const val TAG = "VideoDecoderActivity"

        // Session 12: codec is no longer hardcoded to H.264 -- the PC side
        // can now actually negotiate HEVC (see StreamingCoordinator.cs
        // ChooseCodec()/H264Encoder.cpp VideoCodecType, and
        // CaptureEncodeExports.cpp DisplayBridge_CaptureInitWithCodec).
        // `mimeType` (instance field below) tracks which one is active;
        // MIME_TYPE_H264 is also the initial fallback before the first
        // CONFIG/MODE_CHANGE tells us which codec the PC actually chose.
        private const val MIME_TYPE_H264 = MediaFormat.MIMETYPE_VIDEO_AVC
        private const val MIME_TYPE_HEVC = MediaFormat.MIMETYPE_VIDEO_HEVC

        /** Wire codec id per schema.yaml Config.codec / ModeChange.codec: 0=H264, 1=HEVC. */
        private fun mimeTypeForCodec(codec: Int): String = if (codec == 1) MIME_TYPE_HEVC else MIME_TYPE_H264

        // Touch-slop for the draggable floating settings button: total
        // ACTION_MOVE distance under this (dp) is still a tap; past it, the
        // gesture becomes a drag and no click fires on release.
        private const val DRAG_CLICK_SLOP_DP = 8
        const val EXTRA_HOST = "com.displaybridge.video.EXTRA_HOST"
        const val EXTRA_PORT = "com.displaybridge.video.EXTRA_PORT"
        const val EXTRA_CONTROL_PORT = "com.displaybridge.video.EXTRA_CONTROL_PORT"

        // Fallback dimensions used to init the decoder before the first
        // CSD/SPS-PPS is known. MediaCodec will adapt via
        // MediaCodec.INFO_OUTPUT_FORMAT_CHANGED once real SPS/PPS arrives
        // in the NAL stream (standard Android decoder behavior).
        private const val FALLBACK_WIDTH = 1920
        private const val FALLBACK_HEIGHT = 1080

        // --- R17 decision (2026-07-03) ---
        // SPS/PPS are carried IN-BAND inside the NAL bitstream (prepended
        // by the PC encoder -- see H264Encoder.cpp/.h
        // PrependSequenceHeaderIfKeyframe()), NOT as out-of-band CSD via
        // MediaFormat "csd-0"/"csd-1" keys. Deliberately do NOT set
        // csd-0/csd-1 on the MediaFormat passed to configure() below --
        // MediaCodec auto-parses SPS/PPS out of the first in-band NAL
        // units it dequeues (standard AVC decoder behavior once
        // INFO_OUTPUT_FORMAT_CHANGED fires). Setting csd-0/csd-1 here
        // would require a separate handshake message to carry the CSD
        // blob, which this project intentionally avoids.

        /** R16: cap consecutive auto-restarts so a persistently broken decoder doesn't spin forever. */
        private const val MAX_CONSECUTIVE_DECODER_RESTARTS = 5

        // Việc 4 (waiting screen) text templates. Kept as two separate
        // strings rather than one generic "waiting" message because we CAN
        // cheaply tell first-connect apart from mid-session disconnect via
        // hasReceivedFirstFrame -- no need for the KISS fallback the brief
        // allowed.
        private const val TEXT_WAITING_FIRST_CONNECT = "Đang chờ kết nối với PC..."
        private const val TEXT_WAITING_RECONNECT = "Mất kết nối, đang thử lại..."
    }

    private lateinit var surfaceView: SurfaceView
    private lateinit var touchCaptureView: TouchCaptureView
    private lateinit var waitingOverlay: FrameLayout
    private lateinit var waitingMessageText: TextView
    private lateinit var fpsOverlayText: TextView
    private lateinit var floatingSettingsButton: ImageButton
    private var decoder: MediaCodec? = null
    private var client: VideoStreamClient? = null
    private var controlClient: ControlSocketClient? = null
    @Volatile private var surfaceReady = false
    @Volatile private var decoderStarted = false

    // Session 12: which MIME type ensureDecoderStarted() should create the
    // decoder as -- updated from onConfig()/onModeChange() when the PC
    // reports config.codec/modeChange.codec (0=H264, 1=HEVC). Starts at
    // H264 as a safe fallback in the unlikely event a video frame arrives
    // before the control-socket CONFIG handshake completes.
    @Volatile private var mimeType: String = MIME_TYPE_H264

    // Việc 4: tracks whether ANY frame has ever been rendered this Activity
    // lifetime -- drives both hiding the waiting overlay on first frame and
    // picking the right message text if we later disconnect mid-session.
    @Volatile private var hasReceivedFirstFrame = false

    // R16 (risk register): MediaCodec.CodecException used to leave the
    // decoder permanently dead after the first error -- decoderStarted
    // stayed true but the underlying codec was unusable, so every
    // subsequent frame silently no-op'd forever with no picture recovery.
    // consecutiveRestarts resets to 0 on any successfully decoded frame.
    private var consecutiveRestarts = 0

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        makeFullscreenImmersive()

        // Wiring-E2E (session 3): overlay a transparent TouchCaptureView
        // (M3) on top of the mirroring SurfaceView (M1-Android) in the
        // same layout, per CONTEXT-BRIEF task brief "Việc 4". Video render
        // and touch capture are two independent views stacked in a
        // FrameLayout so neither module's file needs to touch the other.
        surfaceView = SurfaceView(this)
        touchCaptureView = TouchCaptureView(this)

        val root = FrameLayout(this)
        root.addView(surfaceView, FrameLayout.LayoutParams(FrameLayout.LayoutParams.MATCH_PARENT, FrameLayout.LayoutParams.MATCH_PARENT))
        root.addView(touchCaptureView, FrameLayout.LayoutParams(FrameLayout.LayoutParams.MATCH_PARENT, FrameLayout.LayoutParams.MATCH_PARENT))

        // Việc 4: waiting screen shown until the first real frame arrives
        // (replaces the old plain-black screen), added ON TOP of the
        // surface/touch layers so it fully covers them while visible.
        waitingOverlay = buildWaitingOverlay()
        root.addView(waitingOverlay, FrameLayout.LayoutParams(FrameLayout.LayoutParams.MATCH_PARENT, FrameLayout.LayoutParams.MATCH_PARENT))

        // Việc 3: FPS + mode overlay, top-left corner, on top of everything.
        fpsOverlayText = buildFpsOverlayText()
        root.addView(
            fpsOverlayText,
            FrameLayout.LayoutParams(FrameLayout.LayoutParams.WRAP_CONTENT, FrameLayout.LayoutParams.WRAP_CONTENT).apply {
                gravity = Gravity.TOP or Gravity.START
                leftMargin = dp(8)
                topMargin = dp(8)
            }
        )

        // Floating settings button: added LAST so it sits on top of the
        // z-order stack (FrameLayout hit-tests last-added children first),
        // deliberately placed bottom-right to avoid the FPS overlay
        // (top-left, session 9). Being a normal clickable ImageButton is
        // enough to stop the touch from falling through to
        // touchCaptureView underneath -- no changes to TouchCaptureView.kt
        // needed (verified: View.onTouchEvent() for a clickable View
        // consumes ACTION_DOWN and Android's touch dispatch only offers a
        // new ACTION_DOWN to one child, so touchCaptureView never sees it).
        floatingSettingsButton = buildFloatingSettingsButton()
        root.addView(
            floatingSettingsButton,
            FrameLayout.LayoutParams(dp(48), dp(48)).apply {
                gravity = Gravity.BOTTOM or Gravity.END
                rightMargin = dp(16)
                bottomMargin = dp(16)
            }
        )

        setContentView(root)
        surfaceView.holder.addCallback(this)

        // Bind to the foreground service so decoding survives screen-off /
        // MagicOS background kill (see VideoStreamService, M4.5 note in
        // CONTEXT-BRIEF §5 about HONOR aggressively killing background work).
        val serviceIntent = Intent(this, VideoStreamService::class.java)
        ContextCompatStartForegroundService(this, serviceIntent)

        startControlClientIfReady()
    }

    private fun startControlClientIfReady() {
        if (controlClient != null) return
        val host = intent.getStringExtra(EXTRA_HOST) ?: DEFAULT_VIDEO_STREAM_HOST
        val port = intent.getIntExtra(EXTRA_CONTROL_PORT, DEFAULT_CONTROL_PORT)
        Log.i(TAG, "Starting ControlSocketClient -> $host:$port")
        controlClient = ControlSocketClient(applicationContext, host = host, port = port, listener = this).also {
            it.start()
            // TouchCaptureView (M3) forwards TOUCH_EVENT/TOUCH_BATCH through
            // this same control connection, per ControlChannel's seam.
            touchCaptureView.controlChannel = it
        }
    }

    // ---- ControlSocketListener (handshake CAPS -> CONFIG result) ----

    override fun onConfig(config: ConfigMessage) {
        Log.i(TAG, "Handshake complete: PC chose codec=${config.codec} ${config.width}x${config.height}@${config.hz}Hz ${config.bitrateKbps}kbps")
        // Session 12 fix: this used to be a no-op comment claiming the
        // decoder "already adapts" to whatever codec arrives -- false,
        // MediaCodec.createDecoderByType() is codec-specific
        // (video/avc vs video/hevc); a HEVC bitstream fed to an AVC decoder
        // just errors out. Actually honor the PC's chosen codec here.
        val newMime = mimeTypeForCodec(config.codec)
        if (newMime != mimeType) {
            Log.i(TAG, "Decoder MIME set to $newMime (codec=${config.codec}) from initial CONFIG handshake")
            mimeType = newMime
            if (decoderStarted) {
                // Extremely unlikely (CONFIG normally arrives before any
                // video frame), but don't leave a wrong-codec decoder
                // running if it somehow does.
                Log.w(TAG, "Decoder already started before CONFIG codec was known -- recreating for the correct codec")
                recoverDecoderAfterError()
            }
        }
    }

    override fun onConfigUpdate(update: ConfigUpdateMessage) {
        Log.i(TAG, "CONFIG_UPDATE received: ${update.settings.size} setting(s) applied live (no decoder flush)")
    }

    override fun onModeChange(modeChange: ModeChangeMessage) {
        Log.i(TAG, "MODE_CHANGE received: ${modeChange.width}x${modeChange.height}@${modeChange.hz}Hz codec=${modeChange.codec} -- flushing decoder, awaiting next IDR")
        // Session 12: MODE_CHANGE can also carry a codec switch (e.g. user
        // changed the codec preference in Settings, or a mid-stream
        // resolution change re-ran ChooseCodec() on the PC). Update
        // mimeType BEFORE recoverDecoderAfterError() below recreates the
        // decoder, so ensureDecoderStarted() creates it for the NEW codec
        // instead of silently continuing to decode as the old one.
        val newMime = mimeTypeForCodec(modeChange.codec)
        if (newMime != mimeType) {
            Log.i(TAG, "Decoder MIME switching $mimeType -> $newMime (codec=${modeChange.codec}) due to MODE_CHANGE")
            mimeType = newMime
        }
        // Per schema.yaml MODE_CHANGE semantics: flush decoder and await a
        // fresh IDR. R16's recovery path already knows how to safely
        // tear down + recreate the codec on the same Surface, so reuse it
        // here instead of duplicating that logic.
        recoverDecoderAfterError()
    }

    override fun onControlDisconnected(cause: Throwable?, willRetry: Boolean) {
        Log.w(TAG, "Control socket disconnected (willRetry=$willRetry): ${cause?.message}")
    }

    /**
     * Builds the "waiting for PC" screen (Việc 4): logo + indeterminate
     * spinner + message text, centered on a black background. Visibility is
     * toggled from recordFrameEvidenceAndUpdateOverlay() (hide on first
     * frame) and onDisconnected() (show again on video-socket disconnect).
     */
    private fun buildWaitingOverlay(): FrameLayout {
        val overlay = FrameLayout(this)
        overlay.setBackgroundColor(Color.BLACK)

        val column = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            gravity = Gravity.CENTER
        }

        val logo = ImageView(this).apply {
            setImageResource(R.mipmap.ic_launcher)
        }
        column.addView(logo, LinearLayout.LayoutParams(dp(96), dp(96)))

        val progress = ProgressBar(this).apply {
            isIndeterminate = true
        }
        column.addView(
            progress,
            LinearLayout.LayoutParams(LinearLayout.LayoutParams.WRAP_CONTENT, LinearLayout.LayoutParams.WRAP_CONTENT).apply {
                topMargin = dp(16)
            }
        )

        waitingMessageText = TextView(this).apply {
            text = TEXT_WAITING_FIRST_CONNECT
            setTextColor(Color.WHITE)
            textSize = 16f
            gravity = Gravity.CENTER
        }
        column.addView(
            waitingMessageText,
            LinearLayout.LayoutParams(LinearLayout.LayoutParams.WRAP_CONTENT, LinearLayout.LayoutParams.WRAP_CONTENT).apply {
                topMargin = dp(16)
            }
        )

        overlay.addView(
            column,
            FrameLayout.LayoutParams(FrameLayout.LayoutParams.WRAP_CONTENT, FrameLayout.LayoutParams.WRAP_CONTENT, Gravity.CENTER)
        )
        return overlay
    }

    /** Việc 3: small always-on-top HUD showing live FPS + current stream mode (H.264 vs JPEG fallback). */
    private fun buildFpsOverlayText(): TextView {
        return TextView(this).apply {
            text = ""
            setTextColor(Color.WHITE)
            setBackgroundColor(0x80000000.toInt()) // semi-transparent black
            textSize = 12f
            setPadding(dp(4), dp(4), dp(4), dp(4))
        }
    }

    /**
     * Floating settings button: app logo, translucent (alpha 0.6-0.7) when
     * idle, fully opaque while pressed for clear tap feedback, opens
     * [MobileSettingsDialog] on tap. Drawn with a plain [GradientDrawable]
     * oval background instead of a Material FloatingActionButton because
     * this module has no Material Components dependency (see
     * app/build.gradle.kts) and adding one just for this button would
     * violate the "avoid new dependency if avoidable" instruction.
     *
     * Draggable (user request 2026-07-04: "floating button không cố định
     * trong màn hình"): uses View.x/y (absolute translation within the
     * parent FrameLayout) instead of touching LayoutParams margins, so it
     * works regardless of the initial Gravity.BOTTOM|END anchor set where
     * this view is added. A tap vs. drag is disambiguated by total
     * ACTION_MOVE distance: under [DRAG_CLICK_SLOP_DP] is still treated as a
     * click (opens the dialog); past that, the gesture is consumed as a drag
     * and no click fires on ACTION_UP -- standard Android touch-slop pattern.
     */
    private fun buildFloatingSettingsButton(): ImageButton {
        var downRawX = 0f
        var downRawY = 0f
        var downViewX = 0f
        var downViewY = 0f
        var isDragging = false

        return ImageButton(this).apply {
            setImageResource(R.mipmap.ic_launcher)
            scaleType = ImageView.ScaleType.CENTER_CROP
            background = GradientDrawable().apply {
                shape = GradientDrawable.OVAL
                setColor(0x66000000) // translucent dark circle behind the logo
            }
            val pad = dp(6)
            setPadding(pad, pad, pad, pad)
            alpha = 0.65f
            setOnTouchListener { view, event ->
                when (event.action) {
                    MotionEvent.ACTION_DOWN -> {
                        alpha = 1.0f
                        isDragging = false
                        downRawX = event.rawX
                        downRawY = event.rawY
                        downViewX = view.x
                        downViewY = view.y
                        false // let ACTION_DOWN also reach the click machinery
                    }
                    MotionEvent.ACTION_MOVE -> {
                        val dx = event.rawX - downRawX
                        val dy = event.rawY - downRawY
                        if (!isDragging && hypot(dx, dy) > dp(DRAG_CLICK_SLOP_DP)) {
                            isDragging = true
                        }
                        if (isDragging) {
                            val parent = view.parent as? View
                            val maxX = ((parent?.width ?: Int.MAX_VALUE) - view.width).toFloat()
                            val maxY = ((parent?.height ?: Int.MAX_VALUE) - view.height).toFloat()
                            view.x = (downViewX + dx).coerceIn(0f, maxX.coerceAtLeast(0f))
                            view.y = (downViewY + dy).coerceIn(0f, maxY.coerceAtLeast(0f))
                        }
                        isDragging // consume (return true) once it's a real drag, so no click fires later
                    }
                    MotionEvent.ACTION_UP, MotionEvent.ACTION_CANCEL -> {
                        alpha = 0.65f
                        val wasDragging = isDragging
                        isDragging = false
                        // Consuming here (wasDragging) suppresses the click
                        // that would otherwise fire from this same ACTION_UP.
                        wasDragging
                    }
                    else -> false
                }
            }
            setOnClickListener {
                MobileSettingsDialog(this@VideoDecoderActivity).show()
            }
        }
    }

    private fun dp(value: Int): Int = (value * resources.displayMetrics.density).toInt()

    private fun makeFullscreenImmersive() {
        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        @Suppress("DEPRECATION")
        window.decorView.systemUiVisibility = (
            View.SYSTEM_UI_FLAG_LAYOUT_STABLE
                or View.SYSTEM_UI_FLAG_LAYOUT_HIDE_NAVIGATION
                or View.SYSTEM_UI_FLAG_LAYOUT_FULLSCREEN
                or View.SYSTEM_UI_FLAG_HIDE_NAVIGATION
                or View.SYSTEM_UI_FLAG_FULLSCREEN
                or View.SYSTEM_UI_FLAG_IMMERSIVE_STICKY
            )
    }

    override fun surfaceCreated(holder: SurfaceHolder) {
        surfaceReady = true
        startClientIfReady()
    }

    override fun surfaceChanged(holder: SurfaceHolder, format: Int, width: Int, height: Int) {
        // No-op: decoder output size is driven by the incoming stream's
        // SPS/PPS, not by the SurfaceView's own size callback.
    }

    override fun surfaceDestroyed(holder: SurfaceHolder) {
        surfaceReady = false
        stopEverything()
    }

    private fun startClientIfReady() {
        if (client != null) return
        val host = intent.getStringExtra(EXTRA_HOST) ?: DEFAULT_VIDEO_STREAM_HOST
        val port = intent.getIntExtra(EXTRA_PORT, DEFAULT_VIDEO_STREAM_PORT)
        Log.i(TAG, "Starting VideoStreamClient -> $host:$port")
        client = VideoStreamClient(host = host, port = port, listener = this).also { it.start() }
    }

    // ---- VideoStreamListener ----

    override fun onConnected() {
        Log.i(TAG, "Connected to PC host video socket")
    }

    override fun onFrame(frame: VideoFrame) {
        // Việc 3/4: record every frame (both branches below) for the FPS
        // overlay + waiting-screen dismissal BEFORE branching on isJpeg --
        // reuses the one frame counter for both concerns instead of two
        // separate ones (see recordFrameEvidenceAndUpdateOverlay).
        recordFrameEvidenceAndUpdateOverlay(frame)

        // Session 4 "chạy cơ bản trước" shortcut: while the real native
        // H.264 encoder is blocked (MSVC v143/Windows SDK, see R13), the PC
        // falls back to GdiScreenCapture (pure C# JPEG) and marks those
        // frames with the isJpeg flag bit. Those frames bypass MediaCodec
        // entirely -- decode with BitmapFactory and draw straight onto the
        // SurfaceView's Canvas. The MediaCodec/H.264 branch below is left
        // completely untouched for when native build unblocks (M2/M5).
        if (frame.isJpeg) {
            renderJpegFrame(frame)
            return
        }

        try {
            ensureDecoderStarted()
            feedDecoder(frame)
            drainDecoder()
            consecutiveRestarts = 0 // this frame round-tripped fine -- decoder is healthy
        } catch (e: MediaCodec.CodecException) {
            // R16 fix: MediaCodec.CodecException previously left the codec
            // dead forever (caught here, logged, then silently swallowed
            // -- no more frames ever decoded). Now: tear down and recreate
            // the codec on the same Surface, self-recovering locally
            // without waiting for a MODE_CHANGE from the PC (this is a
            // local decoder fault, not a stream renegotiation).
            Log.e(TAG, "CodecException (isRecoverable=${e.isRecoverable}, isTransient=${e.isTransient}, errorCode=${e.errorCode}): ${e.message}", e)
            recoverDecoderAfterError()
        } catch (e: Exception) {
            // Never let a single bad frame crash the Activity -- log and
            // keep the stream alive; decoder errors will surface again on
            // the next MODE_CHANGE/IDR per protocol design (schema.yaml).
            Log.e(TAG, "Error processing frame: ${e.message}", e)
        }
    }

    /**
     * R16 recovery: stop()+release() the broken MediaCodec, create a fresh
     * one on the same Surface, configure()+start() it again, and clear
     * decoderStarted so the next onFrame() re-primes it via
     * ensureDecoderStarted(). Capped at MAX_CONSECUTIVE_DECODER_RESTARTS so
     * a hard-broken codec (e.g. driver crash loop) doesn't spin forever;
     * once the cap is hit we stop trying and just keep logging, same as
     * before this fix, until the Activity is recreated (e.g. reconnect).
     */
    private fun recoverDecoderAfterError() {
        consecutiveRestarts++
        if (consecutiveRestarts > MAX_CONSECUTIVE_DECODER_RESTARTS) {
            Log.e(TAG, "Decoder restart cap ($MAX_CONSECUTIVE_DECODER_RESTARTS) exceeded -- giving up auto-recovery, stream will stay frozen until reconnect")
            return
        }

        Log.w(TAG, "Restarting decoder after CodecException (attempt $consecutiveRestarts/$MAX_CONSECUTIVE_DECODER_RESTARTS)")
        try {
            decoder?.stop()
        } catch (e: Exception) {
            Log.w(TAG, "stop() during recovery threw (ignored): ${e.message}")
        }
        try {
            decoder?.release()
        } catch (e: Exception) {
            Log.w(TAG, "release() during recovery threw (ignored): ${e.message}")
        }
        decoder = null
        decoderStarted = false

        if (!surfaceReady) {
            Log.w(TAG, "Surface not ready, deferring decoder recreation to next surfaceCreated()")
            return
        }

        try {
            ensureDecoderStarted()
            Log.i(TAG, "Decoder restarted successfully (attempt $consecutiveRestarts)")
        } catch (e: Exception) {
            Log.e(TAG, "Failed to recreate decoder during recovery: ${e.message}", e)
        }
    }

    override fun onDisconnected(cause: Throwable?, willRetry: Boolean) {
        Log.w(TAG, "Video stream disconnected (willRetry=$willRetry): ${cause?.message}")
        // Intentionally do not crash/finish() -- per task brief, the
        // Activity must survive connect-refused (server not up yet) and
        // keep waiting; VideoStreamClient handles the retry loop.

        // Việc 4: show the waiting screen again instead of leaving the last
        // decoded frame frozen on screen with no indication anything is
        // wrong. Distinguish "never connected yet" from "was connected,
        // just dropped" via hasReceivedFirstFrame -- reset it so the next
        // real frame flips the overlay back off.
        val message = if (hasReceivedFirstFrame) TEXT_WAITING_RECONNECT else TEXT_WAITING_FIRST_CONNECT
        hasReceivedFirstFrame = false
        runOnUiThread {
            waitingMessageText.text = message
            waitingOverlay.visibility = View.VISIBLE
        }
    }

    private fun ensureDecoderStarted() {
        if (decoderStarted) return
        // Session 12: use the codec actually negotiated with the PC
        // (mimeType, set from onConfig()/onModeChange()) instead of a
        // hardcoded H.264 MIME type -- this was the reason a PC-side HEVC
        // choice would never reach the Android decoder.
        val activeMime = mimeType
        val format = MediaFormat.createVideoFormat(activeMime, FALLBACK_WIDTH, FALLBACK_HEIGHT)

        val codec = MediaCodec.createDecoderByType(activeMime)
        if (supportsLowLatency(codec, activeMime)) {
            format.setInteger(MediaFormat.KEY_LOW_LATENCY, 1)
            Log.i(TAG, "KEY_LOW_LATENCY=1 enabled")
        } else {
            Log.i(TAG, "FEATURE_LowLatency not available on this decoder; running without it")
        }

        codec.configure(format, surfaceView.holder.surface, null, 0)
        codec.start()
        decoder = codec
        decoderStarted = true
        Log.i(TAG, "Decoder started with mimeType=$activeMime")
    }

    private fun supportsLowLatency(codec: MediaCodec, activeMime: String): Boolean {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.R) return false
        return try {
            val caps = codec.codecInfo.getCapabilitiesForType(activeMime)
            caps.isFeatureSupported(MediaCodecInfo.CodecCapabilities.FEATURE_LowLatency)
        } catch (e: Exception) {
            Log.w(TAG, "Could not query FEATURE_LowLatency: ${e.message}")
            false
        }
    }

    private fun feedDecoder(frame: VideoFrame) {
        val codec = decoder ?: return
        val inIndex = codec.dequeueInputBuffer(10_000L)
        if (inIndex < 0) {
            // Input side is backed up (decoder slower than network) -- drop
            // this frame rather than blocking the reader thread, per the
            // "drop-to-latest" latency-first policy in the task brief.
            Log.w(TAG, "No input buffer available, dropping frame ptsUs=${frame.ptsUs}")
            return
        }
        val inputBuffer = codec.getInputBuffer(inIndex) ?: return
        inputBuffer.clear()
        inputBuffer.put(frame.payload)
        val flags = if (frame.isKeyframe) MediaCodec.BUFFER_FLAG_KEY_FRAME else 0
        codec.queueInputBuffer(inIndex, 0, frame.payload.size, frame.ptsUs, flags)
    }

    private fun drainDecoder() {
        val codec = decoder ?: return
        val bufferInfo = MediaCodec.BufferInfo()
        while (true) {
            val outIndex = codec.dequeueOutputBuffer(bufferInfo, 0L)
            when {
                outIndex >= 0 -> {
                    // Render immediately, no PTS-synced wait: drop-to-latest,
                    // low-latency first per task brief.
                    codec.releaseOutputBuffer(outIndex, true)
                }
                outIndex == MediaCodec.INFO_OUTPUT_FORMAT_CHANGED -> {
                    Log.i(TAG, "Decoder output format changed: ${codec.outputFormat}")
                }
                else -> return // INFO_TRY_AGAIN_LATER or INFO_OUTPUT_BUFFERS_CHANGED
            }
        }
    }

    // ---- Việc 3/4: shared frame-evidence counter + FPS/waiting overlay ----
    // Generalized from the session-4 JPEG-only frame counter below (which
    // used to only log "JPEG frame #N"/"JPEG fallback: N frames..." and only
    // ran for the JPEG branch) so both the H.264 and JPEG paths share ONE
    // counting mechanism instead of two, and so the FPS overlay works
    // regardless of which codec path is currently active.

    private var framesRenderedTotal = 0L
    private var fpsWindowStartMs = 0L
    private var fpsWindowCount = 0

    /**
     * Called once per incoming frame (both H.264 and JPEG branches) from
     * onFrame(), before any codec-specific handling. Does three things:
     * hides the waiting overlay on the very first frame (Việc 4), logs the
     * first 3 frames individually plus a throttled once/sec fps summary
     * (same evidence-logging style as the PC side's OnFrameWritten), and
     * updates the top-left FPS/mode HUD text (Việc 3) once per second so it
     * doesn't cost a UI-thread hop on every single frame.
     */
    private fun recordFrameEvidenceAndUpdateOverlay(frame: VideoFrame) {
        if (!hasReceivedFirstFrame) {
            hasReceivedFirstFrame = true
            runOnUiThread { waitingOverlay.visibility = View.GONE }
        }

        framesRenderedTotal++
        if (framesRenderedTotal <= 3) {
            Log.i(TAG, "Frame #$framesRenderedTotal rendered: ${frame.payload.size} bytes, isJpeg=${frame.isJpeg}, ptsUs=${frame.ptsUs}")
        }

        fpsWindowCount++
        val now = System.currentTimeMillis()
        if (fpsWindowStartMs == 0L) fpsWindowStartMs = now
        val elapsedMs = now - fpsWindowStartMs
        if (elapsedMs >= 1000) {
            val fps = fpsWindowCount * 1000.0 / elapsedMs
            Log.i(TAG, "Video: $framesRenderedTotal frames total, ${"%.1f".format(fps)} fps (last frame ${frame.payload.size} bytes, isJpeg=${frame.isJpeg})")

            // Session 12: reflect the ACTUAL negotiated codec instead of a
            // hardcoded "H.264" label, now that HEVC is a real possibility.
            val modeLabel = if (frame.isJpeg) "JPEG fallback" else (if (mimeType == MIME_TYPE_HEVC) "HEVC" else "H.264")
            val overlayText = "FPS: ${"%.1f".format(fps)}\n$modeLabel"
            runOnUiThread { fpsOverlayText.text = overlayText }

            fpsWindowStartMs = now
            fpsWindowCount = 0
        }
    }

    // ---- Session 4 JPEG fallback rendering (bypasses MediaCodec) ----

    /**
     * Decodes a standalone JPEG frame (GdiScreenCapture fallback on the PC
     * side, see VideoFrame.isJpeg) with BitmapFactory and draws it directly
     * onto the SurfaceView's Canvas, scaled to fit while preserving aspect
     * ratio. No MediaCodec involved -- this is the "prove it works today"
     * shortcut, not the final quality/perf path (that's the H.264/NVENC
     * branch above, used once native build unblocks per M2/M5).
     */
    private fun renderJpegFrame(frame: VideoFrame) {
        val bitmap = try {
            BitmapFactory.decodeByteArray(frame.payload, 0, frame.payload.size)
        } catch (e: Exception) {
            Log.e(TAG, "JPEG decode failed (size=${frame.payload.size}): ${e.message}", e)
            null
        }
        if (bitmap == null) {
            Log.w(TAG, "JPEG decode returned null bitmap (size=${frame.payload.size}), skipping frame")
            return
        }

        try {
            drawBitmapToSurface(bitmap)
        } finally {
            bitmap.recycle()
        }
        // Evidence logging (task brief Việc 5.6) is now handled once, for
        // both codec paths, by recordFrameEvidenceAndUpdateOverlay() called
        // from onFrame() before this method runs -- see Việc 3/4 section
        // above (previously duplicated JPEG-only counters lived here).
    }

    private fun drawBitmapToSurface(bitmap: Bitmap) {
        val holder = surfaceView.holder
        val canvas: Canvas = try {
            holder.lockCanvas() ?: return
        } catch (e: Exception) {
            Log.w(TAG, "lockCanvas() failed: ${e.message}")
            return
        }
        try {
            // Scale to fit the surface while preserving aspect ratio
            // (letterbox/pillarbox as needed) -- simplest possible
            // transform for a "prove it works" shortcut.
            val surfaceW = canvas.width
            val surfaceH = canvas.height
            val bmpW = bitmap.width
            val bmpH = bitmap.height
            if (surfaceW <= 0 || surfaceH <= 0 || bmpW <= 0 || bmpH <= 0) return

            val scale = minOf(surfaceW.toFloat() / bmpW, surfaceH.toFloat() / bmpH)
            val destW = (bmpW * scale).toInt()
            val destH = (bmpH * scale).toInt()
            val left = (surfaceW - destW) / 2
            val top = (surfaceH - destH) / 2
            val destRect = Rect(left, top, left + destW, top + destH)

            canvas.drawColor(android.graphics.Color.BLACK)
            canvas.drawBitmap(bitmap, null, destRect, null)
        } finally {
            holder.unlockCanvasAndPost(canvas)
        }
    }

    private fun stopEverything() {
        client?.stop()
        client = null
        controlClient?.stop()
        controlClient = null
        try {
            decoder?.stop()
            decoder?.release()
        } catch (e: Exception) {
            Log.w(TAG, "Error releasing decoder: ${e.message}")
        }
        decoder = null
        decoderStarted = false
    }

    override fun onDestroy() {
        stopEverything()
        super.onDestroy()
    }
}

/** Small helper so onCreate stays readable; startForegroundService needs API 26+ (min SDK is 26). */
private fun ContextCompatStartForegroundService(activity: Activity, intent: Intent) {
    activity.startForegroundService(intent)
}
