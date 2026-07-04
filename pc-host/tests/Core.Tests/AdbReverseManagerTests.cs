using DisplayBridge.Core.Video;
using Xunit;

namespace DisplayBridge.Core.Tests;

/// <summary>
/// Covers the "PC Host must set up its own adb reverse tunnels" fix (session
/// 18, docs/RCA-v2-android-connect-adb-reverse-lifecycle.md). The two pieces
/// worth unit-testing without a real adb.exe/tablet are:
///   1. ParseReverseList -- turns captured `adb reverse --list` output into the
///      (device, host) pairs currently installed. Getting this wrong means we
///      either re-apply tunnels that already exist (adb spam) or think a tunnel
///      exists when it doesn't (app stays on ECONNREFUSED).
///   2. MissingMappings -- the pure decision of which requested mappings still
///      need applying given what --list reported.
/// </summary>
public class AdbReverseManagerTests
{
    // --- ParseReverseList ---

    [Fact]
    public void ParseReverseList_EmptyString_ReturnsNoPairs()
    {
        Assert.Empty(AdbReverseManager.ParseReverseList(string.Empty));
    }

    [Fact]
    public void ParseReverseList_WhitespaceOnly_ReturnsNoPairs()
    {
        Assert.Empty(AdbReverseManager.ParseReverseList("\n\n   \n"));
    }

    [Fact]
    public void ParseReverseList_TwoExistingRules_ParsesBothPairs()
    {
        // Real `adb reverse --list` shape: "<serial/transport> <remote> <local>".
        const string output = "host-6 tcp:29500 tcp:29500\nhost-6 tcp:29501 tcp:29501\n";
        var pairs = AdbReverseManager.ParseReverseList(output);

        Assert.Equal(2, pairs.Count);
        Assert.Contains((29500, 29500), pairs);
        Assert.Contains((29501, 29501), pairs);
    }

    [Fact]
    public void ParseReverseList_DistinctDeviceAndHostPorts_KeepsOrderRemoteThenLocal()
    {
        // First tcp: token = remote (device) side, second = local (host) side.
        const string output = "AL9SBB4622000114 tcp:6100 tcp:7100\n";
        var pairs = AdbReverseManager.ParseReverseList(output);

        Assert.Single(pairs);
        Assert.Equal((6100, 7100), pairs[0]);
    }

    [Fact]
    public void ParseReverseList_CrlfLineEndings_ParsedSameAsLf()
    {
        const string output = "host-6 tcp:29500 tcp:29500\r\nhost-6 tcp:29501 tcp:29501\r\n";
        var pairs = AdbReverseManager.ParseReverseList(output);

        Assert.Equal(2, pairs.Count);
        Assert.Contains((29500, 29500), pairs);
        Assert.Contains((29501, 29501), pairs);
    }

    [Theory]
    [InlineData("garbage line with no ports\n")]
    [InlineData("host-6 tcp:29500\n")]                  // only one tcp token -> not a full pair
    [InlineData("host-6 tcp:notaport tcp:alsonot\n")]   // non-numeric ports
    [InlineData("List of reverse connections\n")]        // header-ish noise
    public void ParseReverseList_MalformedLines_AreIgnored(string output)
    {
        Assert.Empty(AdbReverseManager.ParseReverseList(output));
    }

    [Fact]
    public void ParseReverseList_MixOfValidAndMalformed_KeepsOnlyValid()
    {
        const string output =
            "garbage\n" +
            "host-6 tcp:29500 tcp:29500\n" +
            "host-6 tcp:oops\n" +
            "host-6 tcp:29501 tcp:29501\n";
        var pairs = AdbReverseManager.ParseReverseList(output);

        Assert.Equal(2, pairs.Count);
        Assert.Contains((29500, 29500), pairs);
        Assert.Contains((29501, 29501), pairs);
    }

    // --- MissingMappings (pure decision logic) ---

    [Fact]
    public void MissingMappings_AllPresent_ReturnsEmpty()
    {
        var requested = new (int, int)[] { (29500, 29500), (29501, 29501) };
        var existing = new (int, int)[] { (29500, 29500), (29501, 29501) };

        Assert.Empty(AdbReverseManager.MissingMappings(requested, existing));
    }

    [Fact]
    public void MissingMappings_NonePresent_ReturnsAllRequested()
    {
        var requested = new (int, int)[] { (29500, 29500), (29501, 29501) };
        var existing = System.Array.Empty<(int, int)>();

        var missing = AdbReverseManager.MissingMappings(requested, existing);

        Assert.Equal(2, missing.Count);
        Assert.Contains((29500, 29500), missing);
        Assert.Contains((29501, 29501), missing);
    }

    [Fact]
    public void MissingMappings_PartiallyPresent_ReturnsOnlyTheMissingOne()
    {
        var requested = new (int, int)[] { (29500, 29500), (29501, 29501) };
        var existing = new (int, int)[] { (29500, 29500) };

        var missing = AdbReverseManager.MissingMappings(requested, existing);

        Assert.Single(missing);
        Assert.Equal((29501, 29501), missing[0]);
    }

    [Fact]
    public void MissingMappings_SamePortNumberButDifferentSides_IsNotAMatch()
    {
        // (29500->29500) requested but only (29500->7100) present -> still missing:
        // the pair must match on BOTH device and host side, not just device port.
        var requested = new (int, int)[] { (29500, 29500) };
        var existing = new (int, int)[] { (29500, 7100) };

        var missing = AdbReverseManager.MissingMappings(requested, existing);

        Assert.Single(missing);
        Assert.Equal((29500, 29500), missing[0]);
    }

    // --- EnsureReverse never-throw contract ---

    [Fact]
    public void EnsureReverse_AdbPathDoesNotExist_ReturnsFalse_NeverCrashes()
    {
        // Same safety constraint AdbDeviceChecker follows: adb.exe missing must
        // map to Ok=false with a detail string, never a thrown exception.
        var mgr = new AdbReverseManager(adbPath: @"C:\this\path\definitely\does\not\exist\adb.exe");

        var (ok, detail) = mgr.EnsureReverse(new[] { (29500, 29500) });

        Assert.False(ok);
        Assert.NotEmpty(detail);
    }

    [Fact]
    public void EnsureReverse_EmptyMappings_ReturnsOkWithNoLog()
    {
        // Nothing requested -> trivially satisfied, empty detail so the caller
        // logs nothing. Uses a bogus adb path to prove it short-circuits before
        // ever trying to run adb.
        var mgr = new AdbReverseManager(adbPath: @"C:\nope\adb.exe");

        var (ok, detail) = mgr.EnsureReverse(System.Array.Empty<(int, int)>());

        Assert.True(ok);
        Assert.Equal(string.Empty, detail);
    }
}
