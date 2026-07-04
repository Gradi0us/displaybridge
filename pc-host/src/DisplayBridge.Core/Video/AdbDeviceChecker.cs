// AdbDeviceChecker.cs — no-ADB auto-disable task (session 15).
//
// User request: "nếu không có adb được kết nối thì sẽ tạm thời ngưng hoạt
// động của driver đi để k bị nhầm thành 2 màn" -- avoid Windows showing "VDD
// by MTT" as a second monitor when no tablet is actually plugged in over
// ADB. This class ONLY answers "is any real ADB device connected right
// now?" by shelling out to `adb devices` -- it does not touch the driver
// itself (see DriverManager.DisableDevice/EnableDevice for that, and
// StreamingCoordinator's polling loop for the wiring between the two).
using System.Diagnostics;

// Lets Core.Tests exercise ParseDeviceCount (the `adb devices` output
// parser) directly instead of only through a real/mocked process.
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Core.Tests")]

namespace DisplayBridge.Core.Video;

/// <summary>Outcome of a single ADB connectivity check.</summary>
public enum AdbConnectionState
{
    /// <summary>At least one device is listed with state "device" (authorized, ready).</summary>
    Connected,

    /// <summary>adb ran fine and reported zero authorized devices.</summary>
    Disconnected,

    /// <summary>
    /// Could not determine the real state (adb.exe not found, process
    /// failed to start, or timed out). Callers MUST treat this as "no
    /// change" -- never disable the driver on an Indeterminate result, since
    /// that would punish users whose machine simply doesn't have adb on
    /// PATH/well-known SDK path, not users who genuinely unplugged the
    /// tablet.
    /// </summary>
    Indeterminate,
}

/// <summary>
/// Seam so StreamingCoordinator's polling loop can be unit-tested without
/// shelling out to a real adb.exe (mirrors the IDriverManager/ICursorInjector
/// fake-in-tests pattern already used throughout this codebase).
/// </summary>
public interface IAdbDeviceChecker
{
    AdbConnectionState CheckConnectionState();
}

/// <summary>
/// Real implementation: runs "adb devices" with a short timeout and parses
/// stdout. Never throws -- every failure mode (adb not found, timeout,
/// unexpected output) maps to <see cref="AdbConnectionState.Indeterminate"/>
/// plus a detail string for logging, per the task's explicit
/// "không throw crash app" constraint.
/// </summary>
public sealed class AdbDeviceChecker : IAdbDeviceChecker
{
    /// <summary>
    /// Per-invocation timeout for `adb devices`. Kept short (2-3s per the
    /// task brief) so a hung/missing adb.exe can never make the polling
    /// loop (and therefore the host app) feel like it's stuck.
    /// </summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(3);

    private readonly string? _adbPath;
    private readonly TimeSpan _timeout;

    public AdbDeviceChecker(string? adbPath = null, TimeSpan? timeout = null)
    {
        _adbPath = adbPath ?? FindAdbExe();
        _timeout = timeout ?? DefaultTimeout;
    }

    /// <summary>Resolved adb.exe path used by this checker, exposed for logging/diagnostics.</summary>
    public string? AdbPath => _adbPath;

    public string LastDetail { get; private set; } = string.Empty;

    public AdbConnectionState CheckConnectionState()
    {
        if (_adbPath is null)
        {
            LastDetail = "adb.exe khong tim thay (khong co trong PATH lan khong co o Android SDK platform-tools mac dinh) -- khong the kiem tra thiet bi ADB.";
            return AdbConnectionState.Indeterminate;
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = _adbPath,
                Arguments = "devices",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            using var process = Process.Start(psi);
            if (process is null)
            {
                LastDetail = "khong khoi chay duoc adb.exe.";
                return AdbConnectionState.Indeterminate;
            }

            if (!process.WaitForExit((int)_timeout.TotalMilliseconds))
            {
                // Never leave a stray adb.exe running / block the caller
                // longer than the configured timeout.
                try { process.Kill(entireProcessTree: true); } catch { /* best-effort */ }
                LastDetail = $"adb devices khong phan hoi trong {_timeout.TotalSeconds}s -- coi nhu khong xac dinh duoc.";
                return AdbConnectionState.Indeterminate;
            }

            var stdout = process.StandardOutput.ReadToEnd();
            LastDetail = stdout.Trim();

            return ParseDeviceCount(stdout) > 0
                ? AdbConnectionState.Connected
                : AdbConnectionState.Disconnected;
        }
        catch (Exception ex)
        {
            LastDetail = $"loi khi goi adb devices: {ex.GetType().Name}: {ex.Message}";
            return AdbConnectionState.Indeterminate;
        }
    }

    /// <summary>
    /// Counts lines of `adb devices` output that represent a real,
    /// authorized device -- i.e. end with the literal state "device" (tab-
    /// separated), NOT "unauthorized"/"offline"/"no permissions" and not the
    /// "List of devices attached" header line. Internal (not private) so
    /// unit tests can exercise the parser directly against captured sample
    /// output without spawning a process.
    /// </summary>
    internal static int ParseDeviceCount(string adbDevicesOutput)
    {
        var count = 0;
        foreach (var rawLine in adbDevicesOutput.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r').Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("List of devices attached", StringComparison.OrdinalIgnoreCase)) continue;

            var parts = line.Split('\t', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length >= 2 && string.Equals(parts[^1], "device", StringComparison.OrdinalIgnoreCase))
            {
                count++;
            }
        }
        return count;
    }

    /// <summary>
    /// Looks for adb.exe: PATH first (let the OS/shell resolve it, works for
    /// most Android dev setups), then the well-known default Android SDK
    /// platform-tools location. Returns null if neither exists -- callers
    /// must treat that as Indeterminate, never as "disconnected".
    /// </summary>
    private static string? FindAdbExe()
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in pathEnv.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            try
            {
                var candidate = Path.Combine(dir, "adb.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException)
            {
                // Malformed PATH entry -- skip it, don't crash the search.
            }
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var defaultSdkAdb = Path.Combine(localAppData, "Android", "Sdk", "platform-tools", "adb.exe");
        return File.Exists(defaultSdkAdb) ? defaultSdkAdb : null;
    }
}
