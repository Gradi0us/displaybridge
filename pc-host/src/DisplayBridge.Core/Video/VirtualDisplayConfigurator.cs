// VirtualDisplayConfigurator.cs — RESEARCH-v2 fix, session 7.
//
// "Virtual Driver Control" (VDD by MTT, https://github.com/VirtualDrivers/
// Virtual-Display-Driver) reads its monitor/resolution list from a single
// XML file, %ProgramData-equivalent% C:\VirtualDisplayDriver\vdd_settings.xml
// (path confirmed by the user on this machine), on driver (re)start. The
// user manually seeded this file with ONE hardcoded resolution (HONOR
// ROD2-W09's native 3000x1920@120) as a stopgap; per the 2026-07-03 product
// decision there is no longer a resolution picker in the UI at all -- the
// ONLY correct resolution is always "100% of whatever device is connected
// right now", read from the real CAPS handshake (DeviceCaps), not a preset.
//
// This class rewrites ONLY the <resolutions> block of vdd_settings.xml to
// match the live DeviceCaps every time a CAPS message arrives (see
// StreamingCoordinator.OnCapsReceived), using System.Xml.Linq so every
// other section (gpu, global refresh list, options) is left byte-identical.
//
// Driver reload: VDD by MTT needs the ROOT\DISPLAY\0000 device node
// restarted (not a full Windows reboot) to pick up an edited
// vdd_settings.xml -- there is no evidence in this repo of a documented
// "hot reload" IOCTL, so the safest supported mechanism is the standard
// Windows device-restart path (disable+enable / devcon restart), same as
// changing driver INF settings for any other PnP device. "VDD Control.exe"
// (windows-driver/vdd-control/VDD Control.exe) is a WinForms GUI app with no
// documented CLI flags found (its own --help produced no output; no
// README/CLI docs shipped in windows-driver/vdd-control/) -- so we do NOT
// depend on it. Instead we shell out to devcon.exe (already vendored at
// windows-driver/vdd-control/Dependencies/devcon.exe) to restart the PnP
// device node by hardware ID.
//
// Admin limitation (IMPORTANT, do not bypass): restarting a PnP device node
// requires Administrator privileges. DisplayBridge.Host normally runs
// unelevated (no UAC manifest). devcon restart will fail with "Restart
// failed" under a non-elevated token (verified empirically on this machine:
// `devcon restart "@ROOT\DISPLAY\0000"` -> "Restart failed" while
// `IsInRole(Administrator)` == false in the same session). This class
// surfaces that failure via the return value / RestartResult and logs it
// clearly; it does NOT attempt to elevate itself (no UAC prompt, no
// runas, no manifest trickery) per the task's explicit constraint.
using System.Runtime.InteropServices;
using System.Xml.Linq;

namespace DisplayBridge.Core.Video;

/// <summary>Outcome of a driver-restart attempt, for logging/diagnostics.</summary>
public enum VirtualDisplayRestartResult
{
    /// <summary>devcon.exe ran and reported success.</summary>
    Restarted,

    /// <summary>devcon.exe could not be located at any known path.</summary>
    DevconNotFound,

    /// <summary>devcon.exe ran but failed -- most likely missing Administrator privileges.</summary>
    RestartFailed,

    /// <summary>The vdd_settings.xml file itself could not be found/written.</summary>
    SettingsFileNotFound,
}

/// <summary>
/// Rewrites VDD by MTT's vdd_settings.xml &lt;resolutions&gt; block to match a
/// live <see cref="DisplayBridge.Core.Settings.DeviceCaps"/> reading, and
/// attempts to restart the driver's PnP device node so the change takes
/// effect without a full Windows reboot.
/// </summary>
public sealed class VirtualDisplayConfigurator
{
    /// <summary>Default install path confirmed on the dev machine.</summary>
    public const string DefaultSettingsPath = @"C:\VirtualDisplayDriver\vdd_settings.xml";

    /// <summary>
    /// Hardware ID VDD by MTT registers under (Root\MttVDD, confirmed via
    /// `Get-PnpDevice -Class Display` -> HardwareID={Root\MttVDD}). Used to
    /// look up the live device instance ID before restarting, instead of
    /// hardcoding "ROOT\DISPLAY\0000" (that instance number is not
    /// guaranteed stable across reinstalls/other virtual devices).
    /// </summary>
    internal const string HardwareId = "Root\\MttVDD";

