// StartupTaskManager.cs — session 19 feature: "App PC bổ sung bật ngay khi
// lúc mở máy" (auto-start at Windows logon).
//
// Why Task Scheduler and not an HKCU\...\Run key: this app's app.manifest
// requests requireAdministrator, and Run-key entries launch WITHOUT
// elevation -- Windows would either block the launch or throw a UAC prompt
// in the user's face at every logon. A scheduled task created with
// /RL HIGHEST runs the Host elevated silently at logon (the elevation
// consent happened once, when the already-elevated Host created the task).
//
// Same shell-out conventions as AdbDeviceChecker/AdbReverseManager: short
// timeout, never throw, Vietnamese-no-diacritics detail strings for logs.
using System;
using System.Diagnostics;

namespace DisplayBridge.Host;

public interface IStartupTaskManager
{
    bool IsEnabled();
    (bool Ok, string Detail) Enable();
    (bool Ok, string Detail) Disable();
}

public sealed class StartupTaskManager : IStartupTaskManager
{
    /// <summary>Task Scheduler task name -- stable so Query/Delete always target what Enable created.</summary>
    public const string TaskName = "DisplayBridgeHost";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    public bool IsEnabled()
    {
        var (ran, exitCode, _, _) = RunSchtasks($"/Query /TN \"{TaskName}\"");
        // schtasks /Query exits 0 when the task exists, 1 when it doesn't.
        return ran && exitCode == 0;
    }

    public (bool Ok, string Detail) Enable()
    {
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath))
        {
            return (false, "khong xac dinh duoc duong dan exe hien tai (Environment.ProcessPath null).");
        }

        // /F overwrites an existing task, making Enable idempotent and
        // self-healing when the exe path changed between versions.
        var (ran, exitCode, stdout, stderr) = RunSchtasks(
            $"/Create /TN \"{TaskName}\" /TR \"\\\"{exePath}\\\"\" /SC ONLOGON /RL HIGHEST /F");
        if (!ran)
        {
            return (false, stderr);
        }
        return exitCode == 0
            ? (true, $"da tao task '{TaskName}' (ONLOGON, highest) -> {exePath}")
            : (false, $"schtasks /Create loi (exit {exitCode}): {(string.IsNullOrWhiteSpace(stderr) ? stdout : stderr).Trim()}");
    }

    public (bool Ok, string Detail) Disable()
    {
        var (ran, exitCode, stdout, stderr) = RunSchtasks($"/Delete /TN \"{TaskName}\" /F");
        if (!ran)
        {
            return (false, stderr);
        }
        // Deleting a task that doesn't exist exits 1 -- treat as success
        // (desired end state reached either way).
        return exitCode == 0 || exitCode == 1
            ? (true, $"da xoa task '{TaskName}' (hoac task von khong ton tai).")
            : (false, $"schtasks /Delete loi (exit {exitCode}): {(string.IsNullOrWhiteSpace(stderr) ? stdout : stderr).Trim()}");
    }

    /// <summary>Never throws: (Ran=false, stderr=reason) covers start failure and timeout.</summary>
    private static (bool Ran, int ExitCode, string Stdout, string Stderr) RunSchtasks(string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var process = Process.Start(psi);
            if (process is null)
            {
                return (false, 0, string.Empty, "khong khoi chay duoc schtasks.exe.");
            }
            if (!process.WaitForExit((int)Timeout.TotalMilliseconds))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* best-effort */ }
                return (false, 0, string.Empty, $"schtasks khong phan hoi trong {Timeout.TotalSeconds}s.");
            }
            return (true, process.ExitCode, process.StandardOutput.ReadToEnd(), process.StandardError.ReadToEnd());
        }
        catch (Exception ex)
        {
            return (false, 0, string.Empty, $"loi khi goi schtasks: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
