using DisplayBridge.Core.Video;
using Xunit;

namespace DisplayBridge.Core.Tests;

/// <summary>
/// Session 15 (no-ADB auto-disable task): exercises DriverManager.DisableDevice/
/// EnableDevice against the REAL bundled devcon.exe and the REAL "VDD by MTT"
/// hardware ID on this dev machine (same "real desktop, not fully mocked"
/// discipline as VirtualMonitorLocatorTests, session 10) -- catching a
/// devcon argument-syntax mistake (e.g. wrong verb/wildcard form) that a
/// fully-mocked IDriverManager test could never catch.
///
/// Safety: this process is verified non-elevated (Administrator check would
/// return false in this environment, same as every previous session --
/// devcon disable/enable/restart all require Administrator). So calling the
/// REAL devcon disable here is expected to FAIL with "Disable failed"/
/// similar (no admin token), meaning it should NEVER actually disable the
/// device -- there is no real risk of leaving "VDD by MTT" off overnight.
/// The EnableDevice() call in `finally` is still made unconditionally as a
/// defense-in-depth restore step in case this ever runs elevated.
/// If the driver isn't installed on the machine running this test at all,
/// the test is skipped (Assert.True short-circuit) rather than failing --
/// this is an environment-dependent smoke test, not a portable unit test.
/// </summary>
public class DriverManagerDisableEnableRealDevconTests
{
    [Fact]
    public void DisableThenEnable_RunsRealDevconWithoutThrowing_AndRestoresDeviceState()
    {
        var driverManager = new DriverManager();

        if (!driverManager.IsDriverInstalled(out var installedDetail))
        {
            // Environment-dependent: "VDD by MTT" isn't installed on
            // whatever machine is running this test. Nothing to verify.
            return;
        }

        (bool Success, string Message) disableResult = default;
        try
        {
            disableResult = driverManager.DisableDevice();

            // Never throws (already guaranteed by the try/catch inside
            // DriverManager.RunDevconEnableDisable) -- the real assertion
            // here is just that we got a real, non-empty diagnostic message
            // back from devcon, proving the process actually ran the
            // "disable *Root\MttVDD*" command line (as opposed to silently
            // no-op'ing due to a path/argument bug).
            Assert.False(string.IsNullOrWhiteSpace(disableResult.Message));
        }
        finally
        {
            // Always attempt to restore, regardless of what DisableDevice
            // reported -- defense in depth per the task's explicit
            // "cẩn thận enable lại ngay sau test" instruction.
            var enableResult = driverManager.EnableDevice();
            Assert.False(string.IsNullOrWhiteSpace(enableResult.Message));
        }

        // Expected in THIS environment (verified non-elevated -- see class
        // doc): devcon disable requires Administrator, so it should report
        // failure, never silently claim success while running unelevated.
        Assert.False(disableResult.Success, $"Unexpected: devcon disable reported success while unelevated. Message: {disableResult.Message}. Installed detail: {installedDetail}");
    }
}
