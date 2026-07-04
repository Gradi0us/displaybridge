package com.displaybridge.probe;

import android.app.Activity;
import android.graphics.Color;
import android.media.MediaCodecInfo;
import android.media.MediaCodecInfo.CodecCapabilities;
import android.media.MediaCodecInfo.VideoCapabilities;
import android.media.MediaCodecInfo.VideoCapabilities.PerformancePoint;
import android.media.MediaCodecList;
import android.os.Build;
import android.os.Bundle;
import android.util.Log;
import android.widget.ScrollView;
import android.widget.TextView;

import java.util.List;

/**
 * M0.3 codec-probe: standalone diagnostic activity for DisplayBridge.
 *
 * Purpose: dump every REGULAR_CODECS decoder for video/hevc and video/avc,
 * list their MediaCodecInfo.VideoCapabilities.PerformancePoint entries, and
 * decide go/no-go for the M0 gate: does this tablet's HEVC decoder cover
 * 3000x1920 @120fps (P-High) and @144fps (P-Ultra)?
 *
 * No UI polish, no button — runs entirely in onCreate() per M0.3 spec.
 * Results are pushed to a TextView (so `adb shell dumpsys activity` /
 * screenshot can also confirm) AND to Logcat under tag "CodecProbe" so
 * `adb logcat -s CodecProbe:*` captures the full machine-readable dump.
 */
public class CodecProbeActivity extends Activity {

    private static final String TAG = "CodecProbe";

    // Target points for the M0 gate (from CONTEXT-BRIEF-v1-m0-tasks.md §2).
    private static final int TARGET_W = 3000;
    private static final int TARGET_H = 1920;
    private static final int TARGET_FPS_HIGH = 120;  // P-High
    private static final int TARGET_FPS_ULTRA = 144; // P-Ultra

