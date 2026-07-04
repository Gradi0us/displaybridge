// AdbReverseManager.cs — adb reverse tunnel lifecycle (session 18, RC1-FIX).
//
// Root cause (docs/RCA-v2-android-connect-adb-reverse-lifecycle.md): the PC
// Host never set up the `adb reverse tcp:29500/29501` tunnels itself -- they
// only ever existed as a manual step, and adb flushes ALL reverse rules
// whenever the adb server restarts / USB replugs / the device reboots. The
// Android app dials 127.0.0.1:29500/29501 ON THE DEVICE, so without the
// reverse tunnel it gets ECONNREFUSED forever even though `adb devices` still
// shows the tablet.
//
// This class is the missing "infra glue": it makes sure the requested reverse
// mappings exist, re-applying any that adb has silently dropped. It ONLY runs
// `adb reverse --list` / `adb reverse tcp:X tcp:Y` -- like AdbDeviceChecker it
// does not touch the driver, the servers, or anything else (see
// StreamingCoordinator.OnAdbPollTick for the wiring that calls this every
// poll tick while Connected, and Start() for the one immediate attempt).
using System.Diagnostics;

// NOTE: [assembly: InternalsVisibleTo("Core.Tests")] is already declared in
// AdbDeviceChecker.cs (same Core assembly) -- do NOT redeclare it here or the
// build fails with a duplicate attribute. That single declaration is what
// lets AdbReverseManagerTests reach the internal static parser below.

namespace DisplayBridge.Core.Video;

/// <summary>
/// Seam so StreamingCoordinator's polling loop can be unit-tested without
/// shelling out to a real adb.exe (mirrors the IAdbDeviceChecker /
/// IDriverManager fake-in-tests pattern already used throughout this codebase).
/// </summary>
public interface IAdbReverseManager
{
    /// <summary>
    /// Makes sure every requested (DevicePort, HostPort) reverse tunnel is in
    /// place. Idempotent: reads the current `adb reverse --list` first and
    /// only applies the mappings that are actually missing, so it can be
    /// called every poll tick without spamming adb.
    /// </summary>
    /// <returns>
    /// Ok = true only if every requested mapping is present (or was applied
    /// successfully). Detail is a short human-readable log string that is
    /// deliberately EMPTY when nothing changed (all mappings already present)
    /// so the caller can avoid logging "already present" every 7s -- see the
    /// XML doc on <see cref="AdbReverseManager.EnsureReverse"/> and the
    /// caller (StreamingCoordinator.OnAdbPollTick) for that contract.
    /// </returns>
    (bool Ok, string Detail) EnsureReverse(IReadOnlyList<(int DevicePort, int HostPort)> mappings);
}

/// <summary>
/// Real implementation: runs `adb reverse --list` / `adb reverse tcp:X tcp:Y`
/// with a short timeout and parses stdout. Never throws -- every failure mode
/// (adb not found, timeout, non-zero exit, unexpected output) maps to
/// Ok=false plus a detail string for logging, per the same "không throw crash
/// app" constraint AdbDeviceChecker follows.
/// </summary>
public sealed class AdbReverseManager : IAdbReverseManager
{
    /// <summary>
    /// Per-invocation timeout for each adb call. Kept short (3s, same as
    /// <see cref="AdbDeviceChecker.DefaultTimeout"/>) so a hung/missing
    /// adb.exe can never make the polling loop (and therefore the host app)
    /// feel like it's stuck.
    /// </summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(3);

    private readonly string? _adbPath;
    private readonly TimeSpan _timeout;

    public AdbReverseManager(string? adbPath = null, TimeSpan? timeout = null)
    {
        _adbPath = adbPath ?? FindAdbExe();
        _timeout = timeout ?? DefaultTimeout;
    }

    /// <summary>Resolved adb.exe path used by this manager, exposed for logging/diagnostics.</summary>
    public string? AdbPath => _adbPath;

    public string LastDetail { get; private set; } = string.Empty;

    /// <summary>
    /// See <see cref="IAdbReverseManager.EnsureReverse"/>. Detail contract:
    /// EMPTY string when nothing was applied (all mappings already present and
    /// OK) -- the caller uses that to log only on a real apply/failure instead
    /// of once every poll tick. A non-empty Detail always accompanies either a
    /// successful apply (Ok=true) or any failure (Ok=false).
    /// </summary>
    public (bool Ok, string Detail) EnsureReverse(IReadOnlyList<(int DevicePort, int HostPort)> mappings)
    {
        if (_adbPath is null)
        {
            LastDetail = "adb.exe khong tim thay (khong co trong PATH lan khong co o Android SDK platform-tools mac dinh) -- khong the thiet lap adb reverse.";
            return (false, LastDetail);
        }

        if (mappings is null || mappings.Count == 0)
        {
            // Nothing requested -- treat as trivially satisfied, no logging.
            LastDetail = string.Empty;
            return (true, string.Empty);
        }

        // 1) Read current reverse rules so we only apply what's missing.
        var list = RunAdb("reverse --list");
        if (!list.Ran)
        {
            LastDetail = list.Error!;
            return (false, LastDetail);
        }
        if (list.ExitCode != 0)
        {
            // e.g. "no devices/emulators found" when the tablet dropped off
            // between the adb-devices check and here -- surface it, Ok=false.
            var stderr = string.IsNullOrWhiteSpace(list.Stderr) ? list.Stdout : list.Stderr;
            LastDetail = $"adb reverse --list loi (exit {list.ExitCode}): {stderr.Trim()}";
            return (false, LastDetail);
        }

        var existing = ParseReverseList(list.Stdout);
        var missing = MissingMappings(mappings, existing);
        if (missing.Count == 0)
        {
            // All tunnels already present -- empty detail so the caller stays
            // quiet (no "already present" spam every 7s).
            LastDetail = string.Empty;
            return (true, string.Empty);
        }

        // 2) Apply each missing mapping. Any single failure -> Ok=false.
        var applied = new List<string>();
        foreach (var (devicePort, hostPort) in missing)
        {
            var res = RunAdb($"reverse tcp:{devicePort} tcp:{hostPort}");
            if (!res.Ran)
            {
                LastDetail = res.Error!;
                return (false, LastDetail);
            }
            if (res.ExitCode != 0)
            {
                var stderr = string.IsNullOrWhiteSpace(res.Stderr) ? res.Stdout : res.Stderr;
                LastDetail = $"adb reverse tcp:{devicePort} tcp:{hostPort} that bai (exit {res.ExitCode}): {stderr.Trim()}";
                return (false, LastDetail);
            }
            applied.Add($"tcp:{devicePort}->tcp:{hostPort}");
        }

        LastDetail = $"da thiet lap adb reverse: {string.Join(", ", applied)}.";
        return (true, LastDetail);
    }

