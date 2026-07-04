// VideoStreamServerTests.cs — M1.1 video socket framing tests.
// Uses a fake IFrameSource (no native DLL needed) plus a real
// TcpListener/TcpClient round-trip to validate the wire format matches
// schema.yaml's VideoFrameHeader (len:u32 + flags:u8 + ptsUs:u64 +
// payload).
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using DisplayBridge.Core.Protocol.Generated;
using DisplayBridge.Core.Video;
using Xunit;

namespace DisplayBridge.Core.Tests;

/// <summary>
/// Fake frame source for tests: yields queued frames once, then returns
/// null forever (simulating "no more frames ready").
/// </summary>
internal sealed class FakeFrameSource : IFrameSource
{
    private readonly ConcurrentQueue<EncodedFrame> _queue = new();

    public void Enqueue(EncodedFrame frame) => _queue.Enqueue(frame);

    public bool Init() => true;

    public EncodedFrame? GetNextFrame()
    {
        return _queue.TryDequeue(out var frame) ? frame : null;
    }

    public void Shutdown()
    {
    }
}

public class VideoStreamServerTests
{
    [Fact]
    public void WriteFrame_ThenReadFrame_RoundtripsHeaderAndPayload()
    {
        var payload = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0x01, 0x02 };
        const ulong ptsUs = 123456789UL;
        const byte flags = 0x01;

        using var stream = new MemoryStream();
        VideoStreamServer.WriteFrame(stream, ptsUs, flags, payload);
        stream.Position = 0;

        var (header, readPayload) = VideoStreamServer.ReadFrame(stream);

        Assert.Equal((uint)payload.Length, header.Len);
        Assert.Equal(flags, header.Flags);
        Assert.Equal(ptsUs, header.PtsUs);
        Assert.Equal(payload, readPayload);
    }

    [Fact]
    public async Task Server_SendsQueuedFrame_ToConnectedClient_WithCorrectFraming()
    {
        var fakeSource = new FakeFrameSource();
        var payload = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };
        const ulong expectedPts = 987654321UL;
        const byte expectedFlags = 0x01; // keyframe

        fakeSource.Enqueue(new EncodedFrame(payload, expectedPts, IsKeyframe: true));

        // Bind to port 0 so the OS assigns an ephemeral port — avoids
        // colliding with the real 29500 video port or other test runs.
        using var server = new VideoStreamServer(fakeSource, port: 0);
        server.Start();
        Assert.NotEqual(0, server.BoundPort);

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, server.BoundPort);
        using var clientStream = client.GetStream();

        // Read exactly VideoFrameHeader.WireSize + payload.Length bytes,
        // with a timeout so a framing bug fails the test instead of
        // hanging forever.
        var totalExpected = VideoFrameHeader.WireSize + payload.Length;
        var buffer = new byte[totalExpected];
        var readTask = ReadExactAsync(clientStream, buffer, totalExpected);
        var completed = await Task.WhenAny(readTask, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.True(completed == readTask, "Timed out waiting for frame bytes from server.");
        await readTask;

        using var resultStream = new MemoryStream(buffer);
        var (header, readPayload) = VideoStreamServer.ReadFrame(resultStream);

        Assert.Equal((uint)payload.Length, header.Len);
        Assert.Equal(expectedFlags, header.Flags);
        Assert.Equal(expectedPts, header.PtsUs);
        Assert.Equal(payload, readPayload);

        server.Stop();
    }

    private static async Task ReadExactAsync(Stream stream, byte[] buffer, int count)
    {
        var offset = 0;
        while (offset < count)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, count - offset));
            if (read == 0)
            {
                throw new IOException("Stream closed before expected bytes were received.");
            }
            offset += read;
        }
    }
}
