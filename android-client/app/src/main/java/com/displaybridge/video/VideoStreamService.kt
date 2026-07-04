// M1.2: minimal foreground service so the video pipeline is not killed
// when the tablet screen turns off/locks. HONOR MagicOS is known (per
// CONTEXT-BRIEF-v2-m1-m3-m4-tasks.md §5) to aggressively kill background
// work; a full M4.5 HONOR-specific whitelist-guidance flow is a separate,
// later task -- this is just the minimal startForeground() scaffold M1
// needs so VideoDecoderActivity survives screen-off during dev testing.
package com.displaybridge.video

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.Service
import android.content.Intent
import android.content.pm.ServiceInfo
import android.os.Build
import android.os.IBinder

class VideoStreamService : Service() {

    companion object {
        private const val CHANNEL_ID = "displaybridge_video_stream"
        private const val NOTIFICATION_ID = 1001
    }

    override fun onCreate() {
        super.onCreate()
        createNotificationChannelIfNeeded()
        val notification = buildNotification()
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
            // FGS type history:
            //  1. FOREGROUND_SERVICE_TYPE_CONNECTED_DEVICE (matched M4.5 plan
            //     wording) threw SecurityException on targetSdk 36 -- it needs
            //     a USB/Bluetooth/CompanionDeviceManager permission we don't
            //     hold. We are a plain adb-forwarded loopback TCP client, not a
            //     USB/BT companion, so connectedDevice was simply the wrong
            //     type.
            //  2. FOREGROUND_SERVICE_TYPE_DATA_SYNC works but is deprecated on
            //     Android 15+ and subject to a ~6h rolling runtime cap; the OS
            //     force-stops it after the budget, which breaks an indefinite
            //     display mirror.
            //  3. MEDIA_PLAYBACK (used here) is the correct type: a display
            //     mirror is continuous video playback. No time cap, no extra
            //     runtime permission beyond FOREGROUND_SERVICE_MEDIA_PLAYBACK.
            startForeground(
                NOTIFICATION_ID,
                notification,
                ServiceInfo.FOREGROUND_SERVICE_TYPE_MEDIA_PLAYBACK
            )
        } else {
            startForeground(NOTIFICATION_ID, notification)
        }
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        // Sticky: if the OS still kills us under memory pressure, restart
        // and re-enter foreground state rather than staying dead.
        return START_STICKY
    }

    override fun onBind(intent: Intent?): IBinder? = null

    private fun createNotificationChannelIfNeeded() {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.O) return
        val manager = getSystemService(NotificationManager::class.java) ?: return
        if (manager.getNotificationChannel(CHANNEL_ID) != null) return
        val channel = NotificationChannel(
            CHANNEL_ID,
            "DisplayBridge video stream",
            NotificationManager.IMPORTANCE_LOW
        ).apply {
            description = "Keeps the DisplayBridge video mirror connection alive"
        }
        manager.createNotificationChannel(channel)
    }

    private fun buildNotification(): Notification {
        return Notification.Builder(this, CHANNEL_ID)
            .setContentTitle("DisplayBridge")
            .setContentText("Mirroring PC display")
            .setSmallIcon(android.R.drawable.ic_menu_view)
            .setOngoing(true)
            .build()
    }
}
