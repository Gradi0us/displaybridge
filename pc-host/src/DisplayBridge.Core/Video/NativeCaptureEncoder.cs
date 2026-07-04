// NativeCaptureEncoder.cs — M1.1 P/Invoke wrapper over DisplayBridge.Native.dll.
// Wraps the DXGI Desktop Duplication + Media Foundation H.264 encoder
// pipeline exported by CaptureEncodeExports.cpp. This class is
// UNTESTED against the real native DLL (native build blocked — see
// CONTEXT-BRIEF §5 / final report); it is exercised indirectly via
// IFrameSource in VideoStreamServerTests using a fake source instead.
using System.Runtime.InteropServices;

namespace DisplayBridge.Core.Video;

/// <summary>
/// One encoded video frame ready for <see cref="VideoStreamServer"/> to
/// write to the wire. Normally an H.264 access unit from the native
/// encoder, but <see cref="IsJpeg"/> lets a fallback <see cref="IFrameSource"/>
/// (see GdiScreenCapture.cs, session 4 "chạy cơ bản trước" shortcut) mark a
/// frame as a standalone JPEG image instead -- VideoStreamServer sets the
/// corresponding wire flag bit (see FrameFlags) so the Android side knows
/// to bypass MediaCodec entirely for that frame (see VideoFrameParser.kt /
/// VideoDecoderActivity.kt).
/// </summary>
public sealed record EncodedFrame(byte[] Data, ulong PtsUs, bool IsKeyframe, bool IsJpeg = false);

/// <summary>
/// Bit layout of <c>VideoFrameHeader.Flags</c> (schema.yaml: "bit0=keyframe/IDR,
/// bits1-7=reserved"). Session 4 claims bit1 (previously reserved) for the
/// JPEG-fallback marker -- see docs/TASK-v1-tablet-display-tracker.md
/// session 4 for why this shortcut exists and schema.yaml's updated doc
/// comment for the bit1 assignment.
/// </summary>
public static class FrameFlags
{
    public const byte Keyframe = 0x01;

    /// <summary>
    /// Set when the payload is a standalone JPEG image (GdiScreenCapture
    /// fallback), not an H.264/HEVC NAL unit. When set, the receiver MUST
    /// decode with an image decoder (BitmapFactory on Android) instead of
    /// feeding the payload to MediaCodec.
    /// </summary>
    public const byte Jpeg = 0x02;
}

/// <summary>
/// Thin P/Invoke façade over DisplayBridge.Native's capture+encode
/// exports. Not thread-safe — callers should serialize Init/GetNextFrame/
/// Shutdown from a single capture loop thread.
/// </summary>
public sealed class NativeCaptureEncoder : IFrameSource, IDisposable
{
    private const string NativeLib = "DisplayBridge.Native.dll";

