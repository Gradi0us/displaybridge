using System;

namespace DisplayBridge.Core.Input;

/// <summary>
/// Testable seam over "move the real cursor / click / drag". The real
/// implementation (<see cref="CursorInjector"/>) calls Win32Interop
/// (SendInput/SetCursorPos); unit tests use a fake to assert the
/// tap-vs-drag decision without touching a real desktop.
/// </summary>
public interface ICursorInjector
{
    void MoveTo(ushort normalizedX, ushort normalizedY);
    void ButtonDown();
    void ButtonUp();
}

/// <summary>
/// Single-finger Cursor route: moves the real Windows cursor and
/// left-clicks (short tap) or drags (hold past the threshold), per
/// RESEARCH-v1 §4.2/§"Cập nhật thiết kế". Hold-time classification lives
/// here rather than in InputModeClassifier, since it only matters once a
/// sample has already been routed to Cursor mode.
/// </summary>
public sealed class CursorInjector : ICursorInjector
{
    /// <summary>Default hold-before-drag threshold per CONTEXT-BRIEF §4.2 (150ms).</summary>
    public static readonly TimeSpan DefaultTapHoldThreshold = TimeSpan.FromMilliseconds(150);

    private readonly TimeSpan _tapHoldThreshold;
    private readonly IVirtualMonitorLocator _monitorLocator;

    public CursorInjector(TimeSpan? tapHoldThreshold = null, IVirtualMonitorLocator? monitorLocator = null)
    {
        _tapHoldThreshold = tapHoldThreshold ?? DefaultTapHoldThreshold;
        _monitorLocator = monitorLocator ?? new VirtualMonitorLocator();
    }

    public TimeSpan TapHoldThreshold => _tapHoldThreshold;

    public void MoveTo(ushort normalizedX, ushort normalizedY)
    {
        var rect = _monitorLocator.GetVirtualDisplayRect();
        var (x, y) = CoordinateMapper.ToScreenPixels(normalizedX, normalizedY, rect);
        Win32Interop.SetCursorPos(x, y);
    }

    public void ButtonDown()
    {
        var input = new Win32Interop.Input
        {
            Type = Win32Interop.InputMouse,
            Mi = new Win32Interop.MouseInput { DwFlags = (uint)Win32Interop.MouseEventFlags.LeftDown },
        };
        Win32Interop.SendInput(1, new[] { input }, System.Runtime.InteropServices.Marshal.SizeOf<Win32Interop.Input>());
    }

    public void ButtonUp()
    {
        var input = new Win32Interop.Input
        {
            Type = Win32Interop.InputMouse,
            Mi = new Win32Interop.MouseInput { DwFlags = (uint)Win32Interop.MouseEventFlags.LeftUp },
        };
        Win32Interop.SendInput(1, new[] { input }, System.Runtime.InteropServices.Marshal.SizeOf<Win32Interop.Input>());
    }
}
