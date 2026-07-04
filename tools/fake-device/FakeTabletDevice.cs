// FakeTabletDevice.cs — tools/fake-device (session 3).
//
// CONTEXT-BRIEF-v2 §1 listed tools/fake-device/ as "empty, ready for M4
// integration tests" since session 1's M0 scaffold. This is that piece:
// a minimal TCP client that plays the tablet's role against the PC host's
// real VideoStreamServer (29500) + ControlSocketServer (29501), so the
// wiring built in StreamingCoordinator.cs can be verified end-to-end
// WITHOUT a real tablet and WITHOUT the native DLL (C++ toolchain still
// unbuilt, see R13) -- exactly the two blockers called out in the task
// brief for "Việc 5".
using System.Net.Sockets;
using DisplayBridge.Core.Protocol.Generated;

namespace DisplayBridge.FakeDevice;

/// <summary>Everything the fake device observed from one control-socket session, for test assertions.</summary>
public sealed class FakeDeviceObservations
{
    public bool VideoConnected { get; set; }
    public ConfigMessage? ConfigReceived { get; set; }
    public readonly List<ConfigUpdateMessage> ConfigUpdates = new();
    public readonly List<ModeChangeMessage> ModeChanges = new();

    // Session 11: paired UTC timestamps (recorded on the read-loop thread
    // the instant each message/event is observed) so tests can assert
    // ORDERING -- specifically that MODE_CHANGE arrives on the control
    // socket strictly BEFORE the PC closes/reopens the video TCP
    // connection (StreamingCoordinator.SendModeChangeBeforeVideoPipelineRecreate
    // bug fix), not just that both eventually happen.
    public readonly List<DateTime> ModeChangeAtUtc = new();

    /// <summary>Set once the video socket's read loop observes EOF/exception (PC-side VideoStreamServer.Stop() closing the accepted client), null until then.</summary>
    public DateTime? VideoDisconnectedAtUtc { get; set; }
}

/// <summary>
/// Simulates a connected HONOR ROD2-W09-like tablet: connects to both
/// sockets, performs the CAPS->CONFIG handshake with realistic values (see
/// TASK-v1-tablet-display-tracker.md "Hardware profile đã chốt"), and
/// exposes methods to send TOUCH_EVENT/SETTING_REQUEST and observe what
/// the PC host sends back.
/// </summary>
public sealed class FakeTabletDevice : IDisposable
{
    private readonly string _host;
    private readonly int _videoPort;
    private readonly int _controlPort;

    private TcpClient? _videoClient;
    private TcpClient? _controlClient;
    private BinaryWriter? _controlWriter;
    private BinaryReader? _controlReader;
    private Task? _readLoopTask;
    private Task? _videoReadLoopTask;
    private CancellationTokenSource? _cts;

    public FakeDeviceObservations Observations { get; } = new();

    public FakeTabletDevice(string host, int videoPort, int controlPort)
    {
        _host = host;
        _videoPort = videoPort;
        _controlPort = controlPort;
    }

    /// <summary>Connects both sockets and sends CAPS. Does not block waiting for CONFIG -- use WaitForConfigAsync.</summary>
    public async Task ConnectAsync()
    {
        _videoClient = new TcpClient();
        await _videoClient.ConnectAsync(_host, _videoPort).ConfigureAwait(false);
        Observations.VideoConnected = _videoClient.Connected;

        _controlClient = new TcpClient();
        await _controlClient.ConnectAsync(_host, _controlPort).ConfigureAwait(false);
        var stream = _controlClient.GetStream();
        _controlWriter = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        _controlReader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);

        _cts = new CancellationTokenSource();
        _readLoopTask = Task.Run(() => ReadLoopAsync(_cts.Token));
        _videoReadLoopTask = Task.Run(() => VideoReadLoopAsync(_videoClient.GetStream(), _cts.Token));

