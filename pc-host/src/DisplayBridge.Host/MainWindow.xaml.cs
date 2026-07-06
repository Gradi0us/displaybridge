using System.Windows;
using Application = System.Windows.Application;

namespace DisplayBridge.Host;

/// <summary>
/// Placeholder main window — M0.1 scaffold only. Real connection status
/// binding will be wired up by DisplayBridge.Core in a later milestone.
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Closing += MainWindow_Closing;
    }

    /// <summary>
    /// Background-run requirement: clicking the window's [X] must NOT kill
    /// the streaming Host (it would drop the tablet's connection). Instead
    /// hide the window -- App.xaml.cs's tray icon (NotifyIcon) keeps the
    /// process, and StreamingCoordinator, alive. The only way to actually
    /// terminate is the tray context menu's "Thoát", which calls
    /// Application.Shutdown() directly instead of closing this window.
    /// </summary>
    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // Session 19: a genuine quit (tray "Thoát" / the Exit button) sets
        // App.IsQuitting and terminates the process itself, so don't fight
        // it here. Only a plain user [X] (IsQuitting == false) gets
        // intercepted into minimize-to-tray.
        if ((Application.Current as App)?.IsQuitting == true) return;
        e.Cancel = true;
        Hide();
    }

    /// <summary>
    /// Session 19: fully quits (stops background run + disables the VDD),
    /// reachable straight from the window since the Win11 tray overflow hid
    /// the tray "Thoát". See App.QuitApplication.
    /// </summary>
    private void ExitButton_Click(object sender, RoutedEventArgs e)
    {
        (Application.Current as App)?.QuitApplication();
    }

    /// <summary>
    /// RCA fix (RC1, RCA-v1-resolution-stuck-800x600.md): previously used
    /// the object-initializer form <c>new SettingsWindow { Owner = this }</c>,
    /// which required the (now-removed) parameterless constructor and always
    /// showed DeviceCaps.Placeholder instead of the real connected device's
    /// resolution. Reads the live coordinator off App.Current (WPF's
    /// StartupUri creates this window before App.OnStartup's tray-menu code
    /// runs, but by the time a user actually clicks the button the
    /// coordinator is long since started).
    /// </summary>
    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var coordinator = (Application.Current as App)?.Coordinator;
        if (coordinator is null)
        {
            MessageBox.Show(this,
                "Chưa sẵn sàng: StreamingCoordinator chưa khởi động xong. Thử lại sau vài giây.",
                "DisplayBridge", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var settingsWindow = new SettingsWindow(coordinator.SettingsStore, coordinator.CurrentDeviceCaps) { Owner = this };
        settingsWindow.ShowDialog();
    }

    /// <summary>
    /// Called by App.xaml.cs once the first real video frame has flowed to a
    /// connected client (StreamingCoordinator.ClientConnected). Fixes the bug
    /// where this text stayed hardcoded to "chưa kết nối" forever even while
    /// a real session was live (reported by user with tablet screenshot).
    /// </summary>
    public void SetConnected()
    {
        StatusText.Text = "DisplayBridge Host — đã kết nối";
    }
}
