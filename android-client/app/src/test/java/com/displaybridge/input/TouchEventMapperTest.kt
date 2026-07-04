package com.displaybridge.input

import android.view.MotionEvent
import org.junit.Assert.assertEquals
import org.junit.Test

/**
 * Pure-logic JVM unit tests for TouchEventMapper (no Android runtime needed —
 * only numeric MotionEvent.* constants are referenced, never instance methods).
 */
class TouchEventMapperTest {

    @Test
    fun normalizeAxis_mapsZeroToZero() {
        assertEquals(0, TouchEventMapper.normalizeAxis(0f, 1000))
    }

    @Test
    fun normalizeAxis_mapsMaxToUpperBound() {
        assertEquals(65535, TouchEventMapper.normalizeAxis(1000f, 1000))
    }

    @Test
    fun normalizeAxis_mapsMidpoint() {
        val result = TouchEventMapper.normalizeAxis(500f, 1000)
        assertEquals(32767, result)
    }

    @Test
    fun normalizeAxis_clampsOutOfRangeValues() {
        assertEquals(65535, TouchEventMapper.normalizeAxis(5000f, 1000))
        assertEquals(0, TouchEventMapper.normalizeAxis(-100f, 1000))
    }

    @Test
    fun normalizeAxis_zeroExtentReturnsZero() {
        assertEquals(0, TouchEventMapper.normalizeAxis(50f, 0))
    }

    @Test
    fun normalizePressure_clampsAboveOne() {
        // Some digitizers report pressure > 1.0.
        assertEquals(65535, TouchEventMapper.normalizePressure(1.5f))
    }

    @Test
    fun normalizePressure_zeroStaysZero() {
        assertEquals(0, TouchEventMapper.normalizePressure(0f))
    }

    @Test
    fun mapToolType_stylusMapsToOne() {
        assertEquals(TouchEventMapper.TOOL_STYLUS, TouchEventMapper.mapToolType(MotionEvent.TOOL_TYPE_STYLUS))
    }

    @Test
    fun mapToolType_fingerMapsToZero() {
        assertEquals(TouchEventMapper.TOOL_FINGER, TouchEventMapper.mapToolType(MotionEvent.TOOL_TYPE_FINGER))
    }

    @Test
    fun mapAction_downVariantsMapToDown() {
        assertEquals(TouchEventMapper.ACTION_DOWN, TouchEventMapper.mapAction(MotionEvent.ACTION_DOWN))
        assertEquals(TouchEventMapper.ACTION_DOWN, TouchEventMapper.mapAction(MotionEvent.ACTION_POINTER_DOWN))
    }

    @Test
    fun mapAction_upVariantsMapToUp() {
        assertEquals(TouchEventMapper.ACTION_UP, TouchEventMapper.mapAction(MotionEvent.ACTION_UP))
        assertEquals(TouchEventMapper.ACTION_UP, TouchEventMapper.mapAction(MotionEvent.ACTION_POINTER_UP))
    }

    @Test
    fun mapAction_moveMapsToMove() {
        assertEquals(TouchEventMapper.ACTION_MOVE, TouchEventMapper.mapAction(MotionEvent.ACTION_MOVE))
    }

    @Test
    fun mapAction_cancelMapsToCancel() {
        assertEquals(TouchEventMapper.ACTION_CANCEL, TouchEventMapper.mapAction(MotionEvent.ACTION_CANCEL))
    }

    @Test
    fun toTouchEventItem_buildsExpectedFields() {
        val item = TouchEventMapper.toTouchEventItem(
            pointerId = 2,
            action = TouchEventMapper.ACTION_MOVE,
            rawX = 500f,
            rawY = 250f,
            viewWidthPx = 1000,
            viewHeightPx = 500,
            rawPressure = 0.5f,
            motionToolType = MotionEvent.TOOL_TYPE_STYLUS,
            timestampUs = 42L
        )
        assertEquals(2, item.pointerId)
        assertEquals(TouchEventMapper.ACTION_MOVE, item.action)
        assertEquals(32767, item.x)
        assertEquals(32767, item.y)
        assertEquals(TouchEventMapper.TOOL_STYLUS, item.toolType)
        assertEquals(42L, item.timestampUs)
    }
}