    [DllImport(NativeLib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int DisplayBridge_CaptureInit();

    // Session 12: lets the caller pick which codec the native encoder MFT
    // negotiates (0=H264, 1=HEVC, matches VideoCodecType in H264Encoder.h /
    // schema.yaml Config.codec). Previously the codec StreamingCoordinator
    // chose via ChooseCodec() was only ever used for the wire CONFIG
    // message -- native always hardcoded H.264 regardless, a real wiring
    // bug fixed here.
    [DllImport(NativeLib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int DisplayBridge_CaptureInitWithCodec(int codec);

    // Session 14 bug fix: DisplayBridge_CaptureInitWithCodec never took a
    // fps/bitrate parameter at all, so native always used its own
    // kDefaultFps=60/kDefaultBitrateKbps=12000 constants no matter what
    // ChooseHz()/EstimateBitrateKbps() computed for the actually-connected
    // device (60/90/120Hz negotiated, ~19-83 Mbps depending on resolution
    // per PLAN-v2 §5) -- wrong GOP cadence/PTS pacing at higher Hz and a
    // bitrate 5-6x too low for 3000x1920, both directly contributing to the
    // "very laggy" report independent of the GPU color-conversion fix.
    [DllImport(NativeLib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int DisplayBridge_CaptureInitWithCodecFpsBitrate(int codec, uint fps, uint bitrateKbps);

    [DllImport(NativeLib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int DisplayBridge_CaptureGetEncoderDiagnostics();

    [DllImport(NativeLib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int DisplayBridge_CaptureGetFrame(
        out IntPtr outNalData,
        out uint outNalLen,
        out ulong outPtsUs,
        out byte outFlags);

    [DllImport(NativeLib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void DisplayBridge_CaptureFreeFrame(IntPtr nalData);

    [DllImport(NativeLib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void DisplayBridge_CaptureShutdown();

    [DllImport(NativeLib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int DisplayBridge_CaptureUsedFallbackPrimary();

    [DllImport(NativeLib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void DisplayBridge_CaptureGetSize(out uint outWidth, out uint outHeight);

    private bool _initialized;
    private bool _disposed;

    // Session 16 deadlock fix: VideoStreamServer.Stop() only waits 2s for
    // in-flight serve loops; if one is still blocked inside GetNextFrame()
    // (native AcquireFrame vsync wait) when RecreateFrameSourceForNewResolution
    // calls Dispose() on this object from another thread, the native
    // g_capture/g_encoder globals get torn down mid-call -- observed live as
    // the whole host wedging after the Android client force-stopped to apply
    // a codec change. Serializing GetNextFrame/Shutdown on this lock bounds
    // Shutdown's wait to one AcquireFrame timeout (<=500ms) and makes the
    // teardown safe.
    private readonly object _nativeCallLock = new();

    /// <summary>
    /// True once Init() has run and the native layer could NOT find the
    /// "VDD by MTT" virtual monitor, so it fell back to mirroring the
    /// primary (laptop) output -- the OLD, undesired behavior fixed by
    /// RESEARCH-v2-spacedesk-extend-mode-mechanism.md. Callers (see
    /// StreamingCoordinator.cs) MUST log a loud warning when this is true.
    /// </summary>
    public bool UsedFallbackPrimaryDisplay { get; private set; }

    /// <summary>Actual width/height of the output being captured (0,0 before Init()).</summary>
    public (uint Width, uint Height) CapturedSize { get; private set; }

    /// <summary>
    /// True once Init() has confirmed the native encoder is actually doing
    /// BGRA->NV12 color conversion on the GPU (ID3D11VideoDevice's
    /// VideoProcessorBlt) rather than the old per-pixel CPU loop. False
    /// means every frame is going through the CPU fallback (old GPU
    /// driver, or a runtime Blt failure downgraded it for this session) --
    /// see H264Encoder.cpp's InitGpuColorConversion()/SubmitFrame().
    /// </summary>
    public bool UsingGpuColorConversion { get; private set; }

    /// <summary>
    /// True when the encoder MFT accepts D3D11 surfaces directly
    /// (zero-copy, no CPU map of the GPU-converted NV12 texture at all).
    /// Only meaningful when <see cref="UsingGpuColorConversion"/> is true.
    /// </summary>
    public bool UsingZeroCopyD3D11Input { get; private set; }

    /// <summary>True if the native encoder actually negotiated HEVC (vs H.264) with the MFT.</summary>
    public bool UsingHevc { get; private set; }

    /// <summary>
    /// IFrameSource.Init() — parameterless overload required by the
    /// interface (StubFrameSource/GdiScreenCapture callers, existing
    /// tests). Always negotiates H.264, identical to pre-session-12
    /// behavior. StreamingCoordinator.cs uses the Init(int) overload below
    /// instead so it can pass through the actually-chosen codec.
    /// </summary>
    public bool Init() => Init(0);

    /// <summary>
    /// Initializes the native capture+encode pipeline with the given wire
    /// codec (0=H264, 1=HEVC — matches schema.yaml Config.codec /
    /// VideoCodecType in H264Encoder.h). Returns true on success (native
    /// error code 0). On failure, the native error code can be inspected
    /// via the exception message for diagnostics.
    /// </summary>
    public bool Init(int wireCodec) => Init(wireCodec, fps: 0, bitrateKbps: 0);

    /// <summary>
    /// Session 14: real fps/bitrateKbps overload. StreamingCoordinator MUST
    /// call this one (not the codec-only overload above) so the native
    /// encoder actually targets the negotiated Hz/bitrate instead of the
    /// hardcoded 60fps/12Mbps defaults -- see
    /// DisplayBridge_CaptureInitWithCodecFpsBitrate's header comment in
    /// CaptureEncodeExports.cpp for why that mismatch mattered. fps=0/
    /// bitrateKbps=0 (the Init(int) overload's behavior) falls back to the
    /// native defaults, kept for existing stub/test callers.
    /// </summary>
    public bool Init(int wireCodec, uint fps, uint bitrateKbps)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_initialized) return true;

        var result = DisplayBridge_CaptureInitWithCodecFpsBitrate(wireCodec, fps, bitrateKbps);
        _initialized = result == 0;
        if (!_initialized)
        {
            throw new InvalidOperationException($"DisplayBridge_CaptureInitWithCodec({wireCodec}) failed with native error code {result}.");
        }

        UsedFallbackPrimaryDisplay = DisplayBridge_CaptureUsedFallbackPrimary() != 0;
        DisplayBridge_CaptureGetSize(out var w, out var h);
        CapturedSize = (w, h);

        var diag = DisplayBridge_CaptureGetEncoderDiagnostics();
        UsingGpuColorConversion = (diag & 0x01) != 0;
        UsingZeroCopyD3D11Input = (diag & 0x02) != 0;
        UsingHevc = (diag & 0x04) != 0;
        return true;
    }

    /// <summary>
    /// Blocks briefly waiting for the next encoded frame. Returns null if
    /// no frame was available within the native timeout (caller should
    /// retry) rather than throwing, since timeouts are expected/normal.
    /// </summary>
    public EncodedFrame? GetNextFrame()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_initialized)
        {
            throw new InvalidOperationException("NativeCaptureEncoder.Init() must succeed before GetNextFrame().");
        }

        lock (_nativeCallLock)
        {
            if (!_initialized)
            {
                // Shutdown() won the lock race while we were waiting -- the
                // native pipeline is gone, report "no frame" so the serve
                // loop exits cleanly instead of calling into freed globals.
                return null;
            }

            var result = DisplayBridge_CaptureGetFrame(out var nativePtr, out var len, out var ptsUs, out var flags);
            if (result != 0)
            {
                // Non-zero includes "timeout, try again" cases (see native
                // CaptureError::AcquireFrameTimeout) — treat uniformly as
                // "no frame this call" for the PoC caller loop.
                return null;
            }

            if (nativePtr == IntPtr.Zero || len == 0)
            {
                return null;
            }

            try
            {
                var managed = new byte[len];
                Marshal.Copy(nativePtr, managed, 0, checked((int)len));
                var isKeyframe = (flags & 0x01) != 0;
                return new EncodedFrame(managed, ptsUs, isKeyframe);
            }
            finally
            {
                DisplayBridge_CaptureFreeFrame(nativePtr);
            }
        }
    }

    public void Shutdown()
    {
        lock (_nativeCallLock)
        {
            if (!_initialized) return;
            DisplayBridge_CaptureShutdown();
            _initialized = false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        Shutdown();
        _disposed = true;
    }
}
