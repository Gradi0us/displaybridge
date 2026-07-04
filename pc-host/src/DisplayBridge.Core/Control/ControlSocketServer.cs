// ControlSocketServer.cs — wiring-E2E piece (session 3): the control
// socket (port 29501 per CONTEXT-BRIEF §2 / decisions log) was previously
// only a protocol *definition* (schema.yaml -> Messages.cs) with no actual
// TCP server anywhere in the codebase. This class is that server: it
// accepts the tablet's control connection, deserializes framed messages via
// the generated MessageFraming.ReadFramed, and raises one C# event per
// inbound message type so StreamingCoordinator (or tests / fake-device) can
// wire up CAPS->CONFIG handshake, TOUCH_EVENT/TOUCH_BATCH -> input
// injection, and SETTING_REQUEST -> SettingsStore without this class having
// to know about InputModeClassifier/SettingsStore itself (keeps it
// unit/integration-testable standalone, same pattern as VideoStreamServer).
using System.Net;
using System.Net.Sockets;
using DisplayBridge.Core.Protocol.Generated;

namespace DisplayBridge.Core.Control;

public sealed class ControlSocketServer : IDisposable
{
    public const int DefaultPort = 29501;

    private readonly int _requestedPort;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoopTask;
    private bool _disposed;

    // PoC scope (matches VideoStreamServer): single active client at a
    // time. Kept so SendXxx() has somewhere to write to.
    private TcpClient? _activeClient;
    private BinaryWriter? _activeWriter;
    private readonly object _writeLock = new();

    public int BoundPort { get; private set; }
    public bool HasClient => _activeClient?.Connected == true;

    public event Action? ClientConnected;
    public event Action<CapsMessage>? CapsReceived;
    public event Action<SettingRequestMessage>? SettingRequestReceived;
    public event Action<ModeAckMessage>? ModeAckReceived;
    public event Action<StatsMessage>? StatsReceived;
    public event Action<TouchEventMessage>? TouchEventReceived;
    public event Action<TouchBatchMessage>? TouchBatchReceived;
    public event Action<Exception>? ClientError;

    public ControlSocketServer(int port = DefaultPort)
    {
        _requestedPort = port;
    }

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
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }

            _ = Task.Run(() => ServeClientAsync(client, token), token);
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
            }
            catch (Exception)
            {
                return;
            }

            lock (_writeLock)
            {
                _activeClient = client;
                _activeWriter = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
            }
            ClientConnected?.Invoke();

            using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
            try
            {
                while (!token.IsCancellationRequested && client.Connected)
                {
                    IProtocolMessage message;
                    try
                    {
                        message = MessageFraming.ReadFramed(reader);
                    }
                    catch (EndOfStreamException)
                    {
                        return; // client closed cleanly
                    }
                    catch (IOException)
                    {
                        return;
                    }

                    Dispatch(message);
                }
            }
            catch (Exception ex)
            {
                ClientError?.Invoke(ex);
            }
            finally
            {
                lock (_writeLock)
                {
                    if (ReferenceEquals(_activeClient, client))
                    {
                        _activeClient = null;
                        _activeWriter?.Dispose();
                        _activeWriter = null;
                    }
                }
            }
        }
    }

    private void Dispatch(IProtocolMessage message)
    {
        switch (message)
        {
            case CapsMessage caps: CapsReceived?.Invoke(caps); break;
            case SettingRequestMessage req: SettingRequestReceived?.Invoke(req); break;
            case ModeAckMessage ack: ModeAckReceived?.Invoke(ack); break;
            case StatsMessage stats: StatsReceived?.Invoke(stats); break;
            case TouchEventMessage touch: TouchEventReceived?.Invoke(touch); break;
            case TouchBatchMessage batch: TouchBatchReceived?.Invoke(batch); break;
            // ConfigMessage/ConfigUpdateMessage/ModeChangeMessage are PC->tablet
            // only; receiving one from a client is unexpected but non-fatal.
        }
    }

    /// <summary>Sends a framed message to the currently connected client, if any. No-op (returns false) if nobody is connected.</summary>
    public bool Send(IProtocolMessage message)
    {
        lock (_writeLock)
        {
            if (_activeWriter == null) return false;
            try
            {
                MessageFraming.WriteFramed(_activeWriter, message);
                _activeWriter.Flush();
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _listener?.Stop(); } catch (Exception) { }
        _listener = null;
        lock (_writeLock)
        {
            _activeWriter?.Dispose();
            _activeWriter = null;
            _activeClient = null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        Stop();
        _cts?.Dispose();
        _disposed = true;
    }
}
