// PcPresenceWatcherService.kt -- session 19 feature (user request: "app
// mobile chạy ngầm; khi phát hiện PC có app Windows + adb qua USB thì hiển
// thị popup yêu cầu kết nối, đồng ý sẽ tự vào").
//
// A foreground service (foregroundServiceType="specialUse", see manifest --
// none of the typed categories fit "poll a loopback socket", and the ongoing
// low-priority notification it costs doubles as a useful "đang chờ PC"
// status indicator on this HONOR device whose MagicOS aggressively kills
// plain background services). Every 5s, when NOT streaming and the user
// pref is on, it runs PcPresenceProbe against the control port; on an
// ABSENT->PRESENT transition it posts ONE high-priority notification whose
// tap launches VideoDecoderActivity (which then does the real CAPS/CONFIG
// handshake). No re-posting while PRESENT persists; the alert is cancelled
// when streaming starts or the PC drops off.
package com.displaybridge.presence

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Context
import android.content.Intent
import android.os.Build
import android.os.IBinder
import android.util.Log
import com.displaybridge.settings.SettingsPrefs

class PcPresenceWatcherService : Service() {

    companion object {
        private const val TAG = "PcPresenceWatcher"
        private const val CONTROL_PORT = 29501
        private const val PROBE_INTERVAL_MS = 5000L
        private const val CHANNEL_ONGOING = "pc_presence_watch"
        private const val CHANNEL_ALERT = "pc_presence_alert"
        private const val NOTIF_ID_ONGOING = 201
        private const val NOTIF_ID_ALERT = 202

        /**
         * Set true by VideoDecoderActivity while its control socket is
         * connected (streaming session active). The watcher MUST NOT probe
         * during a live session: ControlSocketServer on the PC keeps a
         * single "active client" slot (latest connect wins), so a probe
         * connect would momentarily hijack the real session's write slot.
         * A static volatile flag instead of a bound-service/DI dance: this
         * module deliberately has zero DI/androidx infrastructure (see
         * MobileSettingsDialog's header comment), and the two parties are
         * in-process.
         */
        @Volatile
        var streamingActive: Boolean = false

        fun start(context: Context) {
            val intent = Intent(context, PcPresenceWatcherService::class.java)
            // min SDK 26 -> startForegroundService always exists.
            context.startForegroundService(intent)
        }
    }

    private var watcherThread: Thread? = null
    @Volatile private var running = false

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onCreate() {
        super.onCreate()
        createChannels()
        startForeground(NOTIF_ID_ONGOING, buildOngoingNotification())
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        if (watcherThread == null) {
            running = true
            watcherThread = Thread(::watchLoop, "PcPresenceWatcher").apply {
                isDaemon = true
                start()
            }
        }
        // Restart if MagicOS kills us anyway -- the whole point is to keep
        // waiting for the PC in the background.
        return START_STICKY
    }

    override fun onDestroy() {
        running = false
        watcherThread?.interrupt()
        watcherThread = null
        super.onDestroy()
    }

    private fun watchLoop() {
        var lastPresence = PcPresenceProbe.Presence.ABSENT
        var alertShownForThisEpisode = false

        while (running) {
            try {
                val watcherEnabled = SettingsPrefs.getPresenceWatcher(this)
                if (!watcherEnabled || streamingActive) {
                    // Paused: user turned the feature off, or a real session
                    // owns the control port right now (see streamingActive
                    // doc). Treat as "unknown" -> next enabled+idle probe
                    // starts a fresh episode, and clear any stale alert once
                    // streaming took over (the user already connected).
                    if (streamingActive) cancelAlert()
                    lastPresence = PcPresenceProbe.Presence.ABSENT
                    alertShownForThisEpisode = false
                } else {
                    val now = PcPresenceProbe.probePcPresence("127.0.0.1", CONTROL_PORT)
                    if (now == PcPresenceProbe.Presence.PRESENT &&
                        lastPresence == PcPresenceProbe.Presence.ABSENT &&
                        !alertShownForThisEpisode
                    ) {
                        Log.i(TAG, "PC Host detected via control-port probe -- posting connect prompt")
                        postConnectAlert()
                        alertShownForThisEpisode = true
                    } else if (now == PcPresenceProbe.Presence.ABSENT) {
                        if (lastPresence == PcPresenceProbe.Presence.PRESENT) {
                            Log.i(TAG, "PC Host no longer reachable -- clearing connect prompt")
                            cancelAlert()
                        }
                        alertShownForThisEpisode = false
                    }
                    lastPresence = now
                }
                Thread.sleep(PROBE_INTERVAL_MS)
            } catch (e: InterruptedException) {
                return
            } catch (e: Exception) {
                // Never let a single probe error kill the watcher.
                Log.w(TAG, "Watcher loop error: ${e.message}")
                try { Thread.sleep(PROBE_INTERVAL_MS) } catch (_: InterruptedException) { return }
            }
        }
    }

    private fun createChannels() {
        val nm = getSystemService(NOTIFICATION_SERVICE) as NotificationManager
        nm.createNotificationChannel(
            NotificationChannel(CHANNEL_ONGOING, "Chờ PC DisplayBridge", NotificationManager.IMPORTANCE_MIN).apply {
                description = "Thông báo nền khi app đang chờ phát hiện PC"
            }
        )
        nm.createNotificationChannel(
            NotificationChannel(CHANNEL_ALERT, "PC sẵn sàng kết nối", NotificationManager.IMPORTANCE_HIGH).apply {
                description = "Hiện khi phát hiện PC DisplayBridge sẵn sàng"
            }
        )
    }

    private fun buildOngoingNotification(): Notification =
        Notification.Builder(this, CHANNEL_ONGOING)
            .setSmallIcon(android.R.drawable.stat_sys_data_bluetooth)
            .setContentTitle("DisplayBridge đang chờ PC")
            .setContentText("Sẽ thông báo khi phát hiện máy tính sẵn sàng.")
            .setOngoing(true)
            .build()

    private fun postConnectAlert() {
        val launch = packageManager.getLaunchIntentForPackage(packageName)?.apply {
            addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_CLEAR_TOP)
        } ?: return
        val tapIntent = PendingIntent.getActivity(
            this, 0, launch,
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE
        )
        val alert = Notification.Builder(this, CHANNEL_ALERT)
            .setSmallIcon(android.R.drawable.stat_sys_data_bluetooth)
            .setContentTitle("DisplayBridge: đã phát hiện PC")
            .setContentText("Máy tính đã sẵn sàng — chạm để kết nối màn hình phụ.")
            .setContentIntent(tapIntent)
            .setAutoCancel(true)
            .addAction(Notification.Action.Builder(null, "Kết nối", tapIntent).build())
            .build()
        (getSystemService(NOTIFICATION_SERVICE) as NotificationManager).notify(NOTIF_ID_ALERT, alert)
    }

    private fun cancelAlert() {
        (getSystemService(NOTIFICATION_SERVICE) as NotificationManager).cancel(NOTIF_ID_ALERT)
    }
}