    // Windows CCD (Connecting and Configuring Displays) API — used to force
    // Extend topology so "VDD by MTT" gets its own \\.\DISPLAYn positioned
    // beside the laptop panel instead of Duplicate/Clone (sharing the same
    // \\.\DISPLAY1 as the primary, which is what made
    // DesktopDuplicationCapture end up capturing the primary's content even
    // though it correctly identified the VDD output by hardware ID).
    //
    // EMPIRICALLY VERIFIED on this machine (2026-07-03, unelevated process):
    // before call, GetSystemMetrics(SM_CMONITORS)=1 (VDD in Clone mode,
    // DesktopDuplicationCapture found MTT1337 monitor attached to \\.\DISPLAY1
    // at (0,0)-(2560,1600) == same as primary). After
    // SetDisplayConfig(0,null,0,null,SDC_TOPOLOGY_EXTEND|SDC_APPLY) returned
    // 0 (success) -- GetSystemMetrics(SM_CMONITORS) became 2, and
    // DesktopDuplicationCapture then found the VDD monitor on its OWN
    // \\.\DISPLAY5 at (2560,0)-(3360,600) (positioned to the RIGHT of the
    // primary -- genuine Extend, not Clone). No Administrator privileges
    // were required for this specific call. Resolution shown (800x600) is
    // VDD's current/default mode, not yet the tablet's native size --
    // that requires vdd_settings.xml + driver restart (ApplyResolution/
    // TryRestartDriver below), which DOES need Admin (documented there).
    private const uint SdcTopologyExtend = 0x00000004;
    private const uint SdcApply = 0x00000080;

    [DllImport("user32.dll")]
    private static extern int SetDisplayConfig(uint numPathArrayElements, IntPtr pathArray, uint numModeInfoArrayElements, IntPtr modeInfoArray, uint flags);

    /// <summary>
    /// Forces Windows into Extend display topology (equivalent to Settings
    /// &gt; Display &gt; "Extend these displays", or Win+P &gt; Extend).
    /// Idempotent — safe to call even if already extended. Returns true iff
    /// SetDisplayConfig reported success (return value 0). Does NOT require
    /// Administrator privileges (verified empirically). Must be called
    /// BEFORE DesktopDuplicationCapture::Init() enumerates DXGI outputs, or
    /// the virtual monitor may still be found attached to the primary's
    /// \\.\DISPLAYn (Clone mode) instead of getting its own.
    /// </summary>
    public bool EnsureExtendTopology()
    {
        var result = SetDisplayConfig(0, IntPtr.Zero, 0, IntPtr.Zero, SdcTopologyExtend | SdcApply);
        return result == 0;
    }

    private readonly string _settingsPath;
    private readonly string? _devconPath;

    /// <summary>Resolved devcon.exe path, exposed so <see cref="DriverManager"/> can reuse the same locator result instead of re-searching.</summary>
    public string? DevconPath => _devconPath;

    public VirtualDisplayConfigurator(string? settingsPath = null, string? devconPath = null)
    {
        _settingsPath = settingsPath ?? DefaultSettingsPath;
        _devconPath = devconPath ?? FindDevconExe();
    }

