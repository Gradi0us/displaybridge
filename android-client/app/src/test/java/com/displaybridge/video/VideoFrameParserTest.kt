// M1.2 unit test: VideoFrameParser must correctly reconstruct frames from
// the video-frame wire format (u32 len, u8 flags, u64 ptsUs, payload) even
// when the underlying TCP socket delivers the bytes in arbitrarily small
// pieces across multiple read() calls -- TCP gives no guarantee that a
// single read() returns a whole frame, or even a whole header.
package com.displaybridge.video

import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.ByteArrayOutputStream

class VideoFrameParserTest {

    /** Encode one frame using the exact same little-endian layout as the wire format. */
    private fun encodeFrame(flags: Int, ptsUs: Long, payload: ByteArray): ByteArray {
        val out = ByteArrayOutputStream()
        val len = payload.size.toLong()
        for (i in 0 until 4) out.write(((len ushr (i * 8)) and 0xFF).toInt())
        out.write(flags and 0xFF)
        for (i in 0 until 8) out.write(((ptsUs ushr (i * 8)) and 0xFF).toInt())
        out.write(payload)
        return out.toByteArray()
    }

    @Test
    fun `parses a single complete frame delivered in one chunk`() {
        val payload = byteArrayOf(0, 0, 0, 1, 0x67, 1, 2, 3) // fake SPS-ish NAL
        val wire = encodeFrame(flags = 0x01, ptsUs = 123_456_789L, payload = payload)

        val parser = VideoFrameParser()
        val frames = parser.feed(wire)

        assertEquals(1, frames.size)
        val frame = frames[0]
        assertEquals(payload.size.toLong(), frame.length)
        assertEquals(0x01, frame.flags)
        assertTrue(frame.isKeyframe)
        assertEquals(123_456_789L, frame.ptsUs)
        assertArrayEquals(payload, frame.payload)
    }

    @Test
    fun `parses multiple frames concatenated in one chunk`() {
        val p1 = byteArrayOf(1, 2, 3)
        val p2 = byteArrayOf(4, 5, 6, 7)
        val wire = encodeFrame(0x00, 1000L, p1) + encodeFrame(0x01, 2000L, p2)

        val parser = VideoFrameParser()
        val frames = parser.feed(wire)

        assertEquals(2, frames.size)
        assertArrayEquals(p1, frames[0].payload)
        assertEquals(1000L, frames[0].ptsUs)
        assertArrayEquals(p2, frames[1].payload)
        assertEquals(2000L, frames[1].ptsUs)
        assertTrue(frames[1].isKeyframe)
    }

    @Test
    fun `handles header split across many single-byte reads`() {
        val payload = byteArrayOf(9, 9, 9)
        val wire = encodeFrame(0x01, 42L, payload)

        val parser = VideoFrameParser()
        val allFrames = mutableListOf<VideoFrame>()
        // Feed exactly one byte at a time -- worst-case TCP fragmentation.
        for (b in wire) {
            allFrames += parser.feed(byteArrayOf(b))
        }

        assertEquals(1, allFrames.size)
        assertArrayEquals(payload, allFrames[0].payload)
        assertEquals(42L, allFrames[0].ptsUs)
    }

    @Test
    fun `handles payload split across multiple partial reads`() {
        val payload = ByteArray(100) { it.toByte() }
        val wire = encodeFrame(0x00, 500L, payload)

        val parser = VideoFrameParser()
        val allFrames = mutableListOf<VideoFrame>()
        // Split into small, uneven chunks that don't align to header/payload
        // boundaries at all (chunk size 7 vs header size 13 vs payload 100).
        var offset = 0
        val chunkSize = 7
        while (offset < wire.size) {
            val end = minOf(offset + chunkSize, wire.size)
            allFrames += parser.feed(wire.copyOfRange(offset, end))
            offset = end
        }

        assertEquals(1, allFrames.size)
        assertArrayEquals(payload, allFrames[0].payload)
    }

    @Test
    fun `handles a frame split exactly at the header-payload boundary`() {
        val payload = byteArrayOf(1, 2, 3, 4, 5)
        val wire = encodeFrame(0x01, 77L, payload)
        val headerSize = 13

        val parser = VideoFrameParser()
        val firstBatch = parser.feed(wire.copyOfRange(0, headerSize))
        assertTrue("no frame should be complete after header-only bytes", firstBatch.isEmpty())

        val secondBatch = parser.feed(wire.copyOfRange(headerSize, wire.size))
        assertEquals(1, secondBatch.size)
        assertArrayEquals(payload, secondBatch[0].payload)
    }

    @Test
    fun `handles zero-length payload frame`() {
        val wire = encodeFrame(0x00, 1L, ByteArray(0))
        val parser = VideoFrameParser()
        val frames = parser.feed(wire)

        assertEquals(1, frames.size)
        assertEquals(0L, frames[0].length)
        assertEquals(0, frames[0].payload.size)
    }

    @Test
    fun `continues correctly across many frames fed byte by byte`() {
        val frames = listOf(
            Triple(0x01, 1L, byteArrayOf(1)),
            Triple(0x00, 2L, byteArrayOf(2, 2)),
            Triple(0x01, 3L, byteArrayOf(3, 3, 3))
        )
        val wire = frames.fold(ByteArrayOutputStream()) { acc, (flags, pts, payload) ->
            acc.write(encodeFrame(flags, pts, payload))
            acc
        }.toByteArray()

        val parser = VideoFrameParser()
        val result = mutableListOf<VideoFrame>()
        for (b in wire) result += parser.feed(byteArrayOf(b))

        assertEquals(3, result.size)
        for (i in frames.indices) {
            assertEquals(frames[i].second, result[i].ptsUs)
            assertArrayEquals(frames[i].third, result[i].payload)
        }
    }

    @Test(expected = IllegalArgumentException::class)
    fun `rejects implausibly large frame length as a desync guard`() {
        val out = ByteArrayOutputStream()
        val badLen = 0xFFFFFFFFL // ~4GB, way past MAX_PAYLOAD_BYTES guard
        for (i in 0 until 4) out.write(((badLen ushr (i * 8)) and 0xFF).toInt())
        out.write(0)
        for (i in 0 until 8) out.write(0)

        VideoFrameParser().feed(out.toByteArray())
    }
}
