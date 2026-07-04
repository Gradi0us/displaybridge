// DriverManager.cs — Driver self-management task.
//
// Prior to this, getting "VDD by MTT" to the tablet's real native
// resolution required the user to: (1) install the driver by hand via the
// separate "Virtual Driver Control.exe" GUI tool if it wasn't already
// present, and (2) manually restart it via Device Manager every time
// VirtualDisplayConfigurator wrote a new resolution into vdd_settings.xml,
// because that restart needs Administrator privileges the unelevated Host
// process didn't have (see VirtualDisplayConfigurator.TryRestartDriver's
// header comment, session 7).
//
// Now that DisplayBridge.Host requests Administrator once at launch (see
// app.manifest) and bundles its own signed copy of the driver package
// (Resources/VirtualDisplayDriver/MttVDD.inf|.dll|mttvdd.cat) plus devcon.exe
// (Resources/devcon.exe), this class owns the full "make the driver ready"
// lifecycle so nothing manual is required anymore:
//   IsDriverInstalled -> InstallIfMissing -> (caller writes vdd_settings.xml
//   via VirtualDisplayConfigurator.ApplyResolution) -> RestartDevice.
//
// Deliberately does NOT reimplement device restart: VirtualDisplayConfigurator
// already has a verified-working TryRestartDriver() (devcon restart by
// hardware ID, with clear RestartFailed/DevconNotFound diagnostics) -- this
// class composes that instance rather than duplicating the devcon-restart
// shellout.
using System.Diagnostics;

namespace DisplayBridge.Core.Video;

/// <summary>Outcome of <see cref="DriverManager.InstallIfMissing"/>.</summary>
public enum DriverInstallResult
{
    /// <summary>Driver was already present -- pnputil was not invoked.</summary>
    AlreadyInstalled,

    /// <summary>pnputil ran and reported the driver+device were installed.</summary>
    InstalledNow,

    /// <summary>pnputil ran but did not report success (see the detail string from the caller).</summary>
    InstallFailed,

    /// <summary>Neither the bundled nor pnputil.exe could be located/run.</summary>
    PnputilNotFound,

    /// <summary>The bundled MttVDD.inf could not be located next to the running app.</summary>
    InfNotFound,
}

/// <summary>
/// Seam so StreamingCoordinator can be constructed with a fake in tests
/// (session 11) without shelling out to real devcon.exe/pnputil.exe --
/// mirrors the existing ICursorInjector/ITouchInjector injection pattern.
/// The real implementation is <see cref="DriverManager"/>.
/// </summary>
public interface IDriverManager
{
    (bool Success, string Message) EnsureReady(int nativeWidth, int nativeHeight, IReadOnlyList<int> supportedHz, Action<string>? onStep = null);

    /// <summary>
    /// Session 15 (no-ADB auto-disable task): disables the "VDD by MTT" PnP
    /// device node (devcon disable) so Windows stops reporting it as a
    /// connected display when no tablet is actually plugged in over ADB.
    /// Default no-op implementation (C# 8 default interface member) so
    /// pre-existing fakes (e.g. Integration.Tests' FakeDriverManager, which
    /// only cares about EnsureReady) keep compiling unchanged -- only the
    /// real <see cref="DriverManager"/> and any fake written specifically to
    /// test this feature need to override it.
    /// </summary>
    (bool Success, string Message) DisableDevice() => (false, "IDriverManager.DisableDevice() default no-op -- not overridden by this implementation.");

    /// <summary>Re-enables the device node after ADB reconnects. See <see cref="DisableDevice"/>.</summary>
    (bool Success, string Message) EnableDevice() => (false, "IDriverManager.EnableDevice() default no-op -- not overridden by this implementation.");
}

/// <summary>
/// Owns the full lifecycle of the "VDD by MTT" virtual display driver so
/// DisplayBridge.Host never requires the separate "Virtual Driver
/// Control.exe" tool or manual Device Manager steps. All shell-outs
/// (devcon.exe, pnputil.exe) are best-effort: failures are reported back via
/// return values/log strings, never thrown, so a driver problem degrades to
/// the existing JPEG/GDI fallback instead of crashing the host app.
/// </summary>
public sealed class DriverManager : IDriverManager
{
    private readonly VirtualDisplayConfigurator _configurator;
    private readonly string? _devconPath;
    private readonly string? _infPath;

    public DriverManager(VirtualDisplayConfigurator? configurator = null)
    {
        _configurator = configurator ?? new VirtualDisplayConfigurator();
        _devconPath = _configurator.DevconPath;
        _infPath = FindBundledInf();
    }

