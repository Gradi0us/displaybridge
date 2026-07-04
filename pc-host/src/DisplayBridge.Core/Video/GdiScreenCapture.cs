// GdiScreenCapture.cs — session 4 "chạy cơ bản trước" shortcut.
//
// The real M1-PC pipeline (DXGI Desktop Duplication + Media Foundation
// H.264, see NativeCaptureEncoder.cs) is blocked on MSVC v143 + Windows SDK
// not being installed (R13, see TASK-v1-tablet-display-tracker.md). Rather
// than wait for that, this class is a pure-C# fallback that proves the
// end-to-end pipeline (capture -> socket -> tablet render) actually works
// TODAY, at the cost of quality/perf: GDI screen capture + JPEG encode,
// no native DLL, no MediaCodec involvement on the Android side (see
// FrameFlags.Jpeg in VideoFrameHeader and the Android-side branch added to
// VideoDecoderActivity.kt / VideoFrameParser.kt).
//
// Deliberately NOT trying to be fast or high quality: 3-5 fps, quality
// ~55 JPEG is plenty to prove "PC screen shows up on tablet" and is easy to
// throw away once native H.264/NVENC (M2/M5) is ready.
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace DisplayBridge.Core.Video;

/// <summary>
/// Pure-C# screen capture using <see cref="Graphics.CopyFromScreen"/> +
/// JPEG encoding. Implements <see cref="IFrameSource"/> so it plugs into
/// the exact same <see cref="VideoStreamServer"/> as the native H.264
/// pipeline would -- callers (StreamingCoordinator) don't need to know
/// which one is active besides for logging.
/// </summary>
public sealed class GdiScreenCapture : IFrameSource
{
    /// <summary>
    /// Capture cadence. 200-300ms (3-5 fps) is deliberately slow -- this is
    /// a "prove it works" fallback, not a perf target (that's M2/M5's job
    /// once native H.264/NVENC is available).
    /// </summary>
    public static readonly TimeSpan CaptureInterval = TimeSpan.FromMilliseconds(250);

    // Middle-of-the-road JPEG quality: readable but small enough to push
    // over the wire at this frame rate without a real encoder.
    private const long JpegQuality = 55L;

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;
    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;

    private ImageCodecInfo? _jpegCodec;
    private EncoderParameters? _jpegParams;
    private ulong _frameCounter;
    private DateTime _lastCaptureUtc = DateTime.MinValue;
    private bool _initialized;

    public bool Init()
    {
        _jpegCodec = ImageCodecInfo.GetImageEncoders()
            .FirstOrDefault(c => c.FormatID == ImageFormat.Jpeg.Guid)
            ?? throw new InvalidOperationException("No JPEG encoder registered on this system (unexpected on Windows).");

        _jpegParams = new EncoderParameters(1);
        _jpegParams.Param[0] = new EncoderParameter(Encoder.Quality, JpegQuality);

        _initialized = true;
        _lastCaptureUtc = DateTime.MinValue; // force immediate first capture
        return true;
    }

    /// <summary>
    /// Captures the virtual screen (all monitors) and returns it as a JPEG
    /// EncodedFrame. Paces itself to <see cref="CaptureInterval"/> by
    /// blocking (Thread.Sleep) inside this call -- VideoStreamServer's
    /// serve loop already treats a null return as "retry after 5ms", so
    /// blocking here just controls our own frame rate instead of spinning.
    /// </summary>
    public EncodedFrame? GetNextFrame()
    {
        if (!_initialized)
        {
            throw new InvalidOperationException("GdiScreenCapture.Init() must succeed before GetNextFrame().");
        }

        var elapsed = DateTime.UtcNow - _lastCaptureUtc;
        var remaining = CaptureInterval - elapsed;
        if (remaining > TimeSpan.Zero)
        {
            Thread.Sleep(remaining);
        }

        var x = GetSystemMetrics(SM_XVIRTUALSCREEN);
        var y = GetSystemMetrics(SM_YVIRTUALSCREEN);
        var width = GetSystemMetrics(SM_CXVIRTUALSCREEN);
        var height = GetSystemMetrics(SM_CYVIRTUALSCREEN);
        if (width <= 0 || height <= 0)
        {
            // GetSystemMetrics failing/returning garbage -- treat as "no
            // frame this call" rather than crashing the serve loop.
            return null;
        }

        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppRgb);
        using (var gfx = Graphics.FromImage(bitmap))
        {
            gfx.CompositingQuality = CompositingQuality.HighSpeed;
            gfx.InterpolationMode = InterpolationMode.Low;
            gfx.CopyFromScreen(x, y, 0, 0, new Size(width, height), CopyPixelOperation.SourceCopy);
        }

        using var ms = new MemoryStream();
        bitmap.Save(ms, _jpegCodec!, _jpegParams);

        _lastCaptureUtc = DateTime.UtcNow;
        var ptsUs = (ulong)(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000L);
        _frameCounter++;

        // JPEG frames are always independently decodable -- there's no
        // GOP/keyframe concept here, so IsKeyframe=true for every frame is
        // correct (and harmless: the JPEG flag bit on the wire, set by
        // VideoStreamServer's caller, is what actually tells the Android
        // side to skip MediaCodec entirely -- see VideoFrameHeader.Flags /
        // FrameFlags.Jpeg).
        return new EncodedFrame(ms.ToArray(), ptsUs, IsKeyframe: true, IsJpeg: true);
    }

    public void Shutdown()
    {
        _jpegParams?.Dispose();
        _jpegParams = null;
        _initialized = false;
    }
}
