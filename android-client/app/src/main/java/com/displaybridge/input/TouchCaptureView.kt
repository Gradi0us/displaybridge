package com.displaybridge.input

import android.content.Context
import android.util.AttributeSet
import android.view.MotionEvent
import android.view.View
import com.displaybridge.protocol.generated.MessageFraming
import com.displaybridge.protocol.generated.TouchBatchMessage
import com.displaybridge.protocol.generated.TouchEventItem
import com.displaybridge.protocol.generated.TouchEventMessage
import java.io.ByteArrayOutputStream

/**
 * Transparent overlay view for M3 (bidirectional input). Add this ON TOP of
 * the existing mirroring SurfaceView (M1-Android owns that file — this view
 * is deliberately separate so the two packages never touch the same file).
 * It only captures MotionEvent samples and forwards them as TOUCH_EVENT /
 * TOUCH_BATCH control-socket messages; it renders nothing itself
 * (transparent, MATCH_PARENT over the video surface).
 *
 * PC-authoritative design (see RESEARCH-v1-windows-touch-gesture-mechanism.md):
 * this view does NOT decide Cursor-vs-Touch mode — it just reports raw
 * per-pointer samples. That decision is made by InputModeClassifier on the
 * PC side.
 */
class TouchCaptureView @JvmOverloads constructor(
    context: Context,
    attrs: AttributeSet? = null,
    defStyleAttr: Int = 0
) : View(context, attrs, defStyleAttr) {

    /** Wired up by the hosting Activity/Fragment once the control socket is connected. */
    var controlChannel: ControlChannel? = null

    /** When false, touch capture is fully disabled (mirrors the "Touch to PC" setting). */
    var touchToPcEnabled: Boolean = true

    override fun onTouchEvent(event: MotionEvent): Boolean {
        if (!touchToPcEnabled) return false
        dispatchAsBatch(event)
        return true
    }

    override fun onGenericMotionEvent(event: MotionEvent): Boolean {
        // Hover events (stylus hover-before-contact) arrive here on many
        // devices; MotionEvent.ACTION_HOVER_MOVE maps to a MOVE sample with
        // pressure 0 so the PC can still see stylus position ahead of touch-down.
        if (!touchToPcEnabled) return super.onGenericMotionEvent(event)
        if (event.action == MotionEvent.ACTION_HOVER_MOVE) {
            dispatchAsBatch(event)
            return true
        }
        return super.onGenericMotionEvent(event)
    }

    /**
     * Builds one TouchBatch out of every historical sample
     * (getHistorySize()) plus the current sample, for every active pointer,
     * and sends it as a single control-socket write. Falls back to a plain
     * TOUCH_EVENT when there is exactly one sample total (skips the list
     * envelope overhead for the common case).
     */
    private fun dispatchAsBatch(event: MotionEvent) {
        val channel = controlChannel ?: return
        val width = width
        val height = height
        if (width <= 0 || height <= 0) return

        val timestampBaseUs = System.nanoTime() / 1000L
        val items = ArrayList<TouchEventItem>(event.historySize + 1)

        val pointerCount = event.pointerCount
        for (h in 0 until event.historySize) {
            val historicalTimeUs = timestampBaseUs - (event.eventTime - event.getHistoricalEventTime(h)) * 1000L
            for (p in 0 until pointerCount) {
                items.add(
                    TouchEventMapper.toTouchEventItem(
                        pointerId = event.getPointerId(p),
                        action = TouchEventMapper.mapAction(event.action),
                        rawX = event.getHistoricalX(p, h),
                        rawY = event.getHistoricalY(p, h),
                        viewWidthPx = width,
                        viewHeightPx = height,
                        rawPressure = event.getHistoricalPressure(p, h),
                        motionToolType = event.getToolType(p),
                        timestampUs = historicalTimeUs
                    )
                )
            }
        }
        for (p in 0 until pointerCount) {
            items.add(
                TouchEventMapper.toTouchEventItem(
                    pointerId = event.getPointerId(p),
                    action = TouchEventMapper.mapAction(event.action),
                    rawX = event.getX(p),
                    rawY = event.getY(p),
                    viewWidthPx = width,
                    viewHeightPx = height,
                    rawPressure = event.getPressure(p),
                    motionToolType = event.getToolType(p),
                    timestampUs = timestampBaseUs
                )
            )
        }

        if (items.isEmpty()) return

        val framed = if (items.size == 1) {
            val single = items[0]
            MessageFraming.writeFramed(
                TouchEventMessage(
                    pointerId = single.pointerId,
                    action = single.action,
                    x = single.x,
                    y = single.y,
                    pressure = single.pressure,
                    toolType = single.toolType,
                    timestampUs = single.timestampUs
                )
            )
        } else {
            MessageFraming.writeFramed(TouchBatchMessage(items))
        }
        channel.send(framed)
    }

    /** Test/diagnostic helper: total on-wire bytes a batch of [items] would take. */
    internal fun estimatedBatchSize(items: List<TouchEventItem>): Int {
        val out = ByteArrayOutputStream()
        out.write(MessageFraming.writeFramed(TouchBatchMessage(items)))
        return out.size()
    }
}