    /// <summary>
    /// True if "VDD by MTT" (hardware ID Root\MttVDD) is currently known to
    /// Windows PnP, regardless of enabled/disabled state. Shells out to the
    /// bundled devcon.exe ("devcon find *Root\MttVDD*") rather than adding a
    /// System.Management/WMI dependency -- devcon is already required for
    /// RestartDevice, so this reuses the exact same tool/parsing style
    /// instead of introducing a second detection mechanism.
    /// </summary>
    public bool IsDriverInstalled(out string detail)
    {
        if (_devconPath is null || !File.Exists(_devconPath))
        {
            detail = "devcon.exe khong tim thay -- khong the kiem tra driver da cai chua.";
            return false;
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = _devconPath,
                Arguments = $"find \"*{VirtualDisplayConfigurator.HardwareId}*\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            using var process = Process.Start(psi);
            if (process is null)
            {
                detail = "khong khoi chay duoc devcon.exe de kiem tra driver.";
                return false;
            }

            var stdout = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);

            // devcon find prints "N matching device(s) found." when present,
            // "No matching devices found." otherwise -- verified empirically
            // on this machine (see TASK-v1-tablet-display-tracker.md session 8).
            var found = stdout.Contains("matching device(s) found", StringComparison.OrdinalIgnoreCase)
                        && !stdout.Contains("No matching devices found", StringComparison.OrdinalIgnoreCase);
            detail = stdout.Trim();
            return found;
        }
        catch (Exception ex)
        {
            detail = $"loi khi goi devcon find: {ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// Installs the bundled MttVDD driver package via
    /// "pnputil /add-driver ... /install" if <see cref="IsDriverInstalled"/>
    /// reports it's not present yet. Requires Administrator (same as
    /// VirtualDisplayConfigurator.TryRestartDriver) -- app.manifest now
    /// requests that once at launch, so this should succeed in normal
    /// operation. Never throws: any failure is reported via the return value
    /// and <paramref name="detail"/> so StreamingCoordinator can log it and
    /// keep running in JPEG/GDI fallback mode instead of crashing.
    /// </summary>
    public DriverInstallResult InstallIfMissing(out string detail)
    {
        if (IsDriverInstalled(out var existingDetail))
        {
            detail = $"Driver da duoc cai san -- bo qua pnputil. ({existingDetail})";
            return DriverInstallResult.AlreadyInstalled;
        }

        if (_infPath is null || !File.Exists(_infPath))
        {
            detail = "Khong tim thay MttVDD.inf (Resources\\VirtualDisplayDriver\\MttVDD.inf) -- khong the tu cai driver.";
            return DriverInstallResult.InfNotFound;
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "pnputil.exe",
                Arguments = $"/add-driver \"{_infPath}\" /install",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            using var process = Process.Start(psi);
            if (process is null)
            {
                detail = "khong khoi chay duoc pnputil.exe.";
                return DriverInstallResult.PnputilNotFound;
            }

            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit(20000);
            detail = string.IsNullOrWhiteSpace(stderr) ? stdout.Trim() : $"{stdout.Trim()}\n{stderr.Trim()}";

            // pnputil exits 0 on success; also sanity-check the device shows
            // up afterwards rather than trusting the exit code alone (same
            // "don't trust one signal" discipline as TryRestartDriver's
            // stdout-substring check).
            if (process.ExitCode == 0 && IsDriverInstalled(out _))
            {
                return DriverInstallResult.InstalledNow;
            }

            return DriverInstallResult.InstallFailed;
        }
        catch (Exception ex)
        {
            detail = $"loi khi goi pnputil /add-driver: {ex.GetType().Name}: {ex.Message}";
            return DriverInstallResult.PnputilNotFound;
        }
    }

    /// <summary>
    /// Restarts the "VDD by MTT" PnP device node so it re-reads
    /// vdd_settings.xml. Thin wrapper over
    /// <see cref="VirtualDisplayConfigurator.TryRestartDriver"/> -- kept as a
    /// method here (rather than callers reaching into the configurator
    /// directly) so the whole driver lifecycle reads top-to-bottom from one
    /// class (<see cref="EnsureReady"/>).
    /// </summary>
    public VirtualDisplayRestartResult RestartDevice() => _configurator.TryRestartDriver();

    /// <summary>
    /// Session 15 (no-ADB auto-disable task): disables the "VDD by MTT" PnP
    /// device node via "devcon disable *Root\MttVDD*" so Windows stops
    /// showing it as a second monitor while no tablet is connected over ADB.
    /// Same devcon-by-hardware-ID pattern as RestartDevice/TryRestartDriver
    /// (reuses _devconPath resolved by VirtualDisplayConfigurator, no new
    /// devcon lookup). Requires Administrator, same as restart/install --
    /// app.manifest already grants that once at launch (session 8). Never
    /// throws: any failure is reported via the return tuple so the caller
    /// (StreamingCoordinator's ADB poll) can log it and keep running instead
    /// of crashing the host app.
    /// </summary>
    public (bool Success, string Message) DisableDevice() => RunDevconEnableDisable("disable");

    /// <summary>Re-enables the device node once ADB reconnects. See <see cref="DisableDevice"/>.</summary>
    public (bool Success, string Message) EnableDevice() => RunDevconEnableDisable("enable");

    private (bool Success, string Message) RunDevconEnableDisable(string verb)
    {
        if (_devconPath is null || !File.Exists(_devconPath))
        {
            return (false, "devcon.exe khong tim thay -- khong the disable/enable driver.");
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = _devconPath,
                // Same "restart *hwid*" wildcard-by-hardware-ID form already
                // verified working for TryRestartDriver -- devcon's
                // disable/enable subcommands accept the identical pattern.
                Arguments = $"{verb} *{VirtualDisplayConfigurator.HardwareId}*",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            using var process = Process.Start(psi);
            if (process is null)
            {
                return (false, $"khong khoi chay duoc devcon.exe de {verb} driver.");
            }

            var stdout = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);

            // devcon prints "N device(s) disabled."/"N device(s) enabled."
            // on success, or "Disable failed."/"Enable failed." (no admin,
            // or device already in that state on some devcon builds) --
            // same "don't trust exit code alone" discipline as
            // TryRestartDriver/IsDriverInstalled above.
            var succeeded = stdout.Contains($"{verb}d.", StringComparison.OrdinalIgnoreCase)
                             && !stdout.Contains($"{verb} failed", StringComparison.OrdinalIgnoreCase);
            return (succeeded, stdout.Trim());
        }
        catch (Exception ex)
        {
            return (false, $"loi khi goi devcon {verb}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Runs the full driver-readiness sequence for a newly-received CAPS
    /// message: ensure the driver is installed, write the device's native
    /// resolution into vdd_settings.xml, then restart the device node so it
    /// takes effect. Call this from StreamingCoordinator.OnCapsReceived
    /// instead of calling VirtualDisplayConfigurator.ApplyResolution/
    /// TryRestartDriver separately.
    /// </summary>
    /// <returns>
    /// (true, message) once the resolution has been written AND the device
    /// restarted successfully. (false, message) at any earlier step that
    /// didn't succeed -- message always explains which step and why, never
    /// just "failed".
    /// </returns>
    /// <param name="onStep">
    /// Verify RC2 fix (RCA-v1-resolution-stuck-800x600.md): optional callback
    /// invoked SYNCHRONOUSLY once per log line, as soon as each step
    /// completes (install check, ApplyResolution, RestartDevice) -- not just
    /// once at the very end via the joined return message. Each of these
    /// steps shells out to pnputil/devcon and can take several seconds, so
    /// without this a caller only tailing the returned string sees nothing
    /// until the whole sequence (up to ~25s worst case) has finished. Optional
    /// (default null) so existing callers/tests that only care about the
    /// final (Success, Message) tuple don't need to change.
    /// </param>
    public (bool Success, string Message) EnsureReady(int nativeWidth, int nativeHeight, IReadOnlyList<int> supportedHz, Action<string>? onStep = null)
    {
        var log = new List<string>();
        void Emit(string line)
        {
            log.Add(line);
            onStep?.Invoke(line);
        }

        if (!IsDriverInstalled(out var installedDetail))
        {
            Emit("[1/3] Driver chua duoc cai -- dang chay pnputil /add-driver...");
            var installResult = InstallIfMissing(out var installDetail);
            Emit($"[1/3] InstallIfMissing -> {installResult}: {installDetail}");
            if (installResult != DriverInstallResult.InstalledNow && installResult != DriverInstallResult.AlreadyInstalled)
            {
                return (false, string.Join(" | ", log));
            }
        }
        else
        {
            Emit($"[1/3] Driver da duoc cai san. ({installedDetail})");
        }

        var applied = _configurator.ApplyResolution(nativeWidth, nativeHeight, supportedHz, out var applyError);
        if (!applied)
        {
            Emit($"[2/3] ApplyResolution THAT BAI: {applyError}");
            return (false, string.Join(" | ", log));
        }
        Emit($"[2/3] Da ghi vdd_settings.xml resolution={nativeWidth}x{nativeHeight}.");

        Emit("[3/3] Dang restart driver (devcon restart)...");
        var restartResult = RestartDevice();
        Emit($"[3/3] RestartDevice -> {restartResult}");

        return (restartResult == VirtualDisplayRestartResult.Restarted, string.Join(" | ", log));
    }

    /// <summary>
    /// Looks for the bundled MttVDD.inf next to the running app
    /// (Resources\VirtualDisplayDriver\MttVDD.inf, copied there by
    /// DisplayBridge.Host.csproj). Returns null if not found -- callers must
    /// handle that (InfNotFound) without crashing.
    /// </summary>
    private static string? FindBundledInf()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Resources", "VirtualDisplayDriver", "MttVDD.inf");
        return File.Exists(path) ? path : null;
    }
}
