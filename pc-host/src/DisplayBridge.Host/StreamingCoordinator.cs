// StreamingCoordinator.cs — wiring-E2E entrypoint (session 3).
//
// Session 2 left M1 (video capture/encode + decode), M3 (Hybrid Cursor/
// Touch input) and M4 (settings store + classifier) as three code-complete
// but never-connected islands (see TASK-v1-tablet-display-tracker.md
// "Fable 5 overview" — 0 frames ever crossed the wire). This class is the
// single point that wires them into one running pipeline:
//   - NativeCaptureEncoder (M1-PC) -> VideoStreamServer (29500)
//   - ControlSocketServer (29501, new) -> InputDispatcher (M3) and
//     SettingsStore/SettingsChangeClassifier (M4)
//   - CAPS/CONFIG handshake updates DeviceCaps from the real connected
//     device instead of the hardcoded 2560x1600 placeholder.
//
// Deliberately NOT referenced from App.xaml.cs/MainWindow.xaml.cs's UI
// logic beyond a single OnStartup call (see App.xaml.cs) -- this class
// owns the actual runtime wiring so it stays testable headless (see
// pc-host/tests/Integration.Tests/EndToEndWiringTests.cs, driven by
// tools/fake-device, no tablet or native DLL required).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using DisplayBridge.Core.Control;
using DisplayBridge.Core.Input;
using DisplayBridge.Core.Protocol.Generated;
using DisplayBridge.Core.Settings;
using DisplayBridge.Core.Video;

// Session 15 (no-ADB auto-disable task): lets Integration.Tests call the
// poll-tick logic directly/synchronously instead of waiting real wall-clock
// seconds for AdbPollFirstDelay/AdbPollInterval to elapse.
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Integration.Tests")]

namespace DisplayBridge.Host;

public sealed class StreamingCoordinator : IDisposable
{
    private readonly SettingsStore _settingsStore;
    private AppSettings _settings;
    private DeviceCaps _deviceCaps = DeviceCaps.Placeholder;

    private IFrameSource? _frameSource;
    private VideoStreamServer? _videoServer;
    private ControlSocketServer? _controlServer;

    private readonly InputModeClassifier _inputClassifier = new();
    private readonly ICursorInjector _cursorInjector;
    private readonly ITouchInjector _touchInjector;
    private InputDispatcher? _inputDispatcher;
    private readonly VirtualDisplayConfigurator _vddConfigurator = new();

    // Shared with _cursorInjector/_touchInjector's default construction below
    // (both default to `new VirtualMonitorLocator()` when no fake is
    // injected) so its cache can be invalidated from exactly one place
    // whenever display topology changes (EnsureExtendTopology / driver
    // restart) -- see VirtualMonitorLocator.cs header comment.
    private readonly IVirtualMonitorLocator _monitorLocator = new VirtualMonitorLocator();

    // Driver self-management task: owns install/restart of "VDD by MTT" so
    // no separate "Virtual Driver Control.exe" tool or manual Device
    // Manager step is needed anymore. Wraps _vddConfigurator so restart
    // logic isn't duplicated (see DriverManager.cs header comment).
    // Typed as the IDriverManager seam (session 11) so
    // Integration.Tests can inject a fake that returns success without
    // shelling out to real devcon.exe/pnputil.exe (which aren't bundled
    // next to the test binary -- see EnsureReady's real behavior in CI).
    private readonly IDriverManager _driverManager;

    // No-ADB auto-disable task (session 15): "nếu không có adb được kết nối
    // thì sẽ tạm thời ngưng hoạt động của driver đi để k bị nhầm thành 2
    // màn" -- avoids Windows showing "VDD by MTT" as a genuine second
    // monitor when no tablet is actually plugged in over ADB. Polling
    // (rather than an ADB event/callback API, which doesn't exist for a
    // plain `adb devices` shell-out) via a background Timer; see
    // OnAdbPollTick for the debounce/transition logic and Start() for the
    // delayed-first-check rationale.
    private readonly IAdbDeviceChecker _adbChecker;
    private System.Threading.Timer? _adbPollTimer;
    private bool? _lastKnownAdbConnected;
    private int _adbPollInProgress;

    /// <summary>
    /// First check waits this long after Start() so a user who just opened
    /// the app and is still plugging in the USB cable for the first time
    /// doesn't get the driver yanked out from under them before ADB even
    /// has a chance to enumerate the device (task brief: "để không tắt
    /// driver ngay lúc app vừa mở khi user còn đang cắm cáp").
    /// </summary>
    internal static readonly TimeSpan AdbPollFirstDelay = TimeSpan.FromSeconds(20);

    /// <summary>Steady-state poll interval once the first check has run (task brief: mỗi 5-10 giây).</summary>
    internal static readonly TimeSpan AdbPollInterval = TimeSpan.FromSeconds(7);

