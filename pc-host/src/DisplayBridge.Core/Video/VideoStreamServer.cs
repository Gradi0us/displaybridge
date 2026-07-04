// VideoStreamServer.cs — M1.1/M1.3 TCP video socket server.
// Listens on the video port (default 29500 per CONTEXT-BRIEF §2), and on
// client connect pulls frames from an IFrameSource and writes them using
// the existing VideoFrameHeader wire format (len:u32 + flags:u8 +
// ptsUs:u64 + payload), reusing the generated record's own
// Serialize/Deserialize rather than inventing new framing.
using System.Net;
using System.Net.Sockets;
using DisplayBridge.Core.Protocol.Generated;

namespace DisplayBridge.Core.Video;

public sealed class VideoStreamServer : IDisposable
{
    public const int DefaultPort = 29500;

    private readonly IFrameSource _frameSource;
    private readonly int _requestedPort;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoopTask;
    private bool _disposed;

    // Session 6: Stop() used to cancel the token and return immediately
    // without waiting for the accept loop / per-client serve loops to
    // actually observe cancellation and exit. That was harmless while the
    // native DLL couldn't load (every GetNextFrame() call was a no-op
    // fallback), but now that DisplayBridge_CaptureGetFrame() is a real
    // P/Invoke into process-global native state, a leftover ServeClientAsync
    // task calling it AFTER StreamingCoordinator.Stop() disposes the
    // IFrameSource (which tears down that same global native state) is a
    // genuine use-after-free -- reproduced as an AccessViolationException
    // in Integration.Tests once two StreamingCoordinator instances ran back
    // to back in the same process. Track spawned client tasks and wait for
    // them (bounded) before Stop() returns, so callers can safely dispose
    // the frame source right after.
    private readonly System.Collections.Concurrent.ConcurrentBag<Task> _clientTasks = new();

    /// <summary>
    /// The actual bound port — useful when constructed with port 0 for
    /// tests, since the OS assigns an ephemeral port.
    /// </summary>
    public int BoundPort { get; private set; }

    public VideoStreamServer(IFrameSource frameSource, int port = DefaultPort)
    {
        _frameSource = frameSource ?? throw new ArgumentNullException(nameof(frameSource));
        _requestedPort = port;
    }

    /// <summary>
    /// Fired after each frame is successfully written to a connected
    /// client (payload byte count, isJpeg, ptsUs). Session 4: gives
    /// StreamingCoordinator/App.xaml.cs something concrete to log as
    /// evidence that real frames crossed the wire, instead of only being
    /// able to say "server started" (see task brief Việc 5.6).
    /// </summary>
    public event Action<int, bool, ulong>? FrameWritten;

    /// <summary>
    /// Writes one VideoFrameHeader + payload to the given stream, using
    /// the schema-defined wire format (VideoFrameHeader.Serialize).
    /// Exposed as a static helper so it's directly unit-testable without
    /// standing up a socket.
    /// </summary>
    public static void WriteFrame(Stream stream, ulong ptsUs, byte flags, ReadOnlySpan<byte> payload)
    {
        using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        var header = new VideoFrameHeader((uint)payload.Length, flags, ptsUs);
        header.Serialize(writer);
        writer.Write(payload);
        writer.Flush();
    }

    /// <summary>
    /// Reads one VideoFrameHeader + payload from the given stream. Mirror
    /// of <see cref="WriteFrame"/>, used by tests (and eventually by any
    /// PC-side diagnostic/replay tooling) to validate framing.
    /// </summary>
    public static (VideoFrameHeader Header, byte[] Payload) ReadFrame(Stream stream)
    {
        using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        var header = VideoFrameHeader.Deserialize(reader);
        var payload = reader.ReadBytes(checked((int)header.Len));
        return (header, payload);
    }

    /// <summary>
    /// Starts listening. Safe to call once; use <see cref="BoundPort"/>
    /// afterwards to discover the actual port when constructed with 0.
    /// </summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_listener != null) return;

        _listener = new TcpListener(IPAddress.Any, _requestedPort);
        _listener.Start();
        BoundPort = ((IPEndPoint)_listener.LocalEndpoint).Port;

        _cts = new CancellationTokenSource();
        _acceptLoopTask = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    private async Task AcceptLoopAsync(CancellationToken token)
    {
        if (_listener == null) return;

        while (!token.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            // PoC scope: serve one client's stream loop at a time, fire
            // and forget — real M1.3 hardening (multi-client rejection,
            // reconnect FSM) is out of scope for this task.
            var clientTask = Task.Run(() => ServeClientAsync(client, token), token);
            _clientTasks.Add(clientTask);
        }
    }

