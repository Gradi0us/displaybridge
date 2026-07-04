// MobileSettingsDialog.kt -- floating-button Settings screen (Việc 2).
//
// Plain framework android.app.Dialog, not DialogFragment: this module has
// no androidx/Fragment dependency at all (see app/build.gradle.kts -- only
// junit for tests), and a Fragment would pull in a whole new dependency
// just to show a couple of RadioGroups. A Dialog needs zero new deps and
// needs no lifecycle management beyond the hosting Activity, matching the
// task brief's "chọn cách nào đơn giản hơn" instruction.
//
// UI is built entirely in code (no layout XML), same style already used
// throughout VideoDecoderActivity (buildWaitingOverlay/buildFpsOverlayText).
package com.displaybridge.settings

import android.app.Activity
import android.app.Dialog
import android.content.Intent
import android.os.Bundle
import android.view.Gravity
import android.view.View
import android.widget.Button
import android.widget.LinearLayout
import android.widget.RadioButton
import android.widget.RadioGroup
import android.widget.TextView

class MobileSettingsDialog(private val activity: Activity) : Dialog(activity) {

    companion object {
        private val FPS_OPTIONS = listOf(60, 90, 120)
        private val ENCODE_OPTIONS = listOf(
            "Auto" to SettingsPrefs.ENCODE_PREF_AUTO,
            "H.264" to SettingsPrefs.ENCODE_PREF_H264,
            "H.265 (HEVC)" to SettingsPrefs.ENCODE_PREF_HEVC
        )
    }

    private lateinit var fpsGroup: RadioGroup
    private lateinit var encodeGroup: RadioGroup

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setTitle("Cài đặt Streaming")
        setContentView(buildLayout())
    }

    private fun dp(value: Int) = (value * activity.resources.displayMetrics.density).toInt()

    private fun buildLayout(): LinearLayout {
        val root = LinearLayout(activity).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(dp(24), dp(16), dp(24), dp(8))
        }

        root.addView(sectionLabel("FPS cap"))
        fpsGroup = buildFpsGroup()
        root.addView(fpsGroup)

        root.addView(sectionLabel("Encode"))
        encodeGroup = buildEncodeGroup()
        root.addView(encodeGroup)

        root.addView(buildButtonRow())
        return root
    }

    private fun sectionLabel(label: String): TextView = TextView(activity).apply {
        text = label
        setPadding(0, dp(12), 0, dp(4))
    }

    private fun buildFpsGroup(): RadioGroup {
        val group = RadioGroup(activity).apply { orientation = RadioGroup.HORIZONTAL }
        val currentFpsCap = SettingsPrefs.getFpsCap(activity)
        FPS_OPTIONS.forEach { hz ->
            val button = RadioButton(activity).apply {
                id = View.generateViewId()
                text = "${hz}Hz"
                tag = hz
            }
            group.addView(button)
            // Default selection when nothing saved yet: highest cap (120) --
            // matches the "no filter" behavior sendCaps() already had before
            // this feature existed (device's own max, up to 120).
            if (hz == currentFpsCap || (currentFpsCap == SettingsPrefs.FPS_CAP_UNSET && hz == FPS_OPTIONS.last())) {
                button.isChecked = true
            }
        }
        return group
    }

    private fun buildEncodeGroup(): RadioGroup {
        // Session 16 bug fix: this group was HORIZONTAL like the FPS one, but
        // its labels are much wider ("H.265 (HEVC)") -- on the real tablet the
        // third radio rendered partially/fully outside the dialog's width, so
        // taps meant for H.265 either missed or landed on H.264. Evidence:
        // user reported choosing H.265 twice, yet shared_prefs on the device
        // held encode_pref=0 while fps_cap=120 from the SAME apply saved fine
        // (the save path itself was proven good by hand-writing encode_pref=1
        // via run-as -- the PC then received CAPS codecs=1 immediately).
        // VERTICAL guarantees every option is inside the dialog and tappable.
        val group = RadioGroup(activity).apply { orientation = RadioGroup.VERTICAL }
        val currentEncodePref = SettingsPrefs.getEncodePref(activity)
        ENCODE_OPTIONS.forEach { (label, codecId) ->
            val button = RadioButton(activity).apply {
                id = View.generateViewId()
                text = label
                tag = codecId
            }
            group.addView(button)
            if (codecId == currentEncodePref) button.isChecked = true
        }
        return group
    }

    private fun buildButtonRow(): LinearLayout {
        val row = LinearLayout(activity).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.END
            setPadding(0, dp(16), 0, 0)
        }
        row.addView(Button(activity).apply {
            text = "Hủy"
            setOnClickListener { dismiss() }
        })
        row.addView(Button(activity).apply {
            text = "Áp dụng (khởi động lại app)"
            setOnClickListener { onApplyClicked() }
        })
        return row
    }

    private fun selectedTag(group: RadioGroup, fallback: Int): Int {
        val checkedId = group.checkedRadioButtonId
        if (checkedId == View.NO_ID) return fallback
        val button = group.findViewById<RadioButton>(checkedId) ?: return fallback
        return button.tag as? Int ?: fallback
    }

    private fun onApplyClicked() {
        val fpsCap = selectedTag(fpsGroup, SettingsPrefs.FPS_CAP_UNSET)
        val encodePref = selectedTag(encodeGroup, SettingsPrefs.ENCODE_PREF_AUTO)
        SettingsPrefs.setFpsCap(activity, fpsCap)
        SettingsPrefs.setEncodePref(activity, encodePref)

        // Session 16: read BACK from disk and show it, so a silent
        // save-vs-selection mismatch (like the horizontal-clip bug above) is
        // user-visible immediately instead of only diagnosable via run-as.
        val savedFps = SettingsPrefs.getFpsCap(activity)
        val savedEncode = SettingsPrefs.getEncodePref(activity)
        val encodeLabel = ENCODE_OPTIONS.firstOrNull { it.second == savedEncode }?.first ?: "?"
        android.util.Log.i("MobileSettingsDialog", "Saved prefs: fps_cap=$savedFps encode_pref=$savedEncode")
        android.widget.Toast.makeText(
            activity,
            "Đã lưu: ${if (savedFps == SettingsPrefs.FPS_CAP_UNSET) "Auto" else "${savedFps}Hz"} / $encodeLabel — khởi động lại...",
            android.widget.Toast.LENGTH_LONG
        ).show()

        dismiss()
        // Give the Toast a beat to render before the process dies --
        // Runtime.exit(0) inside restartApp() would otherwise kill it
        // before it ever appears.
        android.os.Handler(android.os.Looper.getMainLooper())
            .postDelayed({ restartApp() }, 1200)
    }

    /**
     * Việc 2.3: restart the whole process so ControlSocketClient's next
     * sendCaps() picks up the new SharedPreferences values on a clean
     * handshake. Standard KISS restart pattern per task brief: relaunch via
     * PackageManager's own launch intent, then kill this process outright
     * rather than attempting any live-apply of FPS/codec mid-stream.
     */
    private fun restartApp() {
        val packageManager = activity.packageManager
        val launchIntent = packageManager.getLaunchIntentForPackage(activity.packageName)
        if (launchIntent != null) {
            launchIntent.addFlags(Intent.FLAG_ACTIVITY_CLEAR_TASK or Intent.FLAG_ACTIVITY_NEW_TASK)
            activity.startActivity(launchIntent)
        }
        Runtime.getRuntime().exit(0)
    }
}
