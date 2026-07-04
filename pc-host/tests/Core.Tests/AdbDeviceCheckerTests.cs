using DisplayBridge.Core.Video;
using Xunit;

namespace DisplayBridge.Core.Tests;

/// <summary>
/// Covers the "no ADB device connected -> temporarily disable the VDD by MTT
/// driver" task (session 15). AdbDeviceChecker.ParseDeviceCount is the only
/// part of this class that's meaningfully unit-testable without a real
/// adb.exe/tablet -- it's the piece that decides Connected vs Disconnected
/// from captured `adb devices` output, and getting the string parsing wrong
/// (e.g. treating "unauthorized"/"offline" as connected) would directly
/// cause the driver to stay enabled when it shouldn't, or vice versa.
/// </summary>
public class AdbDeviceCheckerTests
{
    [Fact]
    public void ParseDeviceCount_NoDevicesLine_ReturnsZero()
    {
        const string output = "List of devices attached\n\n";
        Assert.Equal(0, AdbDeviceChecker.ParseDeviceCount(output));
    }

    [Fact]
    public void ParseDeviceCount_OneAuthorizedDevice_ReturnsOne()
    {
        const string output = "List of devices attached\nAL9SBB4622000114\tdevice\n\n";
        Assert.Equal(1, AdbDeviceChecker.ParseDeviceCount(output));
    }

    [Fact]
    public void ParseDeviceCount_TwoAuthorizedDevices_ReturnsTwo()
    {
        const string output = "List of devices attached\nDEV1\tdevice\nDEV2\tdevice\n\n";
        Assert.Equal(2, AdbDeviceChecker.ParseDeviceCount(output));
    }

    [Theory]
    [InlineData("List of devices attached\nAL9SBB4622000114\tunauthorized\n\n")]
    [InlineData("List of devices attached\nAL9SBB4622000114\toffline\n\n")]
    [InlineData("List of devices attached\nAL9SBB4622000114\tno permissions (missing udev rules?)\n\n")]
    public void ParseDeviceCount_UnauthorizedOrOfflineDevice_DoesNotCountAsConnected(string output)
    {
        Assert.Equal(0, AdbDeviceChecker.ParseDeviceCount(output));
    }

    [Fact]
    public void ParseDeviceCount_MixedAuthorizedAndUnauthorized_CountsOnlyAuthorized()
    {
        const string output = "List of devices attached\nDEV1\tdevice\nDEV2\tunauthorized\n\n";
        Assert.Equal(1, AdbDeviceChecker.ParseDeviceCount(output));
    }

    [Fact]
    public void ParseDeviceCount_EmptyString_ReturnsZero()
    {
        Assert.Equal(0, AdbDeviceChecker.ParseDeviceCount(string.Empty));
    }

    [Fact]
    public void CheckConnectionState_AdbPathDoesNotExist_ReturnsIndeterminate_NeverCrashes()
    {
        // Task brief's explicit safety constraint: "an toàn nếu không tìm
        // thấy adb.exe... KHÔNG throw crash app" -- must never throw and
        // must never report Disconnected (which would trigger a real
        // devcon disable) just because adb.exe itself couldn't be found.
        var checker = new AdbDeviceChecker(adbPath: @"C:\this\path\definitely\does\not\exist\adb.exe");

        var state = checker.CheckConnectionState();

        Assert.Equal(AdbConnectionState.Indeterminate, state);
        Assert.NotEmpty(checker.LastDetail);
    }
}
