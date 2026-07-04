// M1.2 (Android video pipeline): incremental parser for the video-frame
// wire format defined in tools/protocol-schema/schema.yaml §"Video Frame
// Header" (see CONTEXT-BRIEF-v2-m1-m3-m4-tasks.md §2):
//
//   len    : u32  -- payload byte length
//   flags  : u8   -- bit0 = IDR/keyframe, bit1 = JPEG raw frame (session 4
//                    fallback, see VideoFrame.isJpeg below), bits2-7 reserved
//   ptsUs  : u64  -- presentation timestamp, microseconds
//   payload: len bytes -- H.264/HEVC NAL units, OR a standalone JPEG image
//            when bit1 is set (see VideoFrame.isJpeg)
//
// This header is NOT type-prefixed (unlike the control-socket messages in
// com.displaybridge.protocol.generated.MessageFraming), so that class does
// not apply here -- it assumes a MessageType byte + u16 length. We do reuse
// the exact same little-endian integer encoding conventions as
// protocol.generated.WireIO (WireReader/WireWriter) for consistency with
// the rest of the wire protocol, but implement our own reader here because
// WireReader operates on a single already-complete byte[] and offers no way
// to accumulate partial reads across multiple TCP socket.read() calls.
//
// TCP is a byte stream, not a message stream: a single socket.read() may
// return anywhere from 1 byte to many frames worth of data. VideoFrameParser
// buffers incomplete data across feed() calls and only emits a frame once
// the full header + payload have arrived.
package com.displaybridge.video

import java.io.ByteArrayOutputStream

/** One decoded video frame ready to hand to the MediaCodec decoder. */
data class VideoFrame(
    val length: Long,
    val flags: Int,
    val ptsUs: Long,
    val payload: ByteArray
) {
    val isKeyframe: Boolean get() = (flags and 0x01) != 0

    // Session 4 "chạy cơ bản trước" shortcut: PC's real DXGI+MediaFoundation
    // H.264 native pipeline is still blocked (MSVC v143/Windows SDK not
    // installed, see docs/TASK-v1-tablet-display-tracker.md R13). PC falls
    // back to GdiScreenCapture (pure C# JPEG screen capture) and marks
    // every such frame with bit1 here (see FrameFlags.Jpeg in
    // NativeCaptureEncoder.cs / schema.yaml's updated flags doc). When set,
    // this is a standalone JPEG image, NOT an H.264/HEVC NAL unit -- the
    // receiver (VideoDecoderActivity) must decode it with BitmapFactory,
    // never feed it to MediaCodec.
    val isJpeg: Boolean get() = (flags and 0x02) != 0

    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is VideoFrame) return false
        return length == other.length &&
            flags == other.flags &&
            ptsUs == other.ptsUs &&
            payload.contentEquals(other.payload)
    }

    override fun hashCode(): Int {
        var result = length.hashCode()
        result = 31 * result + flags
        result = 31 * result + ptsUs.hashCode()
        result = 31 * result + payload.contentHashCode()
        return result
    }
}

/**
 * Stateful, streaming parser: feed it arbitrarily-sized chunks (as read from
 * a socket InputStream) via [feed]; it returns zero or more fully-assembled
 * [VideoFrame]s per call and internally remembers any partial header/payload
 * bytes for the next call.
 *
 * Not thread-safe -- intended to be driven by a single reader thread
 * (see VideoStreamClient's read loop).
 */
class VideoFrameParser {

    private companion object {
        const val HEADER_SIZE = 4 + 1 + 8 // len(u32) + flags(u8) + ptsUs(u64)
        const val MAX_PAYLOAD_BYTES = 64L * 1024 * 1024 // 64MB sanity guard
    }

    private enum class State { READING_HEADER, READING_PAYLOAD }

    private var state = State.READING_HEADER
    private val headerBuffer = ByteArrayOutputStream(HEADER_SIZE)

    private var pendingLength = 0L
    private var pendingFlags = 0
    private var pendingPtsUs = 0L
    private var payloadBuffer: ByteArrayOutputStream? = null
    private var payloadRemaining = 0L

    /**
     * Feed newly-received bytes into the parser. Returns any frames that
     * became complete as a result of this call, in arrival order. Safe to
     * call repeatedly with small/partial chunks -- state persists across
     * calls.
     */
    fun feed(chunk: ByteArray, offset: Int = 0, length: Int = chunk.size): List<VideoFrame> {
        val frames = mutableListOf<VideoFrame>()
        var pos = offset
        val end = offset + length
        while (pos < end) {
            when (state) {
                State.READING_HEADER -> {
                    val need = HEADER_SIZE - headerBuffer.size()
                    val take = minOf(need, end - pos)
                    headerBuffer.write(chunk, pos, take)
                    pos += take
                    if (headerBuffer.size() == HEADER_SIZE) {
                        val header = headerBuffer.toByteArray()
                        pendingLength = readU32LE(header, 0)
                        pendingFlags = header[4].toInt() and 0xFF
                        pendingPtsUs = readU64LE(header, 5)
                        headerBuffer.reset()

                        require(pendingLength in 0..MAX_PAYLOAD_BYTES) {
                            "VideoFrameParser: implausible frame length=$pendingLength " +
                                "(max=$MAX_PAYLOAD_BYTES) -- stream desync?"
                        }

                        if (pendingLength == 0L) {
                            frames.add(VideoFrame(0, pendingFlags, pendingPtsUs, ByteArray(0)))
                            state = State.READING_HEADER
                        } else {
                            payloadBuffer = ByteArrayOutputStream(pendingLength.toInt())
                            payloadRemaining = pendingLength
                            state = State.READING_PAYLOAD
                        }
                    }
                }
                State.READING_PAYLOAD -> {
                    val take = minOf(payloadRemaining, (end - pos).toLong()).toInt()
                    payloadBuffer!!.write(chunk, pos, take)
                    pos += take
                    payloadRemaining -= take
                    if (payloadRemaining == 0L) {
                        frames.add(
                            VideoFrame(
                                pendingLength,
                                pendingFlags,
                                pendingPtsUs,
                                payloadBuffer!!.toByteArray()
                            )
                        )
                        payloadBuffer = null
                        state = State.READING_HEADER
                    }
                }
            }
        }
        return frames
    }

    private fun readU32LE(b: ByteArray, off: Int): Long {
        var result = 0L
        for (i in 0 until 4) result = result or ((b[off + i].toLong() and 0xFF) shl (i * 8))
        return result
    }

    private fun readU64LE(b: ByteArray, off: Int): Long {
        var result = 0L
        for (i in 0 until 8) result = result or ((b[off + i].toLong() and 0xFF) shl (i * 8))
        return result
    }
}
