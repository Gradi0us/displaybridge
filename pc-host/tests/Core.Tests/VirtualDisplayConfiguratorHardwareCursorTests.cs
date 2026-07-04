using System.Xml.Linq;
using DisplayBridge.Core.Video;
using Xunit;

namespace DisplayBridge.Core.Tests;

/// <summary>
/// Covers the cursor-disappears-crossing-monitors workaround (session 15):
/// user reported the mouse cursor vanishing when moving from the primary
/// screen onto "VDD by MTT" (the virtual tablet display). Root cause
/// research (GitHub issues VirtualDrivers/Virtual-Display-Driver#25/#447,
/// upstream microsoft/Windows-driver-samples#531) points at a known,
/// unresolved IddCx hardware-cursor rendering limitation, not a bug in
/// DisplayBridge's own CursorInjector (which already uses SetCursorPos with
/// real per-monitor pixel coordinates -- verified correct via real
/// GetCursorPos evidence in session 10, see VirtualMonitorLocatorTests).
/// The chosen workaround is &lt;HardwareCursor&gt;false&lt;/HardwareCursor&gt;
/// in vdd_settings.xml (Windows draws a software cursor instead of the
/// driver), enforced every time VirtualDisplayConfigurator rewrites the
/// file so a driver reinstall (which ships HardwareCursor=true by default)
/// can never silently undo it.
/// </summary>
public class VirtualDisplayConfiguratorHardwareCursorTests : IDisposable
{
    private readonly string _tempSettingsPath;

    public VirtualDisplayConfiguratorHardwareCursorTests()
    {
        _tempSettingsPath = Path.Combine(Path.GetTempPath(), $"vdd-settings-test-{Guid.NewGuid():N}.xml");
    }

    public void Dispose()
    {
        if (File.Exists(_tempSettingsPath)) File.Delete(_tempSettingsPath);
    }

    private void WriteFixture(bool hardwareCursorInitiallyTrue)
    {
        var value = hardwareCursorInitiallyTrue ? "true" : "false";
        File.WriteAllText(_tempSettingsPath, $"""
            <?xml version="1.0" encoding="utf-8"?>
            <vdd_settings>
                <monitors><count>1</count></monitors>
                <gpu><friendlyname>default</friendlyname></gpu>
                <global><g_refresh_rate>60</g_refresh_rate></global>
                <resolutions></resolutions>
                <options>
                    <CustomEdid>false</CustomEdid>
                    <HardwareCursor>{value}</HardwareCursor>
                    <SDR10bit>false</SDR10bit>
                </options>
            </vdd_settings>
            """);
    }

    [Fact]
    public void ApplyResolution_HardwareCursorWasTrue_RewrittenToFalse()
    {
        WriteFixture(hardwareCursorInitiallyTrue: true);
        var configurator = new VirtualDisplayConfigurator(settingsPath: _tempSettingsPath, devconPath: null);

        var applied = configurator.ApplyResolution(3000, 1920, new[] { 60, 90, 120 }, out var error);

        Assert.True(applied, error);
        var doc = XDocument.Load(_tempSettingsPath);
        var hwCursor = doc.Root!.Element("options")!.Element("HardwareCursor")!.Value;
        Assert.Equal("false", hwCursor);
    }

    [Fact]
    public void ApplyResolution_HardwareCursorAlreadyFalse_StaysFalse()
    {
        WriteFixture(hardwareCursorInitiallyTrue: false);
        var configurator = new VirtualDisplayConfigurator(settingsPath: _tempSettingsPath, devconPath: null);

        var applied = configurator.ApplyResolution(3000, 1920, new[] { 60, 90, 120 }, out var error);

        Assert.True(applied, error);
        var doc = XDocument.Load(_tempSettingsPath);
        var hwCursor = doc.Root!.Element("options")!.Element("HardwareCursor")!.Value;
        Assert.Equal("false", hwCursor);
    }

    [Fact]
    public void ApplyResolution_StillWritesResolutionBlock_HardwareCursorFixIsAdditive()
    {
        // Regression guard: the HardwareCursor enforcement must not break
        // the pre-existing, already-relied-upon <resolutions> rewrite.
        WriteFixture(hardwareCursorInitiallyTrue: true);
        var configurator = new VirtualDisplayConfigurator(settingsPath: _tempSettingsPath, devconPath: null);

        var applied = configurator.ApplyResolution(1920, 1080, new[] { 60 }, out var error);

        Assert.True(applied, error);
        var doc = XDocument.Load(_tempSettingsPath);
        var resolution = doc.Root!.Element("resolutions")!.Element("resolution")!;
        Assert.Equal("1920", resolution.Element("width")!.Value);
        Assert.Equal("1080", resolution.Element("height")!.Value);
    }
}
