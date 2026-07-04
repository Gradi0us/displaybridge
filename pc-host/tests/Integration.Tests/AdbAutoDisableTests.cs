// AdbAutoDisableTests.cs — no-ADB auto-disable task (session 15).
//
// User request: "nếu không có adb được kết nối thì sẽ tạm thời ngưng hoạt
// động của driver đi để k bị nhầm thành 2 màn". Exercises
// StreamingCoordinator.OnAdbPollTick (the Timer callback body, called
// directly here via [InternalsVisibleTo] instead of waiting real wall-clock
// AdbPollFirstDelay/AdbPollInterval seconds) against a fake ADB checker and
// a fake driver manager that records Enable/Disable calls, to prove:
//   1. Disconnected -> DisableDevice() is called.
//   2. Reconnected -> EnableDevice() is called.
//   3. Repeated ticks with NO state change do NOT re-call devcon (debounce).
//   4. Indeterminate (adb missing/error) never triggers Enable/Disable.
using DisplayBridge.Core.Settings;
using DisplayBridge.Core.Video;
using DisplayBridge.Host;
using Xunit;

namespace DisplayBridge.Integration.Tests;

/// <summary>Fake IAdbDeviceChecker whose reported state a test can flip on demand.</summary>
internal sealed class FakeAdbDeviceChecker : IAdbDeviceChecker
{
    public AdbConnectionState NextState = AdbConnectionState.Connected;
    public int CallCount;

    public AdbConnectionState CheckConnectionState()
    {
        CallCount++;
        return NextState;
    }
}

/// <summary>Fake IDriverManager that records Enable/Disable calls instead of shelling out to real devcon.exe.</summary>
internal sealed class RecordingDriverManager : IDriverManager
{
    public int EnableCallCount;
    public int DisableCallCount;

    public (bool Success, string Message) EnsureReady(int nativeWidth, int nativeHeight, IReadOnlyList<int> supportedHz, Action<string>? onStep = null)
    {
        onStep?.Invoke("[fake] EnsureReady -> success");
        return (true, "fake EnsureReady success");
    }

    public (bool Success, string Message) EnableDevice()
    {
        EnableCallCount++;
        return (true, "fake enable");
    }

    public (bool Success, string Message) DisableDevice()
    {
        DisableCallCount++;
        return (true, "fake disable");
    }
}

public sealed class AdbAutoDisableTests : IDisposable
{
    private readonly string _tempSettingsPath;
    private readonly RecordingDriverManager _driverManager = new();
    private readonly FakeAdbDeviceChecker _adbChecker = new();
    private readonly StreamingCoordinator _coordinator;

    public AdbAutoDisableTests()
    {
        _tempSettingsPath = Path.Combine(Path.GetTempPath(), $"displaybridge-adb-test-{Guid.NewGuid():N}.json");
        var store = new SettingsStore(_tempSettingsPath);
        _coordinator = new StreamingCoordinator(
            store,
            driverManager: _driverManager,
            frameSourceFactory: () => new NoOpFrameSource(),
            adbChecker: _adbChecker);
    }

    public void Dispose()
    {
        _coordinator.Dispose();
        if (File.Exists(_tempSettingsPath)) File.Delete(_tempSettingsPath);
    }

    [Fact]
    public void NoDeviceConnected_DisablesDriverExactlyOnce()
    {
        _adbChecker.NextState = AdbConnectionState.Disconnected;

        _coordinator.OnAdbPollTick(null);

        Assert.Equal(1, _driverManager.DisableCallCount);
        Assert.Equal(0, _driverManager.EnableCallCount);
    }

    [Fact]
    public void DeviceConnected_EnablesDriverExactlyOnce()
    {
        _adbChecker.NextState = AdbConnectionState.Connected;

        _coordinator.OnAdbPollTick(null);

        Assert.Equal(1, _driverManager.EnableCallCount);
        Assert.Equal(0, _driverManager.DisableCallCount);
    }

    [Fact]
    public void RepeatedTicksWithNoStateChange_DoesNotReCallDevconEachTime()
    {
        // Regression guard for the debounce requirement in the task brief:
        // "Tránh vòng lặp bật/tắt liên tục... chỉ đổi trạng thái nếu tình
        // trạng kết nối thực sự thay đổi so với lần kiểm tra trước".
        _adbChecker.NextState = AdbConnectionState.Connected;

        _coordinator.OnAdbPollTick(null);
        _coordinator.OnAdbPollTick(null);
        _coordinator.OnAdbPollTick(null);

        Assert.Equal(3, _adbChecker.CallCount); // adb WAS actually re-checked each tick...
        Assert.Equal(1, _driverManager.EnableCallCount); // ...but devcon was only invoked on the first (real) transition.
    }

    [Fact]
    public void DisconnectThenReconnect_DisablesThenEnables()
    {
        _adbChecker.NextState = AdbConnectionState.Disconnected;
        _coordinator.OnAdbPollTick(null);
        Assert.Equal(1, _driverManager.DisableCallCount);

        _adbChecker.NextState = AdbConnectionState.Connected;
        _coordinator.OnAdbPollTick(null);
        Assert.Equal(1, _driverManager.EnableCallCount);

        // Still disconnected->connected->disconnected again should disable a second time.
        _adbChecker.NextState = AdbConnectionState.Disconnected;
        _coordinator.OnAdbPollTick(null);
        Assert.Equal(2, _driverManager.DisableCallCount);
    }

    [Fact]
    public void IndeterminateState_NeverCallsEnableOrDisable()
    {
        // Safety constraint from the task brief: never guess-disable the
        // driver just because adb.exe itself couldn't be reached this tick.
        _adbChecker.NextState = AdbConnectionState.Indeterminate;

        _coordinator.OnAdbPollTick(null);
        _coordinator.OnAdbPollTick(null);

        Assert.Equal(0, _driverManager.EnableCallCount);
        Assert.Equal(0, _driverManager.DisableCallCount);
    }

    [Fact]
    public void IndeterminateBetweenRealStates_DoesNotResetDebounceBaseline()
    {
        // Connected -> Indeterminate (transient adb hiccup) -> Connected
        // again should NOT re-invoke EnableDevice the second time, since the
        // last KNOWN real state never actually changed.
        _adbChecker.NextState = AdbConnectionState.Connected;
        _coordinator.OnAdbPollTick(null);
        Assert.Equal(1, _driverManager.EnableCallCount);

        _adbChecker.NextState = AdbConnectionState.Indeterminate;
        _coordinator.OnAdbPollTick(null);

        _adbChecker.NextState = AdbConnectionState.Connected;
        _coordinator.OnAdbPollTick(null);

        Assert.Equal(1, _driverManager.EnableCallCount); // still just the one real transition
    }

    /// <summary>Minimal IFrameSource stand-in -- this suite never touches video/capture, only the ADB poll logic.</summary>
    private sealed class NoOpFrameSource : DisplayBridge.Core.Video.IFrameSource
    {
        public bool Init() => true;
        public DisplayBridge.Core.Video.EncodedFrame? GetNextFrame() => null;
        public void Shutdown() { }
    }
}
