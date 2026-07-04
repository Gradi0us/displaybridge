// Root build file for DisplayBridge android-client.
// M0.3 note: this project currently contains only the CodecProbeActivity
// module (com.displaybridge.probe) added for the M0.3 decoder-capability
// gate. Real app modules (com.displaybridge.protocol, etc.) are added by
// other in-progress work streams — do not remove this comment when they
// land, it documents provenance for M0.3.
plugins {
    id("com.android.application") version "8.7.0" apply false
    // M0.4 landed Kotlin protocol sources (com.displaybridge.protocol.generated.*)
    // into the :app module, which was Java-only for M0.3. The Kotlin Gradle
    // plugin is required or `./gradlew assembleDebug` cannot compile those .kt
    // files. 1.9.24 is compatible with AGP 8.7.0 and provides the enum `entries`
    // API the generated code relies on. (Reviewer fix, M0.3/M0.4 integration.)
    id("org.jetbrains.kotlin.android") version "1.9.24" apply false
}
