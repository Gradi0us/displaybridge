using System;
using System.Windows;
using System.Windows.Forms;
using Application = System.Windows.Application;

namespace DisplayBridge.Host;

/// <summary>
/// App entry point. Owns the system tray icon (NotifyIcon via WinForms interop)
/// for the lifetime of the process. M0.1 scaffold only — no ADB/socket logic here.
/// </summary>
public partial class App : Application
{
    private NotifyIcon? _trayIcon;
    private ToolStripMenuItem? _statusMenuItem;
    private StreamingCoordinator? _streamingCoordinator;

    /// <summary>
    /// Exposes the running coordinator so MainWindow (and any other window)
    /// can read CurrentDeviceCaps/SettingsStore instead of falling back to
    /// placeholders -- see RCA-v1-resolution-stuck-800x600.md RC1.
    /// </summary>
    public StreamingCoordinator? Coordinator => _streamingCoordinator;

    /// <summary>
    /// Session 19: true once a full quit is underway. MainWindow.Closing
    /// checks this so a genuine quit is allowed to close the window instead
    /// of being intercepted into the minimize-to-tray behavior.
    /// </summary>
    public bool IsQuitting { get; private set; }

    /// <summary>
    /// Session 19 (user report: tray icon hidden in the Win11 overflow, so
    /// the only reachable "quit" was Task Manager, and even then background
    /// work kept running). Fully shuts the app down from anywhere -- the
    /// tray "Thoát" and the new MainWindow "Thoát" button both call this:
    ///   1. hide the tray icon immediately (visible feedback that it's gone),
    ///   2. stop the background coordinator (ADB poll, sockets) AND disable
    ///      the VDD so no phantom monitor is left behind -- on a BOUNDED
    ///      worker so a slow/hung devcon can't wedge the quit,
    ///   3. Environment.Exit(0) to GUARANTEE the process and every background
    ///      thread actually terminate (the user should never need Task
    ///      Manager again).
    /// </summary>
    public void QuitApplication()
    {
        if (IsQuitting) return;
        IsQuitting = true;

        if (_trayIcon != null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
        }

        try
        {
            // Coordinator.Dispose() shells out to devcon to disable the
            // driver (~4s); bound it so a hung devcon can't block the quit.
            var cleanup = System.Threading.Tasks.Task.Run(() => _streamingCoordinator?.Dispose());
            cleanup.Wait(TimeSpan.FromSeconds(6));
        }
        catch
        {
            // best-effort: quitting must not be blockable by cleanup failure
        }

        Environment.Exit(0);
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Wiring-E2E (session 3): starts video+control sockets and wires
        // M1/M3/M4 together. See StreamingCoordinator.cs for the "why".
        // Falls back to stub capture automatically if the native DLL isn't
        // built yet (R13) -- never crashes app startup.
        _streamingCoordinator = new StreamingCoordinator();
        // Session 4: this is a WinExe (no console), so Debug.WriteLine alone
        // is invisible unless a debugger is attached. Also append to a
        // plain log file so `adb`-less manual verification ("did a real
        // frame flow?") has something greppable — see Việc 5 in the
        // session-4 task brief (needs real evidence, not just "it ran").
        var logPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "displaybridge-host.log");
        _streamingCoordinator.Log += msg =>
        {
            var line = $"[{DateTime.Now:HH:mm:ss.fff}] {msg}";
            System.Diagnostics.Debug.WriteLine($"[StreamingCoordinator] {msg}");
            try { System.IO.File.AppendAllText(logPath, line + Environment.NewLine); } catch { /* best-effort */ }
        };
        _streamingCoordinator.ClientConnected += () =>
        {
            // Fires on the socket accept-loop thread (not UI thread) -- must
            // marshal back via Dispatcher before touching WPF/WinForms controls.
            Dispatcher.Invoke(() =>
            {
                if (_statusMenuItem != null) _statusMenuItem.Text = "Trạng thái: Đã kết nối";
                if (MainWindow is MainWindow mw) mw.SetConnected();
            });
        };
        try
        {
            _streamingCoordinator.Start();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[StreamingCoordinator] failed to start: {ex.Message}");
            try { System.IO.File.AppendAllText(logPath, $"[{DateTime.Now:HH:mm:ss.fff}] FAILED TO START: {ex}" + Environment.NewLine); } catch { }
        }

        _statusMenuItem = new ToolStripMenuItem("Trạng thái: Chưa kết nối")
        {
            Enabled = false
        };

        // RCA fix (RC1, RCA-v1-resolution-stuck-800x600.md): must pass the
        // REAL connected device's caps + the coordinator's own SettingsStore
        // instead of the removed parameterless constructor's placeholder.
        // _streamingCoordinator is guaranteed non-null here (assigned above,
        // unconditionally, before this handler is registered).
        var coordinatorForSettings = _streamingCoordinator;
        var settingsMenuItem = new ToolStripMenuItem("Settings...");
        settingsMenuItem.Click += (_, _) =>
            new SettingsWindow(coordinatorForSettings!.SettingsStore, coordinatorForSettings.CurrentDeviceCaps).ShowDialog();

        var exitMenuItem = new ToolStripMenuItem("Thoát (tắt hẳn, dừng chạy nền)");
        exitMenuItem.Click += (_, _) => QuitApplication();

        var contextMenu = new ContextMenuStrip();
        contextMenu.Items.Add(_statusMenuItem);
        contextMenu.Items.Add(new ToolStripSeparator());
        contextMenu.Items.Add(settingsMenuItem);
        contextMenu.Items.Add(new ToolStripSeparator());
        contextMenu.Items.Add(exitMenuItem);

        // Icon packaging task: use the DisplayBridge logo for the tray icon
        // instead of the default system icon. Falls back to SystemIcons.Application
        // if Resources/logo.ico isn't found next to the executable (defensive,
        // should not happen once ApplicationIcon/CopyToOutputDirectory is set).
        var iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Resources", "logo.ico");
        var trayIcon = System.IO.File.Exists(iconPath)
            ? new System.Drawing.Icon(iconPath)
            : System.Drawing.SystemIcons.Application;

        _trayIcon = new NotifyIcon
        {
            Icon = trayIcon,
            Visible = true,
            Text = "DisplayBridge Host",
            ContextMenuStrip = contextMenu
        };

        // Background-run requirement: MainWindow.Closing hides instead of
        // closing, so the only way back to the window once minimized is
        // double-clicking the tray icon (standard Windows tray-app
        // convention) -- restores + brings to front rather than leaving
        // the user with no way to reopen Settings.
        _trayIcon.DoubleClick += (_, _) =>
        {
            if (MainWindow is null) return;
            MainWindow.Show();
            MainWindow.WindowState = WindowState.Normal;
            MainWindow.Activate();
        };
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_trayIcon != null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
        }

        _streamingCoordinator?.Dispose();

        base.OnExit(e);
    }
}