    /// <summary>
    /// Parses `adb reverse --list` output into the (DevicePort, HostPort)
    /// pairs currently installed. Each line looks like
    /// "<c>host-6 tcp:29500 tcp:29500</c>" -- a serial/transport token
    /// followed by the remote (device) socket then the local (host) socket.
    /// We take the first two "tcp:<port>" tokens on each line as
    /// (device, host); lines without at least two such tokens are ignored.
    /// Internal (not private) so unit tests can exercise the parser directly
    /// against captured sample output without spawning a process (same pattern
    /// as <see cref="AdbDeviceChecker.ParseDeviceCount"/>).
    /// </summary>
    internal static IReadOnlyList<(int DevicePort, int HostPort)> ParseReverseList(string reverseListOutput)
    {
        var result = new List<(int, int)>();
        if (string.IsNullOrEmpty(reverseListOutput)) return result;

        foreach (var rawLine in reverseListOutput.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r').Trim();
            if (line.Length == 0) continue;

            int? devicePort = null;
            foreach (var token in line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (!token.StartsWith("tcp:", StringComparison.OrdinalIgnoreCase)) continue;
                if (!int.TryParse(token.AsSpan(4), out var port)) continue;

                if (devicePort is null)
                {
                    devicePort = port; // first tcp: token = remote (device) side
                }
                else
                {
                    result.Add((devicePort.Value, port)); // second = local (host) side
                    break; // only the first pair per line matters
                }
            }
        }
        return result;
    }

    /// <summary>
    /// Pure decision helper: which of the <paramref name="requested"/>
    /// mappings are NOT already present in <paramref name="existing"/>.
    /// Factored out (internal static) so the "what needs applying" logic can
    /// be unit-tested without a process, and so EnsureReverse stays readable.
    /// </summary>
    internal static IReadOnlyList<(int DevicePort, int HostPort)> MissingMappings(
        IReadOnlyList<(int DevicePort, int HostPort)> requested,
        IReadOnlyList<(int DevicePort, int HostPort)> existing)
    {
        var present = new HashSet<(int, int)>(existing);
        var missing = new List<(int, int)>();
        foreach (var m in requested)
        {
            if (!present.Contains(m)) missing.Add(m);
        }
        return missing;
    }

    /// <summary>
    /// Shells out to adb with the given arguments under the configured
    /// timeout. Never throws: Ran=false (with Error set) means adb couldn't be
    /// started or timed out; Ran=true carries ExitCode/Stdout/Stderr. Mirrors
    /// AdbDeviceChecker.CheckConnectionState's process handling, including
    /// killing the whole process tree on timeout so no stray adb.exe lingers.
    /// </summary>
    private (bool Ran, int ExitCode, string Stdout, string Stderr, string? Error) RunAdb(string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = _adbPath,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            using var process = Process.Start(psi);
            if (process is null)
            {
                return (false, 0, string.Empty, string.Empty, "khong khoi chay duoc adb.exe.");
            }

            if (!process.WaitForExit((int)_timeout.TotalMilliseconds))
            {
                // Never leave a stray adb.exe running / block the caller
                // longer than the configured timeout.
                try { process.Kill(entireProcessTree: true); } catch { /* best-effort */ }
                return (false, 0, string.Empty, string.Empty,
                    $"adb {arguments} khong phan hoi trong {_timeout.TotalSeconds}s -- coi nhu that bai.");
            }

            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            return (true, process.ExitCode, stdout, stderr, null);
        }
        catch (Exception ex)
        {
            return (false, 0, string.Empty, string.Empty,
                $"loi khi goi adb {arguments}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Looks for adb.exe: PATH first, then the well-known default Android SDK
    /// platform-tools location. Returns null if neither exists -- callers must
    /// treat that as "cannot set up tunnels", never crash.
    /// NOTE: duplicated verbatim from <see cref="AdbDeviceChecker"/>'s private
    /// FindAdbExe (kept private there) -- copied here rather than shared to
    /// keep this a single-new-file change and avoid touching the already
    /// unit-tested AdbDeviceChecker. Keep the two in sync if the discovery
    /// logic ever changes.
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
