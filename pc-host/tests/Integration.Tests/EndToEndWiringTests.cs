// EndToEndWiringTests.cs — "Việc 5" from the wiring-E2E task brief.
//
// Proves StreamingCoordinator (M1 video server + new ControlSocketServer +
// M3 InputDispatcher + M4 SettingsStore/SettingsChangeClassifier) actually
// runs as ONE connected pipeline, using tools/fake-device instead of a real
// tablet and WITHOUT DisplayBridge.Native.dll (native C++ build still
// blocked by R13 -- StreamingCoordinator falls back to its internal
// StubFrameSource automatically, see its CreateFrameSource()).
//
// This is the first real evidence in the whole project that a frame of
// *something* crosses the wire between two independently-started TCP
// servers and a simulated client -- session 2 shipped 67/67 + 36/36 unit
// tests but zero integration proof (see TASK tracker "Fable 5 overview").
using System.Linq;
using DisplayBridge.Core.Input;
using DisplayBridge.Core.Protocol.Generated;
using DisplayBridge.Core.Settings;
using DisplayBridge.Core.Video;
using DisplayBridge.FakeDevice;
using DisplayBridge.Host;
using Xunit;

namespace DisplayBridge.Integration.Tests;

/// <summary>Records calls instead of touching real Win32 APIs -- same fake pattern as InputModeClassifierTests.FakeInjectorTests in Core.Tests.</summary>
internal sealed class RecordingCursorInjector : ICursorInjector
{
    public readonly List<string> Calls = new();
    public void MoveTo(ushort x, ushort y) => Calls.Add($"MoveTo({x},{y})");
    public void ButtonDown() => Calls.Add("ButtonDown");
    public void ButtonUp() => Calls.Add("ButtonUp");
}

internal sealed class RecordingTouchInjector : ITouchInjector
{
    public int InitializeCalls;
    public readonly List<IReadOnlyList<TouchEventMessage>> InjectedBatches = new();
    public void InitializeIfNeeded(uint maxPointCount = 10) => InitializeCalls++;
    public void Inject(IReadOnlyList<TouchEventMessage> contacts) => InjectedBatches.Add(contacts);
}

/// <summary>
/// Session 11: always reports success without shelling out to real
/// devcon.exe/pnputil.exe (which aren't bundled next to the test binary --
/// see DriverManager.EnsureReady's real header comment / session 8 notes on
/// Integration.Tests returning fast-false in CI). Lets the mid-stream
/// resolution-change bug fix (StreamingCoordinator.
/// SendModeChangeBeforeVideoPipelineRecreate) be exercised end-to-end
/// through the real socket wiring without needing a real "VDD by MTT"
/// driver installed on the test machine.
/// </summary>
internal sealed class FakeDriverManager : IDriverManager
{
    public int CallCount;
    public (bool Success, string Message) EnsureReady(int nativeWidth, int nativeHeight, IReadOnlyList<int> supportedHz, Action<string>? onStep = null)
    {
        CallCount++;
        onStep?.Invoke($"[fake] EnsureReady({nativeWidth}x{nativeHeight}) call #{CallCount} -> success");
        return (true, $"fake EnsureReady success for {nativeWidth}x{nativeHeight}");
    }
}

/// <summary>
/// Zero-frame stand-in for NativeCaptureEncoder/GdiScreenCapture, injected
/// via StreamingCoordinator's frameSourceFactory test seam (session 11).
/// Combined with FakeDriverManager this lets
/// RecreateFrameSourceForNewResolution() run its FULL real path (stop old
/// VideoStreamServer, sleep, EnsureExtendTopology, new VideoStreamServer)
/// WITHOUT ever touching real DXGI/D3D11 capture -- doing that for real
/// twice within one test process was empirically found to leave shared
/// native global state broken enough to crash an unrelated LATER test with
/// an access violation (see StreamingCoordinator's _frameSourceFactoryOverride
/// header comment).
/// </summary>
internal sealed class StubFrameSourceForTest : IFrameSource
{
    public bool Init() => true;
    public EncodedFrame? GetNextFrame() => null;
    public void Shutdown() { }
}

