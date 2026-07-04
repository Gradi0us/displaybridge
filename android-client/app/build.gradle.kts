// App module. Written in Java (not Kotlin) for M0.3's CodecProbeActivity:
// this dev environment has no kotlinc / gradle wrapper cache available,
// so the M0.3 probe was authored in Java and built manually with
// javac + d8 + aapt2 (see android-client/CODEC-PROBE-RESULTS.md for the
// exact manual-build commands used to get a real run on-device today).
// This build.gradle.kts lets the same source build under a normal
// `./gradlew assembleDebug` once a full Gradle/AGP cache is available —
// no source duplication, no drift.
plugins {
    id("com.android.application")
    // Required so the M0.4 Kotlin protocol sources under
    // com.displaybridge.protocol.generated (Messages.kt, WireIO.kt, etc.) that
    // live in this same :app module compile alongside the M0.3 Java probe.
    // Without this plugin, `./gradlew assembleDebug` silently drops / fails on
    // the .kt files. Java and Kotlin coexist in one module via this plugin.
    id("org.jetbrains.kotlin.android")
}

android {
    namespace = "com.displaybridge.probe"
    compileSdk = 36

    defaultConfig {
        applicationId = "com.displaybridge.probe"
        minSdk = 26
        targetSdk = 36
        versionCode = 1
        versionName = "0.0.1-m0.3"
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    // Keep Kotlin (M0.4 protocol sources) on the same JVM target as the Java
    // (M0.3 probe) so both halves of this mixed Java+Kotlin module agree.
    kotlinOptions {
        jvmTarget = "17"
    }
}

dependencies {
    // M0.4: JVM-only unit tests for the generated protocol roundtrip
    // (android-client/app/src/test/java/.../protocol/MessagesRoundtripTest.kt).
    // No Android framework / instrumentation needed — pure Kotlin I/O.
    testImplementation("junit:junit:4.13.2")
}