        SendCaps(3000, 1920, new byte[] { 60, 90, 120 }, new byte[] { 0, 1 });
    }

    /// <summary>
    /// Sends a CAPS message. Default call from ConnectAsync() uses the real
    /// HONOR ROD2-W09 profile per TASK tracker "Hardware profile đã chốt".
    /// Public overload (session 11) lets tests simulate a resolution change
    /// arriving MID-STREAM (a second CAPS with different Width/Height after
    /// the first handshake already completed) -- the exact scenario that
    /// used to freeze the Android app because no MODE_CHANGE was ever sent
    /// for it (see StreamingCoordinator.SendModeChangeBeforeVideoPipelineRecreate).
    /// </summary>
    public void SendCaps(int width, int height, byte[] supportedHz, byte[] supportedCodecs, int dpi = 400, int maxTouchPoints = 10)
    {
        var caps = new CapsMessage(
            Width: (ushort)width,
            Height: (ushort)height,
            Dpi: (ushort)dpi,
            SupportedHz: supportedHz,
            SupportedCodecs: supportedCodecs,
            MaxTouchPoints: (byte)maxTouchPoints);
        MessageFraming.WriteFramed(_controlWriter!, caps);
        _controlWriter!.Flush();
    }

    /// <summary>
    /// Session 11: reconnects the video socket to the (possibly-recreated)
    /// video port and resets VideoDisconnectedAtUtc, so a test can observe a
    /// SECOND resolution-change's own disconnect distinctly from the first
    /// one's -- RecreateFrameSourceForNewResolution rebuilds VideoStreamServer
    /// on the same port after each successful CAPS/EnsureReady cycle, but a
    /// fake device that never reconnects has nothing left for a LATER
    /// recreate to actually close, which would make ordering assertions
    /// compare against stale data from an earlier cycle instead of the one
    /// under test.
    /// </summary>
    public async Task ReattachVideoSocketAsync(int videoPort)
    {
        Observations.VideoDisconnectedAtUtc = null;
        try { _videoClient?.Dispose(); } catch (Exception) { /* best-effort */ }

        _videoClient = new TcpClient();
        await _videoClient.ConnectAsync(_host, videoPort).ConfigureAwait(false);
        Observations.VideoConnected = _videoClient.Connected;
        _videoReadLoopTask = Task.Run(() => VideoReadLoopAsync(_videoClient.GetStream(), _cts!.Token));
    }

    /// <summary>
    /// Background reader for the video socket -- the fake device previously
    /// never read from it (only checked TcpClient.Connected once at
    /// connect time), so there was no way to observe WHEN the PC actually
    /// closes/reopens the video connection (VideoStreamServer.Stop() inside
    /// RecreateFrameSourceForNewResolution). Discards any bytes received
    /// (frame content isn't relevant to the ordering test); records
    /// Observations.VideoDisconnectedAtUtc the moment the read loop sees
    /// EOF or the socket throws, which is exactly when the PC side closed
    /// this client's accepted socket.
    /// </summary>
    private async Task VideoReadLoopAsync(NetworkStream stream, CancellationToken token)
    {
        var buffer = new byte[4096];
        try
        {
            while (!token.IsCancellationRequested)
            {
                var n = await stream.ReadAsync(buffer, token).ConfigureAwait(false);
                if (n <= 0)
                {
                    Observations.VideoDisconnectedAtUtc = DateTime.UtcNow;
                    return;
                }
            }
        }
        catch (Exception)
        {
            Observations.VideoDisconnectedAtUtc = DateTime.UtcNow;
        }
    }

    private async Task ReadLoopAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                IProtocolMessage message;
                try
                {
                    message = MessageFraming.ReadFramed(_controlReader!);
                }
                catch (Exception)
                {
                    return; // socket closed / EOF
                }

                switch (message)
                {
                    case ConfigMessage cfg: Observations.ConfigReceived = cfg; break;
                    case ConfigUpdateMessage upd: lock (Observations.ConfigUpdates) Observations.ConfigUpdates.Add(upd); break;
                    case ModeChangeMessage mc:
                        lock (Observations.ModeChanges)
                        {
                            Observations.ModeChanges.Add(mc);
                            Observations.ModeChangeAtUtc.Add(DateTime.UtcNow);
                        }
                        break;
                }
            }
        }
        catch (Exception)
        {
            // best-effort background loop, nothing to propagate to a test thread here.
        }
        await Task.CompletedTask;
    }

    public async Task<ConfigMessage?> WaitForConfigAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (Observations.ConfigReceived != null) return Observations.ConfigReceived;
            await Task.Delay(20).ConfigureAwait(false);
        }
        return Observations.ConfigReceived;
    }

    public async Task<bool> WaitForConfigUpdateAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            lock (Observations.ConfigUpdates)
            {
                if (Observations.ConfigUpdates.Count > 0) return true;
            }
            await Task.Delay(20).ConfigureAwait(false);
        }
        return false;
    }

    public async Task<bool> WaitForModeChangeAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            lock (Observations.ModeChanges)
            {
                if (Observations.ModeChanges.Count > 0) return true;
            }
            await Task.Delay(20).ConfigureAwait(false);
        }
        return false;
    }

    /// <summary>
    /// Waits until at least <paramref name="count"/> ModeChangeMessages have
    /// been observed (session 11) -- used to distinguish "the ModeChange
    /// from the SECOND CAPS" from one that may have already arrived from
    /// the first/initial handshake, without racily clearing shared state.
    /// </summary>
    public async Task<bool> WaitForModeChangeCountAsync(int count, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            lock (Observations.ModeChanges)
            {
                if (Observations.ModeChanges.Count >= count) return true;
            }
            await Task.Delay(20).ConfigureAwait(false);
        }
        return false;
    }

    /// <summary>Waits until the video socket's read loop observes the PC closing the connection (see VideoReadLoopAsync).</summary>
    public async Task<bool> WaitForVideoDisconnectAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (Observations.VideoDisconnectedAtUtc != null) return true;
            await Task.Delay(20).ConfigureAwait(false);
        }
        return false;
    }

    public void SendTouchEvent(byte pointerId, byte action, ushort x, ushort y, ushort pressure = 30000, byte toolType = 0, ulong timestampUs = 0)
    {
        var evt = new TouchEventMessage(pointerId, action, x, y, pressure, toolType, timestampUs);
        MessageFraming.WriteFramed(_controlWriter!, evt);
        _controlWriter!.Flush();
    }

    public void SendSettingRequest(byte key, ulong value)
    {
        var req = new SettingRequestMessage(key, value);
        MessageFraming.WriteFramed(_controlWriter!, req);
        _controlWriter!.Flush();
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _controlWriter?.Dispose();
        _controlReader?.Dispose();
        _videoClient?.Dispose();
        _controlClient?.Dispose();
        _cts?.Dispose();
    }
}