public sealed class EndToEndWiringTests : IDisposable
{
    private readonly string _tempSettingsPath;
    private readonly RecordingCursorInjector _cursor = new();
    private readonly RecordingTouchInjector _touch = new();
    private readonly StreamingCoordinator _coordinator;

    public EndToEndWiringTests()
    {
        _tempSettingsPath = Path.Combine(Path.GetTempPath(), $"displaybridge-test-{Guid.NewGuid():N}.json");
        var store = new SettingsStore(_tempSettingsPath);
        _coordinator = new StreamingCoordinator(store, _cursor, _touch);
    }

    public void Dispose()
    {
        _coordinator.Dispose();
        try { File.Delete(_tempSettingsPath); } catch { /* best-effort */ }
    }

    private async Task<FakeTabletDevice> StartAndConnectAsync()
    {
        // Port 0 -> OS-assigned ephemeral ports, so parallel test runs and
        // a real running Host app on 29500/29501 never collide.
        _coordinator.Start(videoPort: 0, controlPort: 0);
        var device = new FakeTabletDevice("127.0.0.1", _coordinator.VideoBoundPort, _coordinator.ControlBoundPort);
        await device.ConnectAsync();
        return device;
    }

    [Fact]
    public async Task VideoAndControlSockets_BothAcceptConnections()
    {
        using var device = await StartAndConnectAsync();
        Assert.True(device.Observations.VideoConnected);
        Assert.True(_coordinator.VideoBoundPort > 0);
        Assert.True(_coordinator.ControlBoundPort > 0);
    }

    [Fact]
    public async Task FrameSourceSelection_NeverCrashesStart_RegardlessOfNativeAvailability()
    {
        // Session 6: MSVC v143/Windows SDK are now installed, so on THIS
        // machine DisplayBridge.Native.dll builds and NativeCaptureAvailable
        // is genuinely true (see docs/TASK-v1-tablet-display-tracker.md
        // session 6) -- the old hard assumption "native is unbuilt, must be
        // false" (R13) no longer holds universally. The property the test
        // actually needs to guarantee is CreateFrameSource()'s 3-tier
        // fallback chain (native -> GDI JPEG -> stub, see
        // StreamingCoordinator.CreateFrameSource) never throws and the
        // video socket always comes up, on any machine regardless of
        // native/GPU availability.
        using var device = await StartAndConnectAsync();
        Assert.True(device.Observations.VideoConnected);
        Assert.True(_coordinator.VideoBoundPort > 0);
    }

    [Fact]
    public async Task CapsHandshake_UpdatesRealDeviceCaps_AndRepliesWithConfig()
    {
        using var device = await StartAndConnectAsync();

        var config = await device.WaitForConfigAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(config);

        // Fake device reports the real HONOR ROD2-W09 profile (3000x1920) --
        // DeviceCaps must move off the 2560x1600 placeholder to reflect it.
        Assert.Equal(3000, _coordinator.CurrentDeviceCaps.NativeWidth);
        Assert.Equal(1920, _coordinator.CurrentDeviceCaps.NativeHeight);

        // Resolution is always 100% native (2026-07-03 decision) -> Config should carry the native size.
        Assert.Equal((ushort)3000, config!.Width);
        Assert.Equal((ushort)1920, config.Height);
        // Fake device advertises hz [60,90,120]; default RefreshRateHz=120 -> min(120,120)=120.
        Assert.Equal((byte)120, config.Hz);
    }