    private final StringBuilder report = new StringBuilder();

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);

        line("=== DisplayBridge CodecProbe (M0.3) ===");
        line("Device: " + Build.MANUFACTURER + " " + Build.MODEL
                + " | Android " + Build.VERSION.RELEASE
                + " (SDK " + Build.VERSION.SDK_INT + ")");
        line("Target gate: HEVC decoder must cover "
                + TARGET_W + "x" + TARGET_H + "@" + TARGET_FPS_HIGH + " (P-High)"
                + " and @" + TARGET_FPS_ULTRA + " (P-Ultra)");
        line("");

        boolean hevcGoHigh = false;
        boolean hevcGoUltra = false;
        boolean avcGoHigh = false;
        boolean avcGoUltra = false;

        MediaCodecList list = new MediaCodecList(MediaCodecList.REGULAR_CODECS);
        MediaCodecInfo[] infos = list.getCodecInfos();

        for (MediaCodecInfo info : infos) {
            if (info.isEncoder()) continue;

            for (String type : info.getSupportedTypes()) {
                if (!type.equalsIgnoreCase("video/hevc") && !type.equalsIgnoreCase("video/avc")) {
                    continue;
                }

                line("--- Decoder: " + info.getName() + " | type=" + type + " ---");

                CodecCapabilities caps;
                try {
                    caps = info.getCapabilitiesForType(type);
                } catch (Exception e) {
                    line("  ERROR getting capabilities: " + e);
                    continue;
                }

                boolean lowLatency = caps.isFeatureSupported(CodecCapabilities.FEATURE_LowLatency);
                line("  FEATURE_LowLatency supported: " + lowLatency);

                VideoCapabilities vcaps = caps.getVideoCapabilities();
                if (vcaps == null) {
                    line("  No VideoCapabilities (not a video codec?)");
                    continue;
                }

                List<PerformancePoint> points = vcaps.getSupportedPerformancePoints();
                if (points == null || points.isEmpty()) {
                    line("  getSupportedPerformancePoints() returned null/empty"
                            + " (device did not declare PerformancePoints for this decoder)");
                } else {
                    for (PerformancePoint p : points) {
                        line("  PerformancePoint: " + p.toString());
                    }
                }

                // Explicit go/no-go check via PerformancePoint.covers(), independent
                // of whatever toString() prints, per M0.3 spec. Guard against
                // null (some decoders, e.g. c2.android.avc.decoder, do not
                // declare PerformancePoints at all).
                PerformancePoint targetHigh = new PerformancePoint(TARGET_W, TARGET_H, TARGET_FPS_HIGH);
                PerformancePoint targetUltra = new PerformancePoint(TARGET_W, TARGET_H, TARGET_FPS_ULTRA);

                boolean coversHigh = points != null && coversAny(points, targetHigh);
                boolean coversUltra = points != null && coversAny(points, targetUltra);

                line("  Covers " + TARGET_W + "x" + TARGET_H + "@" + TARGET_FPS_HIGH + " (P-High): " + coversHigh);
                line("  Covers " + TARGET_W + "x" + TARGET_H + "@" + TARGET_FPS_ULTRA + " (P-Ultra): " + coversUltra);

                if (type.equalsIgnoreCase("video/hevc")) {
                    hevcGoHigh = hevcGoHigh || coversHigh;
                    hevcGoUltra = hevcGoUltra || coversUltra;
                } else {
                    avcGoHigh = avcGoHigh || coversHigh;
                    avcGoUltra = avcGoUltra || coversUltra;
                }

                // Also report max supported width/height/rate & bitrate as raw
                // fallback data, in case PerformancePoints are absent/incomplete.
                try {
                    line("  Supported widths: " + vcaps.getSupportedWidths());
                    line("  Supported heights: " + vcaps.getSupportedHeights());
                    line("  Supported frame rates for " + TARGET_W + "x" + TARGET_H + ": "
                            + safeFrameRateRange(vcaps, TARGET_W, TARGET_H));
                    line("  Bitrate range: " + vcaps.getBitrateRange());
                } catch (Exception e) {
                    line("  (raw capability dump error: " + e + ")");
                }

                line("");
            }
        }

        line("=== GO/NO-GO SUMMARY ===");
        line("HEVC covers P-High  (3000x1920@120): " + goNoGo(hevcGoHigh));
        line("HEVC covers P-Ultra (3000x1920@144): " + goNoGo(hevcGoUltra));
        line("AVC  covers P-High  (3000x1920@120): " + goNoGo(avcGoHigh));
        line("AVC  covers P-Ultra (3000x1920@144): " + goNoGo(avcGoUltra));
        line("========================");

        // Push everything to Logcat, line by line (already done in line()),
        // plus a single consolidated block for easy `adb logcat -d` grep.
        Log.i(TAG, "PROBE_COMPLETE");

        TextView tv = new TextView(this);
        tv.setText(report.toString());
        tv.setTextColor(Color.WHITE);
        tv.setBackgroundColor(Color.BLACK);
        tv.setTextSize(10f);
        tv.setPadding(24, 24, 24, 24);

        ScrollView scroll = new ScrollView(this);
        scroll.addView(tv);
        setContentView(scroll);
    }

    private static boolean coversAny(List<PerformancePoint> points, PerformancePoint target) {
        for (PerformancePoint p : points) {
            try {
                if (p.covers(target)) return true;
            } catch (Exception ignored) {
                // covers() can throw on some malformed points; skip defensively.
            }
        }
        return false;
    }

    private static String safeFrameRateRange(VideoCapabilities vcaps, int w, int h) {
        try {
            if (!vcaps.isSizeSupported(w, h)) {
                return "size " + w + "x" + h + " not supported by this decoder";
            }
            return vcaps.getSupportedFrameRatesFor(w, h).toString();
        } catch (Exception e) {
            return "n/a (" + e.getMessage() + ")";
        }
    }

    private static String goNoGo(boolean go) {
        return go ? "GO" : "NO-GO";
    }

    private void line(String s) {
        Log.i(TAG, s);
        report.append(s).append('\n');
    }
}