    private async Task ServeClientAsync(TcpClient client, CancellationToken token)
    {
        using (client)
        {
            NetworkStream stream;
            try
            {
                stream = client.GetStream();
                // Session 16 deadlock fix: without a write timeout, a sync
                // NetworkStream.Write to a half-dead socket (adb forward peer
                // force-stopped mid-stream) can block for MINUTES with the
                // send buffer full. Stop()'s 2s WaitAll then times out and
                // RecreateFrameSourceForNewResolution() disposes the native
                // encoder while this loop is still inside GetNextFrame() /
                // WriteFrame() -- observed live 2026-07-04 06:43 as the host
                // wedging forever after the Android app restarted to apply
                // HEVC. 5s is far beyond any healthy adb-forward write.
                stream.WriteTimeout = 5000;
            }
            catch (Exception)
            {
                return;
            }

            var consecutiveNulls = 0;

            while (!token.IsCancellationRequested && client.Connected)
            {
                EncodedFrame? frame;
                try
                {
                    frame = _frameSource.GetNextFrame();
                }
                catch (Exception)
                {
                    // Frame source failure — drop this client's loop.
                    return;
                }

                if (frame is null)
                {
                    // Session 17: re-applies the session-16 fps-ceiling fix
                    // (originally reverted in this same session after it
                    // appeared to cause Host crashes). Root-caused via
                    // NativeSmokeTest, independent of this loop entirely:
                    // DesktopDuplicationCapture's captured width/height
                    // (DXGI DesktopCoordinates delta) is not guaranteed
                    // even, and NV12's 4:2:0 chroma subsampling needs even
                    // dimensions -- odd dims (reproduced at 1013x1011) broke
                    // GPU NV12 texture creation (E_INVALIDARG) AND
                    // heap-corrupted the CPU fallback's UV-plane buffer
                    // (see DesktopDuplicationCapture.cpp/H264Encoder.cpp
                    // "& ~1u" fixes), which is what actually crashed the
                    // Host -- confirmed fixed via a clean 15s/899-frame
                    // NativeSmokeTest run with zero crashes after that fix.
                    // Safe to remove the artificial delay again: native
                    // GetNextFrame() already blocks on AcquireFrame's vsync
                    // wait, which paces this loop for free. See
                    // NativeCaptureEncoder.cs for the original fps-ceiling
                    // analysis.
                    consecutiveNulls++;
                    if (consecutiveNulls < 100)
                    {
                        continue;
                    }
                    try
                    {
                        await Task.Delay(5, token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    consecutiveNulls = 0;
                    continue;
                }

                consecutiveNulls = 0;

                try
                {
                    byte flags = (byte)((frame.IsKeyframe ? FrameFlags.Keyframe : 0) | (frame.IsJpeg ? FrameFlags.Jpeg : 0));
                    WriteFrame(stream, frame.PtsUs, flags, frame.Data);
                    FrameWritten?.Invoke(frame.Data.Length, frame.IsJpeg, frame.PtsUs);
                }
                catch (IOException)
                {
                    return; // client disconnected
                }
                catch (SocketException)
                {
                    return;
                }
            }
        }
    }

    /// <summary>
    /// Cancels the accept loop and every in-flight per-client serve loop,
    /// then BLOCKS (bounded) until they've actually exited. Callers (see
    /// StreamingCoordinator.Stop()) rely on this returning only after every
    /// in-flight IFrameSource.GetNextFrame() call has finished, so it's
    /// safe to Dispose() the frame source immediately afterwards -- see the
    /// class-level comment on _clientTasks for why this matters now that
    /// the native capture/encode DLL is real.
    /// </summary>
    public void Stop()
    {
        _cts?.Cancel();
        try
        {
            _listener?.Stop();
        }
        catch (Exception)
        {
            // best-effort
        }
        _listener = null;

        var tasksToAwait = new List<Task>(_clientTasks);
        if (_acceptLoopTask != null)
        {
            tasksToAwait.Add(_acceptLoopTask);
        }

        if (tasksToAwait.Count > 0)
        {
            try
            {
                // Bounded wait: a well-behaved loop reacts to cancellation
                // within milliseconds (Task.Delay(5) polling / awaited
                // AcceptTcpClientAsync(token)); 2s is generous headroom so
                // Stop() can never hang the caller indefinitely.
                Task.WaitAll(tasksToAwait.ToArray(), TimeSpan.FromSeconds(2));
            }
            catch (AggregateException)
            {
                // Individual task faults/cancellations are expected here
                // (OperationCanceledException etc.) -- Stop() only cares
                // that they've finished running, not how.
            }
        }

        _clientTasks.Clear();
        _acceptLoopTask = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        Stop();
        _cts?.Dispose();
        _disposed = true;
    }
}
