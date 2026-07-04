package com.displaybridge.input

import android.view.MotionEvent
import com.displaybridge.protocol.generated.TouchEventItem

/**
 * Pure coordinate/value mapping helpers used by [TouchCaptureView] to turn
 * raw Android pointer data into wire-ready [TouchEventItem] values.
 *
 * Kept free of any MotionEvent method calls that require the real Android
 * runtime (only numeric constants like [MotionEvent.TOOL_TYPE_STYLUS] and
 * the [MotionEvent.ACTION_*] ints are referenced) so this stays exercisable
 * from a plain JVM unit test.
 */
object TouchEventMapper {

    const val ACTION_DOWN: Int = 0
    const val ACTION_MOVE: Int = 1
    const val ACTION_UP: Int = 2
    const val ACTION_CANCEL: Int = 3

    const val TOOL_FINGER: Int = 0
    const val TOOL_STYLUS: Int = 1

    /** Normalize a raw pixel coordinate to the wire's 0-65535 range. */
    fun normalizeAxis(rawValue: Float, extentPx: Int): Int {
        if (extentPx <= 0) return 0
        val fraction = (rawValue / extentPx).coerceIn(0f, 1f)
        return (fraction * 65535f).toInt().coerceIn(0, 65535)
    }

    /** Android reports pressure as ~0.0-1.0 (can exceed 1.0 on some digitizers). */
    fun normalizePressure(rawPressure: Float): Int {
        return (rawPressure.coerceIn(0f, 1f) * 65535f).toInt().coerceIn(0, 65535)
    }

    /** Maps MotionEvent.TOOL_TYPE_* to the protocol's FINGER=0/STYLUS=1. */
    fun mapToolType(motionToolType: Int): Int =
        if (motionToolType == MotionEvent.TOOL_TYPE_STYLUS) TOOL_STYLUS else TOOL_FINGER

    /**
     * Maps a MotionEvent action to the protocol's DOWN/MOVE/UP/CANCEL, folding
     * the multi-touch POINTER_DOWN/POINTER_UP variants into DOWN/UP (the
     * protocol distinguishes pointers by pointerId, not by which action
     * constant carried the event).
     */
    fun mapAction(motionAction: Int): Int = when (motionAction and MotionEvent.ACTION_MASK) {
        MotionEvent.ACTION_DOWN, MotionEvent.ACTION_POINTER_DOWN -> ACTION_DOWN
        MotionEvent.ACTION_MOVE -> ACTION_MOVE
        MotionEvent.ACTION_UP, MotionEvent.ACTION_POINTER_UP -> ACTION_UP
        MotionEvent.ACTION_CANCEL -> ACTION_CANCEL
        else -> ACTION_MOVE
    }

    fun toTouchEventItem(
        pointerId: Int,
        action: Int,
        rawX: Float,
        rawY: Float,
        viewWidthPx: Int,
        viewHeightPx: Int,
        rawPressure: Float,
        motionToolType: Int,
        timestampUs: Long
    ): TouchEventItem = TouchEventItem(
        pointerId = pointerId.coerceIn(0, 255),
        action = action,
        x = normalizeAxis(rawX, viewWidthPx),
        y = normalizeAxis(rawY, viewHeightPx),
        pressure = normalizePressure(rawPressure),
        toolType = mapToolType(motionToolType),
        timestampUs = timestampUs
    )
}
