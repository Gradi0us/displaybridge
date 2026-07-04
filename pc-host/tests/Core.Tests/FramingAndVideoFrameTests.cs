using System.IO;
using DisplayBridge.Core.Protocol.Generated;
using Xunit;

namespace DisplayBridge.Core.Tests;

/// <summary>
/// Covers the control-socket wire envelope (1-byte type + u16 length prefix)
/// and the video-socket frame header, both defined in schema.yaml.
/// </summary>
public class FramingAndVideoFrameTests
{
    [Fact]
    public void WriteFramed_ThenReadFramed_RoundtripsModeAck()
    {
        var original = new ModeAckMessage(2);

        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            MessageFraming.WriteFramed(writer, original);
        }

        // Envelope is: [type:1][length:2][payload]. ModeAck payload is 1 byte (status).
        var bytes = stream.ToArray();
        Assert.Equal((byte)MessageType.ModeAck, bytes[0]);
        Assert.Equal(1, bytes[1] | (bytes[2] << 8)); // u16 little-endian length = 1
        Assert.Equal(4, bytes.Length); // 1 (type) + 2 (length) + 1 (payload)

        stream.Position = 0;
        using var reader = new BinaryReader(stream);
        var result = Assert.IsType<ModeAckMessage>(MessageFraming.ReadFramed(reader));
        Assert.Equal(original.Status, result.Status);
    }

    [Fact]
    public void WriteFramed_ThenReadFramed_RoundtripsAllMessageTypes()
    {
        IProtocolMessage[] messages =
        {
            new CapsMessage(3000, 1920, 400, new byte[] { 60, 120 }, new byte[] { 0, 1 }, 10),
            new ConfigMessage(1, 3000, 1920, 120, 69000),
            new SettingRequestMessage(5, 42),
            new ConfigUpdateMessage(new[] { new ConfigEntry(1, 7) }),
            new ModeChangeMessage(3000, 1920, 90, 0, new byte[] { 1, 2, 3 }),
            new ModeAckMessage(0),
            new StatsMessage(60, 8, 1, 0),
            new TouchEventMessage(0, 0, 100, 200, 60000, 0, 42),
            new TouchBatchMessage(new[] { new TouchEventItem(0, 1, 101, 201, 60000, 0, 43) }),
        };

        foreach (var message in messages)
        {
            using var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                MessageFraming.WriteFramed(writer, message);
            }
            stream.Position = 0;
            using var reader = new BinaryReader(stream);
            var result = MessageFraming.ReadFramed(reader);
            Assert.Equal(message.Type, result.Type);
        }
    }

    [Fact]
    public void VideoFrameHeader_Roundtrips()
    {
        var original = new VideoFrameHeader(123456, 0x01, 987654321012UL);

        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            original.Serialize(writer);
        }
        Assert.Equal(VideoFrameHeader.WireSize, stream.Length);

        stream.Position = 0;
        using var reader = new BinaryReader(stream);
        var result = VideoFrameHeader.Deserialize(reader);
        Assert.Equal(original, result);
    }
}
