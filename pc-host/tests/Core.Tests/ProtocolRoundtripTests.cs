using System.IO;
using System.Linq;
using DisplayBridge.Core.Protocol.Generated;
using Xunit;

namespace DisplayBridge.Core.Tests;

/// <summary>
/// One roundtrip test per generated message type: Serialize then Deserialize,
/// assert every field matches the original value. Covers all 7 control-socket
/// message types defined in tools/protocol-schema/schema.yaml.
/// </summary>
public class ProtocolRoundtripTests
{
    private static T Roundtrip<T>(T message, System.Func<BinaryReader, T> deserialize)
        where T : IProtocolMessage
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            message.Serialize(writer);
        }
        stream.Position = 0;
        using var reader = new BinaryReader(stream);
        return deserialize(reader);
    }

    [Fact]
    public void Caps_Roundtrips()
    {
        // NB: record-generated Equals uses reference equality for IReadOnlyList<T>/byte[]
        // fields, so list-bearing messages are asserted field-by-field (with sequence
        // equality for the collections) rather than via a single Assert.Equal(record).
        var original = new CapsMessage(3000, 1920, 400, new byte[] { 60, 90, 120, 144 }, new byte[] { 0, 1 }, 10);
        var result = Roundtrip(original, CapsMessage.Deserialize);
        Assert.Equal(original.Width, result.Width);
        Assert.Equal(original.Height, result.Height);
        Assert.Equal(original.Dpi, result.Dpi);
        Assert.True(original.SupportedHz.SequenceEqual(result.SupportedHz));
        Assert.True(original.SupportedCodecs.SequenceEqual(result.SupportedCodecs));
        Assert.Equal(original.MaxTouchPoints, result.MaxTouchPoints);
    }

    [Fact]
    public void Config_Roundtrips()
    {
        var original = new ConfigMessage(1, 3000, 1920, 120, 69000);
        var result = Roundtrip(original, ConfigMessage.Deserialize);
        Assert.Equal(original, result);
    }

    [Fact]
    public void SettingRequest_Roundtrips()
    {
        var original = new SettingRequestMessage(5, 123456789UL);
        var result = Roundtrip(original, SettingRequestMessage.Deserialize);
        Assert.Equal(original, result);
    }

    [Fact]
    public void SettingRequest_Roundtrips_LargeVarint()
    {
        // Exercise multi-byte varint encoding (> 1 byte of LEB128).
        var original = new SettingRequestMessage(1, ulong.MaxValue);
        var result = Roundtrip(original, SettingRequestMessage.Deserialize);
        Assert.Equal(original, result);
    }

    [Fact]
    public void ConfigUpdate_Roundtrips()
    {
        var original = new ConfigUpdateMessage(new[]
        {
            new ConfigEntry(1, 100),
            new ConfigEntry(2, 0),
            new ConfigEntry(3, 4294967295UL),
        });
        var result = Roundtrip(original, ConfigUpdateMessage.Deserialize);
        Assert.True(original.Settings.SequenceEqual(result.Settings));
    }

    [Fact]
    public void ConfigUpdate_Roundtrips_EmptyList()
    {
        var original = new ConfigUpdateMessage(System.Array.Empty<ConfigEntry>());
        var result = Roundtrip(original, ConfigUpdateMessage.Deserialize);
        Assert.Empty(result.Settings);
    }

    [Fact]
    public void ModeChange_Roundtrips()
    {
        var original = new ModeChangeMessage(3000, 1920, 120, 1, new byte[] { 0x00, 0x01, 0x02, 0x03, 0xFF });
        var result = Roundtrip(original, ModeChangeMessage.Deserialize);
        Assert.Equal(original.Width, result.Width);
        Assert.Equal(original.Height, result.Height);
        Assert.Equal(original.Hz, result.Hz);
        Assert.Equal(original.Codec, result.Codec);
        Assert.True(original.Csd.SequenceEqual(result.Csd));
    }

    [Fact]
    public void ModeAck_Roundtrips()
    {
        var original = new ModeAckMessage(0);
        var result = Roundtrip(original, ModeAckMessage.Deserialize);
        Assert.Equal(original, result);
    }

    [Fact]
    public void Stats_Roundtrips()
    {
        var original = new StatsMessage(120, 6, 2, 4294967295U);
        var result = Roundtrip(original, StatsMessage.Deserialize);
        Assert.Equal(original, result);
    }

    [Fact]
    public void TouchEvent_Roundtrips()
    {
        var original = new TouchEventMessage(3, 1, 32768, 40000, 12000, 1, 123456789012UL);
        var result = Roundtrip(original, TouchEventMessage.Deserialize);
        Assert.Equal(original, result);
    }

    [Fact]
    public void TouchBatch_Roundtrips()
    {
        var original = new TouchBatchMessage(new[]
        {
            new TouchEventItem(0, 0, 100, 200, 60000, 0, 1),
            new TouchEventItem(1, 1, 101, 201, 60000, 0, 2),
            new TouchEventItem(0, 2, 102, 202, 0, 0, 3),
        });
        var result = Roundtrip(original, TouchBatchMessage.Deserialize);
        Assert.True(original.Events.SequenceEqual(result.Events));
    }

    [Fact]
    public void TouchBatch_Roundtrips_EmptyList()
    {
        var original = new TouchBatchMessage(System.Array.Empty<TouchEventItem>());
        var result = Roundtrip(original, TouchBatchMessage.Deserialize);
        Assert.Empty(result.Events);
    }
}
