using DisplayBridge.Core.Settings;
using Xunit;

namespace Core.Tests;

public class SettingsChangeClassifierTests
{
    [Theory]
    [InlineData(SettingField.DisplayRefreshRateHz, ApplyType.ReMode)]
    [InlineData(SettingField.StreamingCodec, ApplyType.ReMode)]
    [InlineData(SettingField.StreamingQualityPreset, ApplyType.Live)]
    [InlineData(SettingField.StreamingFpsCap, ApplyType.Live)]
    [InlineData(SettingField.StreamingAdaptiveBitrate, ApplyType.Live)]
    [InlineData(SettingField.StreamingLatencyPriority, ApplyType.Live)]
    [InlineData(SettingField.InputTouchEnabled, ApplyType.Live)]
    [InlineData(SettingField.InputMode, ApplyType.Live)]
    [InlineData(SettingField.InputPenPressureEnabled, ApplyType.Live)]
    [InlineData(SettingField.InputPenPressureGamma, ApplyType.Live)]
    [InlineData(SettingField.ConnectionTransport, ApplyType.Reconnect)]
    [InlineData(SettingField.ConnectionAutoConnect, ApplyType.WindowsApi)]
    [InlineData(SettingField.ConnectionPorts, ApplyType.Reconnect)]
    [InlineData(SettingField.DiagnosticsStatsOverlay, ApplyType.Live)]
    [InlineData(SettingField.DiagnosticsLatencyHud, ApplyType.Live)]
    [InlineData(SettingField.DiagnosticsLogLevel, ApplyType.Live)]
    public void Classify_MatchesCatalogTable(SettingField field, ApplyType expected)
    {
        Assert.Equal(expected, SettingsChangeClassifier.Classify(field));
    }

    [Fact]
    public void ClassifyBatch_NoFields_ReturnsLive()
    {
        Assert.Equal(ApplyType.Live, SettingsChangeClassifier.ClassifyBatch(Array.Empty<SettingField>()));
    }

    [Fact]
    public void ClassifyBatch_AllLiveFields_ReturnsLive()
    {
        var fields = new[] { SettingField.StreamingQualityPreset, SettingField.InputMode };
        Assert.Equal(ApplyType.Live, SettingsChangeClassifier.ClassifyBatch(fields));
    }

    [Fact]
    public void ClassifyBatch_MixOfLiveAndReMode_ReturnsReMode()
    {
        var fields = new[] { SettingField.StreamingQualityPreset, SettingField.DisplayRefreshRateHz };
        Assert.Equal(ApplyType.ReMode, SettingsChangeClassifier.ClassifyBatch(fields));
    }

    [Fact]
    public void ClassifyBatch_MixOfLiveAndReconnect_ReturnsReconnect()
    {
        var fields = new[] { SettingField.DiagnosticsLogLevel, SettingField.ConnectionTransport };
        Assert.Equal(ApplyType.Reconnect, SettingsChangeClassifier.ClassifyBatch(fields));
    }

    [Fact]
    public void ClassifyBatch_ReModeTakesPriorityOverReconnect()
    {
        var fields = new[] { SettingField.ConnectionTransport, SettingField.DisplayRefreshRateHz };
        Assert.Equal(ApplyType.ReMode, SettingsChangeClassifier.ClassifyBatch(fields));
    }
}