    // Test-only seam (session 11): lets Integration.Tests exercise the
    // resolution-change-mid-stream MODE_CHANGE-ordering fix (see
    // SendModeChangeBeforeVideoPipelineRecreate/RecreateFrameSourceForNewResolution)
    // WITHOUT going through real DXGI/D3D11 capture teardown+recreate twice
    // in one test process -- CreateFrameSource()'s real path is process-global
    // native state (see VideoStreamServer.cs header comment on why Stop()
    // blocks for in-flight client tasks) and was empirically found to crash
    // a LATER, unrelated test with an access violation when a fake
    // DriverManager was used to force two real recreate cycles back to back
    // on a machine where the native DLL/driver genuinely work. Defaults to
    // null so production and all pre-existing tests are unaffected.
    private readonly Func<IFrameSource>? _frameSourceFactoryOverride;

    private bool _disposed;

    /// <summary>
    /// Wire codec (0=H264, 1=HEVC) to pass to NativeCaptureEncoder.Init()
    /// the NEXT time CreateFrameSource() runs. Session 12 bug fix: ChooseCodec()'s
    /// result used to be computed in OnCapsReceived only for the wire CONFIG
    /// message and never actually reached the native encoder init call --
    /// native always hardcoded H.264 regardless of what was "chosen" for
    /// the tablet. Defaults to the user's forced preference if set (so the
    /// very first CreateFrameSource() in Start(), which runs before any
    /// CAPS has arrived, at least honors an explicit Force setting instead
    /// of always guessing H264); Auto defaults to H264 until the first CAPS
    /// handshake supplies the tablet's real supported codec list.
    /// </summary>
    private int _activeCodec;

    /// <summary>
    /// Session 14 bug fix companion to _activeCodec: the real negotiated Hz
    /// (60/90/120) and bitrate (EstimateBitrateKbps, resolution-appropriate)
    /// to pass into NativeCaptureEncoder.Init() -- previously never reached
    /// native at all (see NativeCaptureEncoder.Init(int,uint,uint) header
    /// comment), so the encoder always used its hardcoded 60fps/12Mbps
    /// defaults regardless of what CAPS/settings actually negotiated. 0
    /// before the first CAPS arrives, same "let native pick its default"
    /// convention as _activeCodec's initial seed below.
    /// </summary>
    private uint _activeFps;
    private uint _activeBitrateKbps;

    /// <summary>True once NativeCaptureEncoder.Init() actually succeeded (real video flowing). False = stub/test mode.</summary>
    public bool NativeCaptureAvailable { get; private set; }

    /// <summary>True once the active NativeCaptureEncoder confirmed GPU (VideoProcessorBlt) color conversion is running, not the old CPU per-pixel loop.</summary>
    public bool NativeUsingGpuColorConversion { get; private set; }

    public DeviceCaps CurrentDeviceCaps => _deviceCaps;
    public AppSettings CurrentSettings => _settings;

    /// <summary>
    /// Exposes the same SettingsStore instance the coordinator itself reads
    /// from/writes to, so the Settings UI (App.xaml.cs / MainWindow.xaml.cs)
    /// edits the exact same on-disk file instead of a second SettingsStore
    /// pointed at the same default path by coincidence.
    /// </summary>
    public SettingsStore SettingsStore => _settingsStore;
    public int VideoBoundPort => _videoServer?.BoundPort ?? -1;
    public int ControlBoundPort => _controlServer?.BoundPort ?? -1;

    public event Action<string>? Log;
    public event Action? ClientConnected;

    public StreamingCoordinator(SettingsStore? settingsStore = null, ICursorInjector? cursorInjector = null, ITouchInjector? touchInjector = null, IDriverManager? driverManager = null, Func<IFrameSource>? frameSourceFactory = null, IAdbDeviceChecker? adbChecker = null)
    {
        _settingsStore = settingsStore ?? new SettingsStore();
        _settings = _settingsStore.Load(_deviceCaps);
        _cursorInjector = cursorInjector ?? new CursorInjector(monitorLocator: _monitorLocator);
        _touchInjector = touchInjector ?? new TouchInjector(monitorLocator: _monitorLocator);
        _driverManager = driverManager ?? new DriverManager(_vddConfigurator);
        _frameSourceFactoryOverride = frameSourceFactory;
        _adbChecker = adbChecker ?? new AdbDeviceChecker();
        // Seed from the user's forced preference (if any) so the very
        // first CreateFrameSource() in Start() -- before any CAPS has
        // arrived -- at least honors an explicit Force setting; Auto falls
        // back to H264 (0) until the real CAPS handshake tells us what the
        // tablet supports (see OnCapsReceived, which then updates this and
        // recreates the frame source with the correct codec).
        _activeCodec = _settings.Streaming.Codec == Codec.ForceHevc ? 1 : 0;
    }

