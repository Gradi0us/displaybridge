// SettingsPrefs.kt -- tablet-local streaming preferences for the floating
// settings button (VideoDecoderActivity) + MobileSettingsDialog. Per task
// brief KISS instruction: plain SharedPreferences, no DataStore.
//
// IMPORTANT distinction (see ControlSocketClient.sendCaps() callsite):
// these are "what does the USER want" values, separate from CapsMessage's
// supportedHz/supportedCodecs which report "what can the DEVICE do".
// sendCaps() filters the device capability list down to the user's
// preference (if set) before sending CAPS -- no new protocol field needed,
// reuses the PC's existing ChooseHz/ChooseCodec selection logic untouched.
package com.displaybridge.settings

import android.content.Context

object SettingsPrefs {
    private const val PREFS_NAME = "displaybridge_mobile_settings"
    private const val KEY_FPS_CAP = "fps_cap"
    private const val KEY_ENCODE_PREF = "encode_pref"

    /** No user preference set yet -- sendCaps() sends the device's full supportedHz list unfiltered. */
    const val FPS_CAP_UNSET = -1

    /** "Auto" encode preference -- sendCaps() sends the device's full supportedCodecs list unfiltered. */
    const val ENCODE_PREF_AUTO = -1

    // Match schema.yaml Config.codec CodecId exactly (0=H264, 1=HEVC) so the
    // stored preference value can be compared directly against CapsMessage's
    // supportedCodecs entries with no translation step.
    const val ENCODE_PREF_H264 = 0
    const val ENCODE_PREF_HEVC = 1

    fun getFpsCap(context: Context): Int =
        prefs(context).getInt(KEY_FPS_CAP, FPS_CAP_UNSET)

    // Real-device bug found while verifying this feature end-to-end (not
    // suspected in advance): MobileSettingsDialog.restartApp() calls
    // Runtime.getRuntime().exit(0) right after saving these prefs.
    // SharedPreferences.Editor.apply() writes to disk ASYNCHRONOUSLY on a
    // background handler thread -- exit(0) killed the whole JVM before that
    // background write ever ran, so shared_prefs/displaybridge_mobile_settings.xml
    // never got created on disk (confirmed empty via `run-as ... ls
    // shared_prefs/` right after tapping "Áp dụng" on a real tablet) even
    // though the RadioGroup selection and the apply-click both fired
    // correctly. Fix: commit() blocks the calling thread until the write is
    // flushed to disk, so it's guaranteed durable before the process exits.
    fun setFpsCap(context: Context, hz: Int) {
        prefs(context).edit().putInt(KEY_FPS_CAP, hz).commit()
    }

    fun getEncodePref(context: Context): Int =
        prefs(context).getInt(KEY_ENCODE_PREF, ENCODE_PREF_AUTO)

    fun setEncodePref(context: Context, codecId: Int) {
        prefs(context).edit().putInt(KEY_ENCODE_PREF, codecId).commit()
    }

    private fun prefs(context: Context) =
        context.applicationContext.getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE)
}
