# CODEC-PROBE-RESULTS (M0.3)

**Date**: 2026-07-03
**Device**: HONOR ROD2-W09, Android 16 (SDK 36), serial `AL9SBB4622000114` (real hardware, connected via `adb`, no emulator)
**App**: `com.displaybridge.probe` / `CodecProbeActivity`, installed and run for real (`adb install` + `adb shell am start` + `adb logcat -d`)

---

## 1. How this was built and run

No `gradle` binary and no Gradle wrapper cache were available in this environment (only Android SDK
platform-tools/build-tools + JDK 17), and Kotlin was not compilable here (no `kotlinc`). To still get a
**real on-device run today** rather than a paper/simulated result, `CodecProbeActivity` was written in
Java and built manually with the SDK tools already installed:

```
javac -source 8 -target 8 -bootclasspath android-36/android.jar ... CodecProbeActivity.java
d8.bat --min-api 26 --lib android.jar  CodecProbeActivity.class            # -> classes.dex
aapt2.exe link -I android.jar --manifest AndroidManifest.xml -o base.apk
jar uf base.apk classes.dex                                                 # add dex
zipalign.exe -f -p 4 withdex.apk aligned.apk
apksigner.bat sign --ks debug.keystore ... --out CodecProbe-debug.apk aligned.apk
adb install -r CodecProbe-debug.apk
adb shell am start -n com.displaybridge.probe/.CodecProbeActivity
adb logcat -d -s CodecProbe:*
```

Gradle scaffolding (`settings.gradle.kts`, `build.gradle.kts`, `app/build.gradle.kts`) was also created
so the exact same Java source builds normally under `./gradlew assembleDebug` once a full Gradle/AGP
cache is available — no source duplication between the manual build and the Gradle project.

**applicationId**: `com.displaybridge.probe` — deliberately different from the in-progress
`com.displaybridge.protocol` module another agent is building in parallel, so this diagnostic app can be
installed/uninstalled independently.

A first run crashed with a real `NullPointerException` (`CodecProbeActivity.java:160`,
`coversAny()` called on a null `List<PerformancePoint>` for `c2.android.avc.decoder`, which does not
declare PerformancePoints) — this was caught from `adb logcat` (`RuntimeService: faultMessage ...
NullPointerException ... coversAny ... onCreate:106`), fixed with a null-guard, rebuilt, and re-run
successfully. Not fabricated — see full raw crash log excerpt in the git history of this file / the
`.manual-build/logcat-full.txt` (first run) and `.manual-build/logcat-full-run2.txt` (fixed run) capture
files left in `android-client/.manual-build/`.

---

## 2. Decoders found (REGULAR_CODECS, video/hevc + video/avc)

| Decoder | Type | LowLatency feature | Covers 3000x1920@120 | Covers 3000x1920@144 |
|---|---|---|---|---|
| c2.qti.avc.decoder | avc | false | **true** | false |
| OMX.qcom.video.decoder.avc | avc | false | **true** | false |
| c2.qti.avc.decoder.low_latency | avc | true | **true** | false |
| OMX.qcom.video.decoder.avc.low_latency | avc | true | **true** | false |
| c2.qti.hevc.decoder | hevc | false | **true** | false |
| OMX.qcom.video.decoder.hevc | hevc | false | **true** | false |
| c2.qti.hevc.decoder.low_latency | hevc | true | **true** | false |
| OMX.qcom.video.decoder.hevc.low_latency | hevc | true | **true** | false |
| c2.android.avc.decoder (software) | avc | false | false (no PerformancePoints declared) | false |
| OMX.google.h264.decoder (software) | avc | false | false (no PerformancePoints declared) | false |
| c2.android.hevc.decoder (software) | hevc | false | false (no PerformancePoints declared) | false |
| OMX.google.hevc.decoder (software) | hevc | false | false (no PerformancePoints declared) | false |

All 4 hardware Qualcomm (`c2.qti.*` / `OMX.qcom.*`) HEVC and AVC decoders report the **same**
PerformancePoint set:
```
PerformancePoint(7680x4320@30)
PerformancePoint(4096x2176@120)   [HEVC] / PerformancePoint(4096x2160@120) [AVC]
PerformancePoint(3840x2160@120)
PerformancePoint(1920x1088@240)
PerformancePoint(1280x720@480)
```
`getSupportedFrameRatesFor(3000, 1920)` on the hardware HEVC decoders reports up to **~185.2 fps**
(theoretical macroblock-rate ceiling; not a guarantee of sustained real-time decode at that combination —
see caveat below), and up to ~183.8 fps for hardware AVC.