    [Fact]
    public async Task TouchEvent_SingleFinger_RoutesToCursorInjector()
    {
        using var device = await StartAndConnectAsync();
        await device.WaitForConfigAsync(TimeSpan.FromSeconds(5));

        device.SendTouchEvent(pointerId: 0, action: (byte)TouchAction.Down, x: 32768, y: 32768);

        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (_cursor.Calls.Count == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        Assert.Contains(_cursor.Calls, c => c.StartsWith("MoveTo"));
        Assert.Contains("ButtonDown", _cursor.Calls);
        Assert.Empty(_touch.InjectedBatches);
    }

    [Fact]
    public async Task TouchEvent_TwoFingers_RoutesToTouchInjector()
    {
        using var device = await StartAndConnectAsync();
        await device.WaitForConfigAsync(TimeSpan.FromSeconds(5));

        device.SendTouchEvent(pointerId: 0, action: (byte)TouchAction.Down, x: 20000, y: 20000);
        device.SendTouchEvent(pointerId: 1, action: (byte)TouchAction.Down, x: 40000, y: 40000);

        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (_touch.InjectedBatches.Count == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        Assert.NotEmpty(_touch.InjectedBatches);
    }

    [Fact]
    public async Task SettingRequest_LiveField_SendsConfigUpdate_NoFrameError()
    {
        using var device = await StartAndConnectAsync();
        await device.WaitForConfigAsync(TimeSpan.FromSeconds(5));

        // StreamingFpsCap is classified Live per SettingsChangeClassifier.
        device.SendSettingRequest(SettingKeyMap.ToWireKey(SettingField.StreamingFpsCap), 80);
        var got = await device.WaitForConfigUpdateAsync(TimeSpan.FromSeconds(5));
        Assert.True(got);
        Assert.Equal(80, _coordinator.CurrentSettings.Streaming.FpsCap);

        // M4.2 DoD-style check: drive it through a couple more values, no exceptions/frame errors.
        device.SendSettingRequest(SettingKeyMap.ToWireKey(SettingField.StreamingFpsCap), 20);
        device.SendSettingRequest(SettingKeyMap.ToWireKey(SettingField.StreamingFpsCap), 80);
        var stillGettingUpdates = await device.WaitForConfigUpdateAsync(TimeSpan.FromSeconds(5));
        Assert.True(stillGettingUpdates);
    }

    [Fact]
    public async Task SettingRequest_ReModeField_SendsModeChange()
    {
        using var device = await StartAndConnectAsync();
        await device.WaitForConfigAsync(TimeSpan.FromSeconds(5));

        // DisplayRefreshRateHz is classified ReMode per SettingsChangeClassifier.
        device.SendSettingRequest(SettingKeyMap.ToWireKey(SettingField.DisplayRefreshRateHz), 90);

        var got = await device.WaitForModeChangeAsync(TimeSpan.FromSeconds(5));
        Assert.True(got);
        Assert.Equal(90, _coordinator.CurrentSettings.Display.RefreshRateHz);
    }
}

/// <summary>
/// Session 11 -- regression coverage for the "chạm tablet -> resolution
/// change mid-stream freezes the Android app" bug (see
/// TASK-v1-tablet-display-tracker.md session 11, StreamingCoordinator.
/// SendModeChangeBeforeVideoPipelineRecreate). Root cause: a second CAPS
/// message (resolution changed while already streaming) triggered
/// RecreateFrameSourceForNewResolution(), which tears down and rebuilds
/// VideoStreamServer -- closing the live video TCP connection -- WITHOUT
/// ever telling Android via MODE_CHANGE first. Uses its own
/// StreamingCoordinator+FakeDriverManager (rather than the shared one in
/// EndToEndWiringTests) because it needs EnsureReady to actually SUCCEED so
/// RecreateFrameSourceForNewResolution really runs -- the real DriverManager
/// fails fast in CI (no devcon.exe/pnputil resources bundled next to the
/// test binary, see DriverManager header comment / session 8 notes), which
/// is exactly why the other tests in this file never exercise this path.
/// </summary>
public sealed class ResolutionChangeMidStreamTests : IDisposable
{
    private readonly string _tempSettingsPath;
    private readonly FakeDriverManager _fakeDriverManager = new();
    private readonly StreamingCoordinator _coordinator;

    public ResolutionChangeMidStreamTests()
    {
        _tempSettingsPath = Path.Combine(Path.GetTempPath(), $"displaybridge-test-{Guid.NewGuid():N}.json");
        var store = new SettingsStore(_tempSettingsPath);
        _coordinator = new StreamingCoordinator(store, driverManager: _fakeDriverManager, frameSourceFactory: () => new StubFrameSourceForTest());
    }

    public void Dispose()
    {
        _coordinator.Dispose();
        try { File.Delete(_tempSettingsPath); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task SecondCapsWithDifferentResolution_SendsModeChange_BeforeClosingVideoSocket()
    {
        // ControlSocketServer's per-client read loop handles CapsReceived
        // synchronously (see ControlSocketServer.ServeClientAsync), so
        // ApplyVirtualDisplayResolution (send MODE_CHANGE, then
        // RecreateFrameSourceForNewResolution's Thread.Sleep(3000) + real
        // native re-init on this machine) fully blocks that client's read
        // loop -- a second CAPS sent immediately is simply queued in the OS
        // socket buffer and only processed once the first CAPS's whole
        // cycle finishes. Track recreate-completion via the Log event
        // rather than a fixed sleep, since real native re-init time varies.
        var logLines = new List<string>();
        _coordinator.Log += line => { lock (logLines) logLines.Add(line); };
        bool RecreateCompletedAtLeast(int times)
        {
            lock (logLines) return logLines.Count(l => l.Contains("VideoStreamServer da tao lai")) >= times;
        }

        _coordinator.Start(videoPort: 0, controlPort: 0);
        using var device = new FakeTabletDevice("127.0.0.1", _coordinator.VideoBoundPort, _coordinator.ControlBoundPort);
        await device.ConnectAsync();

        // Initial handshake (CAPS sent automatically by ConnectAsync, 3000x1920)
        // -- with FakeDriverManager succeeding, this ALSO triggers a
        // RecreateFrameSourceForNewResolution/MODE_CHANGE round once already
        // (pre-existing session-8 behavior, unrelated to this bug fix). Wait
        // for that FIRST cycle to fully settle (video server rebuilt and
        // listening again) before reattaching the video socket and
        // triggering the scenario under test, so the ordering assertions
        // below are unambiguously about the SECOND CAPS's own MODE_CHANGE
        // and its own video-socket close, not the first cycle's.
        await device.WaitForConfigAsync(TimeSpan.FromSeconds(15));
        await device.WaitForModeChangeCountAsync(1, TimeSpan.FromSeconds(15));
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!RecreateCompletedAtLeast(1) && DateTime.UtcNow < deadline) await Task.Delay(50);
        Assert.True(RecreateCompletedAtLeast(1), "Expected the first CAPS's RecreateFrameSourceForNewResolution to complete (video server rebuilt).");

        // Reconnect the video socket to the recreated VideoStreamServer --
        // without this, there is no live client for the SECOND CAPS's
        // recreate to actually disconnect, and any "disconnect" observation
        // would stay stuck at the FIRST cycle's stale timestamp.
        await device.ReattachVideoSocketAsync(_coordinator.VideoBoundPort);

        // The bug scenario: resolution changes to something completely
        // different WHILE already streaming (not at startup) -- e.g.
        // 800x600 -> 3000x1920 per the user's real report.
        device.SendCaps(1920, 1080, new byte[] { 60, 90, 120 }, new byte[] { 0, 1 });

        var gotModeChange = await device.WaitForModeChangeCountAsync(2, TimeSpan.FromSeconds(15));
        Assert.True(gotModeChange, "Expected a second MODE_CHANGE for the second CAPS's new resolution.");

        var gotVideoDisconnect = await device.WaitForVideoDisconnectAsync(TimeSpan.FromSeconds(15));
        Assert.True(gotVideoDisconnect, "Expected the second CAPS's RecreateFrameSourceForNewResolution to close the reattached video socket.");

        // Field correctness: the LAST ModeChange observed must carry the
        // SECOND CAPS's resolution (1920x1080), not the first (3000x1920).
        var lastModeChange = device.Observations.ModeChanges[^1];
        Assert.Equal((ushort)1920, lastModeChange.Width);
        Assert.Equal((ushort)1080, lastModeChange.Height);

        // Ordering: the moment MODE_CHANGE for the new resolution was
        // observed on the control socket must be BEFORE the (reattached)
        // video socket was closed -- this is the actual bug fix (order
        // matters: Android must be told to prepare BEFORE the video
        // connection dies, not after). This is the core assertion this
        // test exists to make.
        var lastModeChangeAtUtc = device.Observations.ModeChangeAtUtc[^1];
        var videoDisconnectedAtUtc = device.Observations.VideoDisconnectedAtUtc!.Value;
        Assert.True(
            lastModeChangeAtUtc <= videoDisconnectedAtUtc,
            $"MODE_CHANGE arrived at {lastModeChangeAtUtc:O} but video socket was already closed at {videoDisconnectedAtUtc:O} -- Android would be blindsided, exactly the bug being fixed.");
    }
}