    /// <summary>
    /// Rewrites the &lt;resolutions&gt; block to a single entry
    /// (nativeWidth, nativeHeight, min(120, maxDeviceHz)) and leaves every
    /// other element (gpu/global/options and all XML comments) untouched.
    /// </summary>
    /// <returns>
    /// (true, null) on success. (false, reason) if the settings file doesn't
    /// exist or couldn't be parsed/written -- caller MUST log
    /// <paramref name="error"/> (not just the bool) so failures are
    /// diagnosable; a real bug was found this way during session 7
    /// verification (the shipped vdd_settings.xml had an XML comment
    /// containing "--", which is illegal in XML and made XDocument.Load
    /// throw XmlException on every call -- silently swallowing that
    /// exception would have looked identical to "file not found" and wasted
    /// debugging time). This method itself still never throws.
    /// </returns>
    public bool ApplyResolution(int nativeWidth, int nativeHeight, IReadOnlyList<int> supportedHz, out string? error)
    {
        error = null;

        if (nativeWidth <= 0 || nativeHeight <= 0)
        {
            error = $"Invalid resolution {nativeWidth}x{nativeHeight}.";
            return false;
        }

        if (!File.Exists(_settingsPath))
        {
            error = $"Settings file not found: {_settingsPath}";
            return false;
        }

        var refreshHz = ChooseRefreshHz(supportedHz);

        XDocument doc;
        try
        {
            doc = XDocument.Load(_settingsPath, LoadOptions.PreserveWhitespace);
        }
        catch (Exception ex)
        {
            error = $"Failed to parse {_settingsPath}: {ex.GetType().Name}: {ex.Message}";
            return false;
        }

        var resolutionsElement = doc.Root?.Element("resolutions");
        if (resolutionsElement is null)
        {
            error = $"{_settingsPath} has no <resolutions> element under <{doc.Root?.Name}>.";
            return false;
        }

        // Remove only the <resolution> child elements; leave any comment
        // nodes (e.g. the explanatory comment already in the file) in place
        // so the file stays self-documenting.
        resolutionsElement.Elements("resolution").Remove();

        resolutionsElement.Add(new XElement("resolution",
            new XElement("width", nativeWidth),
            new XElement("height", nativeHeight),
            new XElement("refresh_rate", refreshHz)));

        EnforceHardwareCursorDisabled(doc);

        try
        {
            doc.Save(_settingsPath);
        }
        catch (Exception ex)
        {
            error = $"Failed to save {_settingsPath}: {ex.GetType().Name}: {ex.Message}";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Cursor-disappears-crossing-monitors workaround (session 15): forces
    /// &lt;options&gt;&lt;HardwareCursor&gt; to "false" every time this class
    /// rewrites vdd_settings.xml, so it can never silently drift back to
    /// "true" (e.g. the user reinstalling the driver package, which ships
    /// with HardwareCursor=true by default) after we've already diagnosed
    /// hardware-cursor rendering as the root cause of the disappearing
    /// cursor bug (known IddCx limitation -- see the comment left directly
    /// in vdd_settings.xml and TASK-v1-tablet-display-tracker.md session
    /// 15). Leaves every other &lt;options&gt; child untouched. No-op (does
    /// not throw/fail ApplyResolution) if the file has no &lt;options&gt; or
    /// &lt;HardwareCursor&gt; element at all -- this is a best-effort
    /// workaround layered on top of the resolution-write, not something
    /// that should ever block the primary "apply resolution" contract.
    /// </summary>
    private static void EnforceHardwareCursorDisabled(XDocument doc)
    {
        var hardwareCursorElement = doc.Root?.Element("options")?.Element("HardwareCursor");
        if (hardwareCursorElement is not null)
        {
            hardwareCursorElement.Value = "false";
        }
    }

    /// <summary>
    /// FPS cap is hard-capped at 120 (2026-07-03 decision, 144Hz dropped).
    /// Picks the highest device-supported Hz that does not exceed 120,
    /// defaulting to 60 if the device's list is empty/unexpected.
    /// </summary>
    private static int ChooseRefreshHz(IReadOnlyList<int> supportedHz)
    {
        const int cap = 120;
        var best = 60;
        foreach (var hz in supportedHz)
        {
            if (hz <= cap && hz > best)
            {
                best = hz;
            }
        }
        return best;
    }

    /// <summary>
    /// Attempts to restart the VDD by MTT PnP device node so it re-reads
    /// vdd_settings.xml. Requires Administrator privileges -- if the
    /// current process isn't elevated this will reliably return
    /// <see cref="VirtualDisplayRestartResult.RestartFailed"/>. This method
    /// deliberately does NOT try to relaunch itself elevated / prompt UAC;
    /// it only reports the outcome so the caller can tell the user to
    /// restart the driver manually (e.g. via Device Manager) if needed.
    /// </summary>
    public VirtualDisplayRestartResult TryRestartDriver()
    {
        if (!File.Exists(_settingsPath))
        {
            return VirtualDisplayRestartResult.SettingsFileNotFound;
        }

        if (_devconPath is null || !File.Exists(_devconPath))
        {
            return VirtualDisplayRestartResult.DevconNotFound;
        }

        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = _devconPath,
                Arguments = $"restart \"=Display\" @{HardwareId}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            // devcon's restart-by-hwid form is `devcon restart <hwid-pattern>`
            // (no class filter needed since MttVDD's hardware ID is unique).
            psi.Arguments = $"restart *{HardwareId}*";

            using var process = System.Diagnostics.Process.Start(psi);
            if (process is null)
            {
                return VirtualDisplayRestartResult.RestartFailed;
            }

            var stdout = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);

            // devcon prints "Restart failed" (no admin) or "N matching
            // device(s) restarted." on success -- match on both to avoid
            // false positives from exit code alone (devcon's exit codes are
            // not consistently documented across builds).
            if (stdout.Contains("restarted", StringComparison.OrdinalIgnoreCase) &&
                !stdout.Contains("Restart failed", StringComparison.OrdinalIgnoreCase))
            {
                return VirtualDisplayRestartResult.Restarted;
            }

            return VirtualDisplayRestartResult.RestartFailed;
        }
        catch (Exception)
        {
            return VirtualDisplayRestartResult.RestartFailed;
        }
    }

    /// <summary>
    /// Looks for devcon.exe. Driver self-management task: DisplayBridge.Host
    /// now bundles its own copy at Resources\devcon.exe (see
    /// DisplayBridge.Host.csproj) so the shipped app doesn't depend on the
    /// dev repo's windows-driver/ folder existing next to it -- that path is
    /// checked FIRST. The old repo-relative search (walking up from the
    /// app's base directory looking for windows-driver/vdd-control/
    /// Dependencies/devcon.exe) is kept as a fallback purely for the dev
    /// environment (running via `dotnet run` from inside the repo before
    /// the Resources copy step existed). Returns null if neither is found --
    /// callers must handle that (DevconNotFound) without crashing.
    /// </summary>
    private static string? FindDevconExe()
    {
        var candidates = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, "Resources", "devcon.exe"),
        };

        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && dir is not null; i++)
        {
            candidates.Add(Path.Combine(dir, "windows-driver", "vdd-control", "Dependencies", "devcon.exe"));
            var parent = Directory.GetParent(dir);
            dir = parent?.FullName;
        }

        return candidates.FirstOrDefault(File.Exists);
    }
}