    /// <summary>
    /// Starts capture+encode (best-effort), the video server, and the
    /// control server. Safe to call even when the native DLL can't be
    /// loaded (C++ toolchain unbuilt, see docs/... R13) -- falls back to
    /// GdiScreenCapture (pure C# JPEG screen capture, session 4) so real
    /// frames still flow end-to-end; if even that fails (e.g. headless),
    /// falls back further to a stub frame source that keeps the video port
    /// alive with zero frames so control/input/settings wiring can still be
    /// exercised (see fake-device based EndToEndWiringTests.cs).
    /// </summary>
    public void Start(int? videoPort = null, int? controlPort = null)
    {
        // RESEARCH-v2 fix, session 7: must force Extend topology BEFORE
        // creating the frame source -- if "VDD by MTT" is in Windows'
        // default Clone/Duplicate mode it shares the primary's \\.\DISPLAYn
        // and DesktopDuplicationCapture ends up duplicating primary content
        // even though it correctly identifies the VDD monitor by hardware
        // ID. Empirically verified: no Administrator privileges required.
        var extended = _vddConfigurator.EnsureExtendTopology();
        Log?.Invoke(extended
            ? "VirtualDisplayConfigurator: da chuyen Windows sang che do Extend (hoac da o Extend san)."
            : "VirtualDisplayConfigurator: KHONG the chuyen sang Extend topology (SetDisplayConfig that bai) -- 'VDD by MTT' co the van dang Clone/Duplicate voi man chinh.");
        _monitorLocator.Invalidate();

        _frameSource = CreateFrameSource();

        _videoServer = new VideoStreamServer(_frameSource, videoPort ?? _settings.Connection.VideoPort);
        _videoServer.FrameWritten += OnFrameWritten;
        _videoServer.Start();
        Log?.Invoke($"VideoStreamServer listening on {_videoServer.BoundPort} (native={NativeCaptureAvailable})");

        _inputDispatcher = new InputDispatcher(_inputClassifier, _cursorInjector, _touchInjector);

        _controlServer = new ControlSocketServer(controlPort ?? _settings.Connection.ControlPort);
        _controlServer.CapsReceived += OnCapsReceived;
        _controlServer.TouchEventReceived += OnTouchEventReceived;
        _controlServer.TouchBatchReceived += OnTouchBatchReceived;
        _controlServer.SettingRequestReceived += OnSettingRequestReceived;
        _controlServer.ModeAckReceived += ack => Log?.Invoke($"MODE_ACK status={ack.Status}");
        _controlServer.StatsReceived += stats => Log?.Invoke($"STATS fps={stats.Fps} decodeMs={stats.DecodeMs} dropped={stats.Dropped}");
        _controlServer.ClientError += ex => Log?.Invoke($"Control client error: {ex.Message}");
        _controlServer.Start();
        Log?.Invoke($"ControlSocketServer listening on {_controlServer.BoundPort}");

        // No-ADB auto-disable task (session 15): background poll starts here,
        // AFTER both servers are up, with a long first delay (see
        // AdbPollFirstDelay) so a user still plugging in the tablet for the
        // first time doesn't get the driver disabled out from under them.
        // System.Threading.Timer callbacks run on a ThreadPool thread, so
        // this never blocks Start()/the caller.
        _adbPollTimer = new System.Threading.Timer(OnAdbPollTick, null, AdbPollFirstDelay, AdbPollInterval);
        Log?.Invoke($"ADB auto-disable poll: se kiem tra lan dau sau {AdbPollFirstDelay.TotalSeconds:F0}s, sau do moi {AdbPollInterval.TotalSeconds:F0}s.");
    }