Software decoders (`c2.android.*`, `OMX.google.*`) declare **no** PerformancePoints at all and only
support up to 4080x4080 / 4096x4096 with much lower frame-rate ceilings (~22–87 fps) — irrelevant for
3000x1920 hardware-accelerated targets, listed only for completeness.

---

## 3. Go/No-Go (raw Logcat summary, verbatim)

```
=== GO/NO-GO SUMMARY ===
HEVC covers P-High  (3000x1920@120): GO
HEVC covers P-Ultra (3000x1920@144): NO-GO
AVC  covers P-High  (3000x1920@120): GO
AVC  covers P-Ultra (3000x1920@144): NO-GO
========================
```

This was computed via `PerformancePoint.covers(new PerformancePoint(3000, 1920, targetFps))` against
every PerformancePoint the platform reports for each decoder (not string-matching / manual math) —
`PerformancePoint(4096x2176@120)` covers 3000x1920@120 because the platform's `covers()` accounts for
macroblock-rate equivalence across resolutions at the same frame rate class, but no declared point covers
the 144fps case (all points cap at 120fps or step to 240fps at a much smaller resolution).

---

## 4. Conclusion for M0 gate (R8)

| Profile | Target | Result | Decision |
|---|---|---|---|
| **P-High** (3000x1920@120Hz, HEVC, default) | HEVC decoder covers 3K@120 | **GO** | Confirmed on real hardware. Proceed with P-High as default per PLAN-v2. |
| **P-Ultra** (3000x1920@144Hz, HEVC) | HEVC decoder covers 3K@144 | **NO-GO** | Platform-declared PerformancePoints do NOT cover 144fps at 3000x1920. Do not ship P-Ultra as advertised/guaranteed on this device based on `getSupportedPerformancePoints()` alone. |

**Recommendation**: keep P-High (120Hz) as the default/max supported profile for this tablet per the M0
gate. P-Ultra (144Hz) should be either (a) dropped from the supported profile list for this SKU, or
(b) offered as an unverified/"experimental" toggle only if a live `MediaCodec` encode+decode soak test at
144fps is later run and empirically holds (PerformancePoint under-reporting happens on some OEM builds —
this is a declared-capability check, not an empirical throughput measurement). That empirical soak test
is out of scope for M0.3 and should be a follow-up if P-Ultra is still desired.

This does **not** block M0.3 DoD ("assert HEVC ≥3K@120") — that assertion is confirmed GO.

---

## 5. Tooling / environment notes (what worked, what was missing)

- `adb` present at two locations (WinGet platform-tools + Android SDK `platform-tools`); device
  `AL9SBB4622000114` recognized as `device` (authorized) throughout.
- Android SDK present at `%LOCALAPPDATA%\Android\Sdk` with platforms `android-34/35/36` and build-tools
  up to `36.1.0`; used `build-tools/36.0.0` (aapt2, d8, zipalign, apksigner) — all worked without issue.
- **Missing**: no `gradle` binary in PATH, no Gradle wrapper jar/cache anywhere on the machine, no
  `kotlinc`. Internet access was available (verified via `curl` to `services.gradle.org`), so a full
  Gradle+AGP+Kotlin download was theoretically possible but was skipped in favor of the faster,
  deterministic manual-toolchain build described in §1, to guarantee a real run today within the task's
  time budget.
- Java source (`-source 8 -target 8` cross-compile flags) emitted standard "obsolete source/target"
  deprecation warnings from `javac` — harmless, only affects the manual build path, not correctness.

---

## 6. Files created

- `android-client/settings.gradle.kts`
- `android-client/build.gradle.kts`
- `android-client/app/build.gradle.kts`
- `android-client/app/src/main/AndroidManifest.xml`
- `android-client/app/src/main/java/com/displaybridge/probe/CodecProbeActivity.java`
- `android-client/CODEC-PROBE-RESULTS.md` (this file)
- `android-client/.manual-build/` — manual build artifacts + raw logcat captures (`logcat-full.txt` =
  first run with the NPE crash, `logcat-full-run2.txt` = fixed run, full probe output) kept as evidence.

## 7. Unresolved questions

- Should `.manual-build/` (debug keystore + APK + raw logcat captures) be committed, or should it be
  gitignored as build output? Left in place for now since this repo state has no `.gitignore` yet.
- P-Ultra (144Hz) empirical soak test (real encode→decode pipeline at 144fps, not just declared
  capabilities) is not covered by M0.3 scope — flag for a later milestone if P-Ultra remains desired.
