package com.displaybridge.protocol

import com.displaybridge.protocol.generated.CapsMessage
import com.displaybridge.protocol.generated.ConfigEntry
import com.displaybridge.protocol.generated.ConfigMessage
import com.displaybridge.protocol.generated.ConfigUpdateMessage
import com.displaybridge.protocol.generated.MessageFraming
import com.displaybridge.protocol.generated.MessageType
import com.displaybridge.protocol.generated.ModeAckMessage
import com.displaybridge.protocol.generated.ModeChangeMessage
import com.displaybridge.protocol.generated.SettingRequestMessage
import com.displaybridge.protocol.generated.StatsMessage
import com.displaybridge.protocol.generated.TouchBatchMessage
import com.displaybridge.protocol.generated.TouchEventItem
import com.displaybridge.protocol.generated.TouchEventMessage
import com.displaybridge.protocol.generated.VideoFrameHeader
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Test

/**
 * JVM unit tests for the generated protocol (com.displaybridge.protocol.generated),
 * mirroring pc-host/tests/Core.Tests/ProtocolRoundtripTests.cs: one roundtrip per
 * message type defined in tools/protocol-schema/schema.yaml, plus framing + the
 * video frame header.
 *
 * NB: Kotlin data classes do NOT generate structural equals()/hashCode() for
 * ByteArray properties (only reference equality), so ModeChangeMessage.csd is
 * compared with contentEquals / assertArrayEquals instead of a whole-object
 * assertEquals.
 */
class MessagesRoundtripTest {

    @Test
    fun caps_roundtrips() {
        val original = CapsMessage(3000, 1920, 400, listOf(60, 90, 120, 144), listOf(0, 1), 10)
        val result = CapsMessage.deserialize(original.serialize())
        assertEquals(original, result)
    }

    @Test
    fun config_roundtrips() {
        val original = ConfigMessage(1, 3000, 1920, 120, 69000L)
        val result = ConfigMessage.deserialize(original.serialize())
        assertEquals(original, result)
    }

    @Test
    fun settingRequest_roundtrips() {
        val original = SettingRequestMessage(5, 123456789L)
        val result = SettingRequestMessage.deserialize(original.serialize())
        assertEquals(original, result)
    }

    @Test
    fun settingRequest_roundtrips_largeVarint() {
        // Exercise multi-byte varint encoding (Long.MAX_VALUE > 1 LEB128 byte).
        val original = SettingRequestMessage(1, Long.MAX_VALUE)
        val result = SettingRequestMessage.deserialize(original.serialize())
        assertEquals(original, result)
    }

    @Test
    fun configUpdate_roundtrips() {
        val original = ConfigUpdateMessage(listOf(ConfigEntry(1, 100), ConfigEntry(2, 0)))
        val result = ConfigUpdateMessage.deserialize(original.serialize())
        assertEquals(original, result)
    }

    @Test
    fun configUpdate_roundtrips_emptyList() {
        val original = ConfigUpdateMessage(emptyList())
        val result = ConfigUpdateMessage.deserialize(original.serialize())
        assertEquals(emptyList<ConfigEntry>(), result.settings)
    }

    @Test
    fun modeChange_roundtrips() {
        val original = ModeChangeMessage(3000, 1920, 120, 1, byteArrayOf(0, 1, 2, 3, -1))
        val result = ModeChangeMessage.deserialize(original.serialize())
        assertEquals(original.width, result.width)
        assertEquals(original.height, result.height)
        assertEquals(original.hz, result.hz)
        assertEquals(original.codec, result.codec)
        assertArrayEquals(original.csd, result.csd)
    }

    @Test
    fun modeAck_roundtrips() {
        val original = ModeAckMessage(0)
        val result = ModeAckMessage.deserialize(original.serialize())
        assertEquals(original, result)
    }

    @Test
    fun stats_roundtrips() {
        val original = StatsMessage(120, 6, 2, 4294967295L)
        val result = StatsMessage.deserialize(original.serialize())
        assertEquals(original, result)
    }

    @Test
    fun touchEvent_roundtrips() {
        val original = TouchEventMessage(
            pointerId = 3,
            action = 1,
            x = 32768,
            y = 40000,
            pressure = 12000,
            toolType = 1,
            timestampUs = 123456789012L
        )
        val result = TouchEventMessage.deserialize(original.serialize())
        assertEquals(original, result)
    }

    @Test
    fun touchBatch_roundtrips() {
        val original = TouchBatchMessage(
            listOf(
                TouchEventItem(0, 0, 100, 200, 60000, 0, 1L),
                TouchEventItem(1, 1, 101, 201, 60000, 0, 2L),
                TouchEventItem(0, 2, 102, 202, 0, 0, 3L)
            )
        )
        val result = TouchBatchMessage.deserialize(original.serialize())
        assertEquals(original.events, result.events)
    }

    @Test
    fun touchBatch_roundtrips_emptyList() {
        val original = TouchBatchMessage(emptyList())
        val result = TouchBatchMessage.deserialize(original.serialize())
        assertEquals(emptyList<TouchEventItem>(), result.events)
    }

    @Test
    fun writeFramed_thenReadFramed_roundtripsModeAck() {
        val original = ModeAckMessage(2)
        val framed = MessageFraming.writeFramed(original)

        // Envelope is [type:1][length:2][payload]. ModeAck payload is 1 byte.
        assertEquals(MessageType.ModeAck.value, framed[0].toInt() and 0xFF)
        assertEquals(4, framed.size)

        val result = MessageFraming.readFramed(framed) as ModeAckMessage
        assertEquals(original.status, result.status)
    }

    @Test
    fun videoFrameHeader_roundtrips() {
        val original = VideoFrameHeader(123456L, 1, 987654321012L)
        val result = VideoFrameHeader.deserialize(original.serialize())
        assertEquals(original, result)
        assertEquals(VideoFrameHeader.WIRE_SIZE, original.serialize().size)
    }
}
