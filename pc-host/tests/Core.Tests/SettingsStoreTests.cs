using DisplayBridge.Core.Settings;
using Xunit;

namespace Core.Tests;

public class SettingsStoreTests : IDisposable
{
    private readonly string _tempFile;
    private readonly SettingsStore _store;

    public SettingsStoreTests()
    {
        _tempFile = Path.Combine(Path.GetTempPath(), $"displaybridge-settings-test-{Guid.NewGuid():N}.json");
        _store = new SettingsStore(_tempFile);
    }

    public void Dispose()
    {
        if (File.Exists(_tempFile))
        {
            File.Delete(_tempFile);
        }
    }

    [Fact]
    public void Load_WhenFileMissing_ReturnsDefaults()
    {
        var settings = _store.Load();

        Assert.Equal(120, settings.Display.RefreshRateHz);
        Assert.Equal(InputMode.Hybrid, settings.Input.InputMode);
    }

    [Fact]
    public void SaveThenLoad_RoundtripsAllFields()
    {
        var settings = new AppSettings();
        settings.Display.RefreshRateHz = 90;
        settings.Streaming.Codec = Codec.ForceHevc;
        settings.Streaming.QualityPreset = QualityPreset.Ultra;
        settings.Streaming.FpsCap = 90;
        settings.Input.InputMode = InputMode.Touch;
        settings.Input.PenPressureGamma = 1.5;
        settings.Connection.Transport = Transport.Adb;
        settings.Diagnostics.LogLevel = LogLevel.Debug;

        _store.Save(settings);
        var loaded = _store.Load();

        Assert.Equal(90, loaded.Display.RefreshRateHz);
        Assert.Equal(Codec.ForceHevc, loaded.Streaming.Codec);
        Assert.Equal(QualityPreset.Ultra, loaded.Streaming.QualityPreset);
        Assert.Equal(90, loaded.Streaming.FpsCap);
        Assert.Equal(InputMode.Touch, loaded.Input.InputMode);
        Assert.Equal(1.5, loaded.Input.PenPressureGamma);
        Assert.Equal(Transport.Adb, loaded.Connection.Transport);
        Assert.Equal(LogLevel.Debug, loaded.Diagnostics.LogLevel);
    }

    [Fact]
    public void Clamp_RefreshRate144_IsClampedTo120()
    {
        var settings = new AppSettings();
        settings.Display.RefreshRateHz = 144;

        SettingsStore.Clamp(settings);

        Assert.Equal(120, settings.Display.RefreshRateHz);
    }

    [Fact]
    public void Save_RefreshRate144_PersistsAsClamped120()
    {
        var settings = new AppSettings();
        settings.Display.RefreshRateHz = 144;

        _store.Save(settings);
        var loaded = _store.Load();

        Assert.Equal(120, loaded.Display.RefreshRateHz);
    }

    [Fact]
    public void Load_FileMissingNewFields_MigratesWithDefaults()
    {
        // Simulate an old settings.json written before Input/Diagnostics existed.
        var legacyJson = """
        {
          "SchemaVersion": 1,
          "Display": { "ResolutionMode": "Max", "CustomWidth": 0, "CustomHeight": 0, "RefreshRateHz": 60 }
        }
        """; // ResolutionMode/CustomWidth/CustomHeight are intentionally still
             // present in this literal -- it simulates an OLD on-disk file from
             // before the 2026-07-03 preset removal. System.Text.Json must
             // silently ignore unknown members and still populate RefreshRateHz.
        File.WriteAllText(_tempFile, legacyJson);

        var loaded = _store.Load();

        Assert.Equal(60, loaded.Display.RefreshRateHz);
        // Fields absent from legacy JSON fall back to defaults.
        Assert.Equal(InputMode.Hybrid, loaded.Input.InputMode);
        Assert.True(loaded.Streaming.AdaptiveBitrate);
        Assert.Equal(LogLevel.Info, loaded.Diagnostics.LogLevel);
    }

    // NOTE (2026-07-03 decision, session 7): ResolutionMode.Custom and the
    // CustomWidth/CustomHeight fields were removed entirely — resolution is
    // now always 100% native from DeviceCaps, never a user-entered value.
    // The 3 tests that used to live here (Save_CustomResolutionExceedsMax_
    // ThrowsValidationException / Save_CustomResolutionWithinMax_Succeeds /
    // Save_CustomResolutionZero_ThrowsValidationException) are obsolete —
    // replaced by this one confirming Validate() is now a documented no-op
    // regardless of caps, since there is nothing left to validate.
    [Fact]
    public void Validate_NoLongerThrows_ResolutionIsAlwaysFromCaps()
    {
        var caps = new DeviceCaps(1920, 1080);
        var settings = new AppSettings();

        var exception = Record.Exception(() => SettingsStore.Validate(settings, caps));

        Assert.Null(exception);
    }

    [Fact]
    public void Clamp_FpsCapAboveRefreshRate_IsClampedToRefreshRate()
    {
        var settings = new AppSettings();
        settings.Display.RefreshRateHz = 60;
        settings.Streaming.FpsCap = 120;

        SettingsStore.Clamp(settings);

        Assert.Equal(60, settings.Streaming.FpsCap);
    }

    [Fact]
    public void Clamp_PenPressureGammaOutOfRange_IsClamped()
    {
        var high = new AppSettings();
        high.Input.PenPressureGamma = 5.0;
        SettingsStore.Clamp(high);
        Assert.Equal(2.0, high.Input.PenPressureGamma);

        var low = new AppSettings();
        low.Input.PenPressureGamma = 0.1;
        SettingsStore.Clamp(low);
        Assert.Equal(0.5, low.Input.PenPressureGamma);
    }
}