    /// <summary>
    /// Timer callback (session 15): checks real ADB connectivity and
    /// disables/enables "VDD by MTT" on a genuine state TRANSITION only
    /// (never on every tick) so devcon isn't shelled out to redundantly
    /// every 7s while the connection state hasn't actually changed --
    /// this is the debounce the task brief asked for.
    /// Indeterminate (adb.exe missing/timeout/error) deliberately leaves
    /// _lastKnownAdbConnected untouched: we must never guess-disable the
    /// driver just because we couldn't run adb this one time.
    /// Guarded against overlapping runs (Interlocked flag) in case a check
    /// somehow takes longer than the poll interval -- devcon/adb calls are
    /// already timeout-bounded, but this is a cheap extra safety net.
    /// </summary>
    internal void OnAdbPollTick(object? state)
    {
        if (_disposed) return;
        if (System.Threading.Interlocked.Exchange(ref _adbPollInProgress, 1) == 1)
        {
            return;
        }

        try
        {
            var connState = _adbChecker.CheckConnectionState();
            if (connState == AdbConnectionState.Indeterminate)
            {
                Log?.Invoke("ADB poll: khong xac dinh duoc trang thai thiet bi (adb.exe khong tim thay hoac loi/timeout) -- giu nguyen trang thai driver hien tai.");
                return;
            }

            var connected = connState == AdbConnectionState.Connected;
            if (_lastKnownAdbConnected.HasValue && _lastKnownAdbConnected.Value == connected)
            {
                return; // no real change since last check -- nothing to do
            }
            _lastKnownAdbConnected = connected;

            if (connected)
            {
                var (ok, message) = _driverManager.EnableDevice();
                Log?.Invoke($"ADB device da ket noi lai -- bat lai 'VDD by MTT' (ok={ok}): {message}");
            }
            else
            {
                var (ok, message) = _driverManager.DisableDevice();
                Log?.Invoke($"Khong co ADB device nao ket noi -- tam thoi tat 'VDD by MTT' de tranh nham thanh 2 man hinh (ok={ok}): {message}");
            }
        }
        catch (Exception ex)
        {
            // Never let a poll-tick exception take down the ThreadPool
            // thread silently or crash the host app.
            Log?.Invoke($"ADB poll gap loi khong mong muon: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            System.Threading.Interlocked.Exchange(ref _adbPollInProgress, 0);
        }
    }

    private IFrameSource CreateFrameSource()
    {
        if (_frameSourceFactoryOverride != null)
        {
            return _frameSourceFactoryOverride();
        }

        var native = new NativeCaptureEncoder();
        try
        {
            // Session 14 bug fix: right after DriverManager restarts "VDD by
            // MTT" (ApplyVirtualDisplayResolution -> RecreateFrameSourceForNewResolution
            // -> here), IDXGIOutput1::DuplicateOutput reliably fails ONCE
            // with CaptureError.DuplicateOutputFailed (native error code 6)
            // -- DXGI's well-documented "desktop is still recomposing after
            // a topology/mode change" race, not a permanent failure. The
            // code used to give up on the very first failure and silently
            // fall back to ~4fps JPEG for the rest of the session (user
            // report 2026-07-04: "FPS đang rất rất tệ" -- reproduced live:
            // logs showed exactly this native error code 6 right after a
            // driver restart, meaning the session was running JPEG the
            // whole time, not the ~65fps H.264 pipeline session 13
            // validated in isolation). Retry a few times with a short delay
            // before falling through to the JPEG/stub fallback chain below.
            const int maxAttempts = 4;
            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    native.Init(_activeCodec, _activeFps, _activeBitrateKbps);
                    break;
                }
                catch (InvalidOperationException) when (attempt < maxAttempts)
                {
                    Log?.Invoke($"NativeCaptureEncoder.Init() that bai lan {attempt}/{maxAttempts} (co the la DXGI DuplicateOutput dang recompose sau driver restart) -- thu lai sau 400ms...");
                    Thread.Sleep(400);
                }
            }
            NativeCaptureAvailable = true;
            NativeUsingGpuColorConversion = native.UsingGpuColorConversion;
            var (capW, capH) = native.CapturedSize;
            var codecLabel = native.UsingHevc ? "HEVC" : "H264";
            var gpuLabel = native.UsingGpuColorConversion
                ? (native.UsingZeroCopyD3D11Input ? "GPU(zero-copy)" : "GPU(map 1 lan)")
                : "CPU fallback (cham hon, xem log native fprintf de biet ly do)";
            if (native.UsedFallbackPrimaryDisplay)
            {
                Log?.Invoke(
                    $"CANH BAO: khong tim thay 'VDD by MTT' -- dang mirror man CHINH cua laptop ({capW}x{capH}), " +
                    "DAY KHONG PHAI hanh vi mong muon. Kiem tra Virtual Driver Control da bat/enable display chua " +
                    "(xem RESEARCH-v2-spacedesk-extend-mode-mechanism.md).");
            }
            else
            {
                Log?.Invoke($"NativeCaptureEncoder initialized -- real capture/encode active, target=VDD by MTT, size={capW}x{capH}, codec={codecLabel}, color-conversion={gpuLabel}.");
            }
            return native;
        }
        catch (Exception ex) when (ex is DllNotFoundException or InvalidOperationException or BadImageFormatException)
        {
            // Expected on this dev machine per R13 (C++ toolchain unbuilt --
            // DisplayBridge.Native.dll does not exist yet). Do NOT crash the
            // host app: fall back to GdiScreenCapture (session 4, "chạy cơ
            // bản trước" shortcut) -- pure C# GDI screen capture + JPEG,
            // 3-5fps, no quality/perf target, just enough to prove the PC
            // screen actually shows up on the tablet end-to-end while the
            // real DXGI+MediaFoundation H.264 native pipeline stays blocked
            // on MSVC v143/Windows SDK. Real H.264/NVENC quality work is
            // M2/M5 -- see docs/TASK-v1-tablet-display-tracker.md session 4.
            Log?.Invoke($"Native capture unavailable ({ex.GetType().Name}: {ex.Message}) -- đang chạy chế độ JPEG fallback (chưa build được native H.264 encoder, xem R13).");
            native.Dispose();
            NativeCaptureAvailable = false;

            var gdi = new GdiScreenCapture();
            try
            {
                gdi.Init();
                Log?.Invoke("GdiScreenCapture initialized -- JPEG fallback active (~4fps, quality~55, không phải H.264 thật).");
                return gdi;
            }
            catch (Exception gdiEx)
            {
                // If even the GDI fallback can't init (e.g. headless CI
                // box with no display), degrade further to the old
                // zero-frame stub so control/input/settings wiring is
                // still exercisable without crashing the host app.
                Log?.Invoke($"GdiScreenCapture also unavailable ({gdiEx.GetType().Name}: {gdiEx.Message}) -- chạy ở chế độ stub/test (0 frame).");
                return new StubFrameSource();
            }
        }
    }

    // --- Frame evidence logging (session 4) ---

    private long _framesWritten;
    private DateTime _fpsWindowStartUtc = DateTime.UtcNow;
    private int _fpsWindowCount;

    private void OnFrameWritten(int byteLength, bool isJpeg, ulong ptsUs)
    {
        if (_framesWritten == 0)
        {
            ClientConnected?.Invoke();
        }
        _framesWritten++;
        _fpsWindowCount++;

        // Log the first 3 frames individually (proves real bytes flowed
        // immediately), then throttle to once/sec fps summaries so a long
        // demo run doesn't flood the log file.
        if (_framesWritten <= 3)
        {
            Log?.Invoke($"Frame #{_framesWritten} written: {byteLength} bytes, isJpeg={isJpeg}, ptsUs={ptsUs}");
        }

        var elapsed = DateTime.UtcNow - _fpsWindowStartUtc;
        if (elapsed.TotalSeconds >= 1)
        {
            var fps = _fpsWindowCount / elapsed.TotalSeconds;
            Log?.Invoke($"Video: {_framesWritten} frames total, {fps:F1} fps (last frame {byteLength} bytes, isJpeg={isJpeg})");
            _fpsWindowStartUtc = DateTime.UtcNow;
            _fpsWindowCount = 0;
        }
    }

    // --- CAPS -> CONFIG handshake ---

    private void OnCapsReceived(CapsMessage caps)
    {
        // Replace the placeholder (2560x1600) with the device's REAL
        // reported resolution -- this was the #1 TODO left in
        // DeviceCaps.cs from M4.
        _deviceCaps = new DeviceCaps(caps.Width, caps.Height);
        Log?.Invoke($"CAPS received: {caps.Width}x{caps.Height}@{caps.Dpi}dpi, hz={string.Join(',', caps.SupportedHz)}, codecs={string.Join(',', caps.SupportedCodecs)}, touchPoints={caps.MaxTouchPoints}");

        // Session 12 bug fix: this MUST be set BEFORE ApplyVirtualDisplayResolution
        // (below) runs, because that call may trigger RecreateFrameSourceForNewResolution
        // -> CreateFrameSource(), which reads _activeCodec to initialize the
        // native encoder. Previously ChooseCodec()'s result was computed
        // further down (after ApplyVirtualDisplayResolution) and only ever
        // used for the wire CONFIG message -- the native encoder never saw
        // it and always negotiated H.264 regardless of what was "chosen".
        _activeCodec = ChooseCodec(caps.SupportedCodecs);

        // Session 14 bug fix, same "must be set before ApplyVirtualDisplayResolution"
        // reasoning as _activeCodec above: fps/bitrate must be ready before
        // CreateFrameSource() (possibly triggered by RecreateFrameSourceForNewResolution
        // below) reads them for NativeCaptureEncoder.Init(). Uses the device's
        // real reported resolution (caps.Width/Height), not _deviceCaps.Resolve()
        // (that field isn't updated until the line above this comment block in
        // the original code -- caps.Width/Height is equivalent here, already set).
        _activeFps = (uint)ChooseHz(caps.SupportedHz);
        _activeBitrateKbps = (uint)EstimateBitrateKbps(caps.Width, caps.Height, (byte)_activeFps, _settings.Streaming.QualityPreset);

        // RESEARCH-v2 fix: keep VDD by MTT's vdd_settings.xml in sync with
        // the REAL connected device's native resolution -- no more preset
        // picker, resolution is always 100% native per CAPS (2026-07-03
        // decision). This does not affect the socket/CONFIG handshake below
        // (that already used _deviceCaps.Resolve); it only makes Windows'
        // virtual monitor itself report the right EDID mode so Windows
        // Display Settings shows the correct native resolution too.
        ApplyVirtualDisplayResolution(caps);

        var chosenHz = _activeFps; // set above, before ApplyVirtualDisplayResolution (session 14)
        var chosenCodec = _activeCodec; // set above, before ApplyVirtualDisplayResolution
        var (width, height) = _deviceCaps.Resolve();
        var bitrateKbps = _activeBitrateKbps; // set above, same value passed into NativeCaptureEncoder.Init()

        var config = new ConfigMessage((byte)chosenCodec, (ushort)width, (ushort)height, (byte)chosenHz, bitrateKbps);
        var sent = _controlServer?.Send(config) ?? false;
        Log?.Invoke($"CONFIG sent (ok={sent}): codec={chosenCodec} {width}x{height}@{chosenHz}Hz {bitrateKbps}kbps");
    }

    /// <summary>
    /// Driver self-management task: replaces the old
    /// ApplyResolution+TryRestartDriver pair (called separately, restart
    /// always failing without Admin) with one DriverManager.EnsureReady call
    /// that installs the driver if missing, writes the resolution, and
    /// restarts the device node -- app.manifest now grants Administrator at
    /// launch so the restart actually succeeds instead of just logging a
    /// manual-fallback message. On success, the frame source is recreated
    /// (see RecreateFrameSourceForNewResolution) so capture immediately picks
    /// up the new native resolution instead of waiting for the next Start().
    /// </summary>
    private void ApplyVirtualDisplayResolution(CapsMessage caps)
    {
        var supportedHzInt = caps.SupportedHz.Select(h => (int)h).ToList();
        // Verify RC2 fix: pass onStep so each [1/3]/[2/3]/[3/3] line lands in
        // %TEMP%\displaybridge-host.log the moment it happens, instead of
        // only appearing once EnsureReady returns (it shells out to
        // pnputil/devcon and can take many seconds end-to-end).
        var (success, message) = _driverManager.EnsureReady(
            caps.Width,
            caps.Height,
            supportedHzInt,
            onStep: line => Log?.Invoke($"DriverManager.EnsureReady: {line}"));
        Log?.Invoke($"DriverManager.EnsureReady({caps.Width}x{caps.Height}) hoan tat: {message}");

        if (success)
        {
            SendModeChangeBeforeVideoPipelineRecreate(caps);
            RecreateFrameSourceForNewResolution();
        }
        else
        {
            Log?.Invoke("DriverManager: driver KHONG san sang o resolution moi -- video se tiep tuc dung frame source hien tai (co the sai resolution) cho toi khi khac phuc duoc loi o tren.");
        }
    }

    /// <summary>
    /// Bug fix (session 11): user reported the Android app freezing
    /// completely when the virtual display's resolution changed MID-STREAM
    /// (e.g. 800x600 -> 3000x1920 while already streaming, not at startup),
    /// requiring a full PC Host restart to recover. Root cause confirmed by
    /// reading code: RecreateFrameSourceForNewResolution() below tears down
    /// and rebuilds VideoStreamServer -- closing the live video TCP
    /// connection -- but NEVER told Android beforehand via the control
    /// socket. Android's VideoDecoderActivity.onModeChange() already calls
    /// recoverDecoderAfterError() correctly (proactive flush + decoder
    /// recreation, ready for a fresh IDR at the new resolution), but that
    /// only fires on a MODE_CHANGE message, which was never sent for this
    /// path -- only the one-time CONFIG sent right after this call (in
    /// OnCapsReceived) existed, and that is the initial handshake, not a
    /// live change notification. Blindsided, Android saw the video socket
    /// die and reconnect with NAL units at a completely different
    /// resolution with no proactive flush, risking exhausting
    /// VideoStreamClient's 5-retry limit (R16) and hanging instead of
    /// recovering.
    ///
    /// Fix: send MODE_CHANGE with the new width/height/hz/codec BEFORE
    /// RecreateFrameSourceForNewResolution() stops/replaces VideoStreamServer
    /// -- order matters, Android must be told to prepare BEFORE the video
    /// connection is closed, not after -- then give Android a short window
    /// to run onModeChange()/flush its decoder before the socket actually
    /// goes down, avoiding a race between "Android is flushing" and "PC has
    /// already closed the connection". Reuses the exact same
    /// ChooseHz/ChooseCodec logic OnCapsReceived already uses (no duplicated
    /// selection logic) and the same ModeChangeMessage send pattern already
    /// used for settings-triggered mode changes in PushSettingsChange.
    /// </summary>
    private void SendModeChangeBeforeVideoPipelineRecreate(CapsMessage caps)
    {
        var chosenHz = _activeFps; // set in OnCapsReceived before this call (session 14), same value used to (re)init the native encoder
        var chosenCodec = _activeCodec; // set in OnCapsReceived before this call, same value used to (re)init the native encoder
        var (width, height) = _deviceCaps.Resolve();

        var modeChange = new ModeChangeMessage((ushort)width, (ushort)height, (byte)chosenHz, (byte)chosenCodec, Array.Empty<byte>());
        var ok = _controlServer?.Send(modeChange) ?? false;
        Log?.Invoke($"Da gui MODE_CHANGE cho Android truoc khi tai tao video pipeline o resolution moi: {width}x{height}@{chosenHz}Hz codec={chosenCodec} (ok={ok}).");

        // ApplyVirtualDisplayResolution runs synchronously on the
        // ControlSocketServer per-client read task (Task.Run in
        // ServeClientAsync -> CapsReceived event), not a UI thread, so a
        // short blocking sleep here is safe -- same pattern already used by
        // RecreateFrameSourceForNewResolution's Thread.Sleep(3000) a few
        // lines below for the post-driver-restart PnP settle wait.
        Thread.Sleep(300);
    }

    /// <summary>
    /// Tears down and rebuilds the video pipeline (frame source +
    /// VideoStreamServer, same bound port) after DriverManager.EnsureReady
    /// has restarted "VDD by MTT" with a new native resolution. Necessary
    /// because CreateFrameSource() previously only ran once in Start() --
    /// without this, DesktopDuplicationCapture would keep capturing at
    /// whatever resolution the virtual display had at process startup, even
    /// though vdd_settings.xml/the device node have since moved to the
    /// tablet's real native size.
    /// </summary>
    private void RecreateFrameSourceForNewResolution()
    {
        if (_videoServer is null)
        {
            return;
        }

        Log?.Invoke("Driver da restart -- dang tao lai frame source de bat dung resolution moi...");
        var port = _videoServer.BoundPort;

        _videoServer.FrameWritten -= OnFrameWritten;
        _videoServer.Stop();
        (_frameSource as IDisposable)?.Dispose();

        // devcon restart returns as soon as the disable+enable pair
        // completes, but Windows can take a couple of seconds to fully
        // re-attach the desktop/EDID mode afterwards -- give it a moment
        // before re-enumerating DXGI/GDI outputs, otherwise CreateFrameSource
        // can race the device coming back up and fall back to primary/stub.
        Thread.Sleep(3000);

        var extended = _vddConfigurator.EnsureExtendTopology();
        Log?.Invoke(extended
            ? "VirtualDisplayConfigurator: van o che do Extend sau restart."
            : "VirtualDisplayConfigurator: KHONG the xac nhan lai Extend topology sau restart.");
        _monitorLocator.Invalidate();

        _frameSource = CreateFrameSource();

        _videoServer = new VideoStreamServer(_frameSource, port);
        _videoServer.FrameWritten += OnFrameWritten;
        _videoServer.Start();
        Log?.Invoke($"VideoStreamServer da tao lai, dang lang nghe tren {_videoServer.BoundPort} (native={NativeCaptureAvailable}).");
    }

    private byte ChooseHz(IReadOnlyList<byte> supportedHz)
    {
        var cap = Math.Min(_settings.Display.RefreshRateHz, AppSettings.MaxRefreshRateHz);
        byte best = 60;
        foreach (var hz in supportedHz)
        {
            if (hz <= cap && hz > best) best = hz;
        }
        return best;
    }

    // CodecId per schema.yaml Config.codec doc: 0=H264, 1=HEVC.
    private int ChooseCodec(IReadOnlyList<byte> supportedCodecs)
    {
        return _settings.Streaming.Codec switch
        {
            Codec.ForceH264 => 0,
            Codec.ForceHevc => 1,
            _ => supportedCodecs.Contains((byte)1) ? 1 : 0, // Auto: prefer HEVC
        };
    }

    private static int EstimateBitrateKbps(int width, int height, byte hz, QualityPreset preset)
    {
        var bpp = preset switch
        {
            QualityPreset.Low => 0.04,
            QualityPreset.Balanced => 0.07,
            QualityPreset.High => 0.10,
            QualityPreset.Ultra => 0.14,
            _ => 0.10,
        };
        var bitsPerSecond = (double)width * height * hz * bpp;
        return Math.Max(1000, (int)(bitsPerSecond / 1000.0));
    }

    // --- Input wiring ---

    private void OnTouchEventReceived(TouchEventMessage evt)
    {
        Log?.Invoke($"TOUCH_EVENT pointer={evt.PointerId} action={(TouchAction)evt.Action} x={evt.X} y={evt.Y}");
        _inputDispatcher?.Dispatch(evt);
    }

    private void OnTouchBatchReceived(TouchBatchMessage batch)
    {
        Log?.Invoke($"TOUCH_BATCH count={batch.Events.Count}");
        _inputDispatcher?.DispatchBatch(batch.Events);
    }

    // --- Settings wiring ---

    private void OnSettingRequestReceived(SettingRequestMessage req)
    {
        if (!SettingKeyMap.TryFromWireKey(req.Key, out var field))
        {
            Log?.Invoke($"SETTING_REQUEST with unknown key={req.Key} ignored.");
            return;
        }

        if (!ApplyToSettings(_settings, field, req.Value))
        {
            Log?.Invoke($"SETTING_REQUEST for {field} not supported via live request, ignored.");
            return;
        }

        try
        {
            _settingsStore.Save(_settings, _deviceCaps);
        }
        catch (SettingsValidationException ex)
        {
            Log?.Invoke($"SETTING_REQUEST for {field}={req.Value} rejected: {ex.Message}");
            return;
        }

        var applyType = SettingsChangeClassifier.Classify(field);
        PushSettingsChange(field, applyType);
    }

    /// <summary>
    /// Public hook so the WPF Settings UI (M4.1) can push a change made
    /// locally (not via SETTING_REQUEST) out to the connected tablet, using
    /// the same Live/ReMode routing as the tablet-initiated path above.
    /// </summary>
    public void NotifySettingsChanged(SettingField field)
    {
        var applyType = SettingsChangeClassifier.Classify(field);
        PushSettingsChange(field, applyType);
    }

    private void PushSettingsChange(SettingField field, ApplyType applyType)
    {
        if (applyType == ApplyType.Live)
        {
            var value = ReadAsWireValue(_settings, field);
            var update = new ConfigUpdateMessage(new[] { new ConfigEntry(SettingKeyMap.ToWireKey(field), value) });
            var ok = _controlServer?.Send(update) ?? false;
            Log?.Invoke($"CONFIG_UPDATE sent (ok={ok}) for {field}={value}");
        }
        else if (applyType == ApplyType.ReMode)
        {
            var chosenCodec = _settings.Streaming.Codec switch
            {
                Codec.ForceH264 => 0,
                Codec.ForceHevc => 1,
                _ => 1,
            };
            var (width, height) = _deviceCaps.Resolve();
            var modeChange = new ModeChangeMessage((ushort)width, (ushort)height, (byte)_settings.Display.RefreshRateHz, (byte)chosenCodec, Array.Empty<byte>());
            var ok = _controlServer?.Send(modeChange) ?? false;
            Log?.Invoke($"MODE_CHANGE sent (ok={ok}) for {field} -> {width}x{height}@{_settings.Display.RefreshRateHz}Hz (csd empty: R17 in-band SPS/PPS, see H264Encoder.cpp)");

            // Session 14 bug fix (user report 2026-07-04: "H265 vẫn chưa
            // chạy"): this branch computed chosenCodec and told Android to
            // expect it via MODE_CHANGE, but NEVER updated _activeCodec or
            // re-ran CreateFrameSource() -- the exact same "computed but
            // discarded" class of bug already fixed for the CAPS-handshake
            // path (OnCapsReceived, session 12/13). Android would flush its
            // decoder and switch MIME type while the PC kept encoding
            // whatever codec was already active, a real mismatch. Only do
            // this for StreamingCodec specifically -- other ReMode fields
            // (DisplayRefreshRateHz) don't need a native re-init, they only
            // need Android to know the new mode via the MODE_CHANGE already
            // sent above (resolution/Hz come from DeviceCaps, not the
            // native encoder's own state).
            if (field == SettingField.StreamingCodec && chosenCodec != _activeCodec)
            {
                _activeCodec = chosenCodec;
                Thread.Sleep(300); // let Android's onModeChange()/decoder flush land before the video socket dies below
                RecreateFrameSourceForNewResolution();
            }
        }
        // WindowsApi/Reconnect/Local: no protocol message by design (catalog §3.5).
    }

    private static bool ApplyToSettings(AppSettings settings, SettingField field, ulong value)
    {
        switch (field)
        {
            case SettingField.DisplayRefreshRateHz: settings.Display.RefreshRateHz = (int)value; return true;
            case SettingField.StreamingCodec: settings.Streaming.Codec = (Codec)value; return true;
            case SettingField.StreamingQualityPreset: settings.Streaming.QualityPreset = (QualityPreset)value; return true;
            case SettingField.StreamingFpsCap: settings.Streaming.FpsCap = (int)value; return true;
            case SettingField.StreamingAdaptiveBitrate: settings.Streaming.AdaptiveBitrate = value != 0; return true;
            case SettingField.StreamingLatencyPriority: settings.Streaming.LatencyPriority = (LatencyPriority)value; return true;
            case SettingField.InputTouchEnabled: settings.Input.TouchEnabled = value != 0; return true;
            case SettingField.InputMode: settings.Input.InputMode = (InputMode)value; return true;
            case SettingField.InputPenPressureEnabled: settings.Input.PenPressureEnabled = value != 0; return true;
            case SettingField.InputPenPressureGamma: settings.Input.PenPressureGamma = value / 100.0; return true;
            case SettingField.ConnectionTransport: settings.Connection.Transport = (Transport)value; return true;
            case SettingField.ConnectionAutoConnect: settings.Connection.AutoConnect = value != 0; return true;
            case SettingField.DiagnosticsStatsOverlay: settings.Diagnostics.StatsOverlay = (StatsOverlayMode)value; return true;
            case SettingField.DiagnosticsLatencyHud: settings.Diagnostics.LatencyHud = value != 0; return true;
            case SettingField.DiagnosticsLogLevel: settings.Diagnostics.LogLevel = (LogLevel)value; return true;
            default: return false; // e.g. ConnectionPorts: requires reconnect flow, not a live tablet-triggerable field.
        }
    }

    private static ulong ReadAsWireValue(AppSettings settings, SettingField field) => field switch
    {
        SettingField.DisplayRefreshRateHz => (ulong)settings.Display.RefreshRateHz,
        SettingField.StreamingCodec => (ulong)settings.Streaming.Codec,
        SettingField.StreamingQualityPreset => (ulong)settings.Streaming.QualityPreset,
        SettingField.StreamingFpsCap => (ulong)settings.Streaming.FpsCap,
        SettingField.StreamingAdaptiveBitrate => settings.Streaming.AdaptiveBitrate ? 1u : 0u,
        SettingField.StreamingLatencyPriority => (ulong)settings.Streaming.LatencyPriority,
        SettingField.InputTouchEnabled => settings.Input.TouchEnabled ? 1u : 0u,
        SettingField.InputMode => (ulong)settings.Input.InputMode,
        SettingField.InputPenPressureEnabled => settings.Input.PenPressureEnabled ? 1u : 0u,
        SettingField.InputPenPressureGamma => (ulong)Math.Round(settings.Input.PenPressureGamma * 100),
        SettingField.ConnectionTransport => (ulong)settings.Connection.Transport,
        SettingField.ConnectionAutoConnect => settings.Connection.AutoConnect ? 1u : 0u,
        SettingField.DiagnosticsStatsOverlay => (ulong)settings.Diagnostics.StatsOverlay,
        SettingField.DiagnosticsLatencyHud => settings.Diagnostics.LatencyHud ? 1u : 0u,
        SettingField.DiagnosticsLogLevel => (ulong)settings.Diagnostics.LogLevel,
        _ => 0,
    };

    public void Stop()
    {
        _adbPollTimer?.Dispose();
        _adbPollTimer = null;
        _controlServer?.Stop();
        _videoServer?.Stop();
        (_frameSource as IDisposable)?.Dispose();
    }

    public void Dispose()
    {
        if (_disposed) return;
        Stop();
        _controlServer?.Dispose();
        _videoServer?.Dispose();
        _disposed = true;
    }

    /// <summary>
    /// Stub IFrameSource used when the native DLL can't be loaded (C++
    /// unbuilt, see R13). Keeps VideoStreamServer's accept loop alive with
    /// zero real frames so the video port doesn't refuse connections
    /// outright -- lets a tablet/fake-device still complete the CAPS/
    /// CONFIG handshake and exercise control/input/settings end-to-end.
    /// </summary>
    private sealed class StubFrameSource : IFrameSource
    {
        public bool Init() => true;
        public EncodedFrame? GetNextFrame() => null;
        public void Shutdown() { }
    }
}
