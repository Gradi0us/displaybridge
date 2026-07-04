using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using DisplayBridge.Core.Protocol.Generated;

namespace DisplayBridge.Core.Input;

/// <summary>Testable seam over InjectTouchInput; see ICursorInjector for why this exists.</summary>
public interface ITouchInjector
{
    void InitializeIfNeeded(uint maxPointCount = 10);
    void Inject(IReadOnlyList<TouchEventMessage> contacts);
}

/// <summary>
/// Multi-point Touch route: injects raw contact-level touch input via
/// InjectTouchInput. Per RESEARCH-v1 Finding #2-3, Windows' own Tầng 2/3
/// (WM_POINTER/WM_GESTURE) turns this into pinch/zoom/rotate/edge-swipe for
/// free — this class only has to get the contact list right (position,
/// pointer id stability, down/move/up flags).
/// </summary>
public sealed class TouchInjector : ITouchInjector
{
    private bool _initialized;
    private readonly IVirtualMonitorLocator _monitorLocator;

    public TouchInjector(IVirtualMonitorLocator? monitorLocator = null)
    {
        _monitorLocator = monitorLocator ?? new VirtualMonitorLocator();
    }

    public void InitializeIfNeeded(uint maxPointCount = 10)
    {
        if (_initialized) return;
        _initialized = Win32Interop.InitializeTouchInjection(maxPointCount, Win32Interop.TouchFeedback.Indirect);
    }

    private static Win32Interop.PointerFlags FlagsFor(byte action) => (TouchAction)action switch
    {
        TouchAction.Down => Win32Interop.PointerFlags.Down | Win32Interop.PointerFlags.InContact | Win32Interop.PointerFlags.InRange,
        TouchAction.Move => Win32Interop.PointerFlags.Update | Win32Interop.PointerFlags.InContact | Win32Interop.PointerFlags.InRange,
        TouchAction.Up => Win32Interop.PointerFlags.Up,
        TouchAction.Cancel => Win32Interop.PointerFlags.Up | Win32Interop.PointerFlags.Canceled,
        _ => Win32Interop.PointerFlags.Update,
    };

    public void Inject(IReadOnlyList<TouchEventMessage> contacts)
    {
        if (contacts.Count == 0) return;
        InitializeIfNeeded();

        var rect = _monitorLocator.GetVirtualDisplayRect();

        var infos = contacts.Select(c =>
        {
            var (x, y) = CoordinateMapper.ToScreenPixels(c.X, c.Y, rect);
            return new Win32Interop.PointerTouchInfo
            {
                PointerInfo = new Win32Interop.PointerInfo
                {
                    PointerType = Win32Interop.PointerInputType.Touch,
                    PointerId = c.PointerId,
                    PtPixelLocationX = x,
                    PtPixelLocationY = y,
                    PointerFlags = FlagsFor(c.Action),
                },
                TouchFlags = Win32Interop.TouchFlags.None,
                TouchMask = Win32Interop.TouchMask.Pressure,
                Pressure = c.Pressure,
                Orientation = 0,
                ContactArea = new Win32Interop.Rect { Left = x - 5, Top = y - 5, Right = x + 5, Bottom = y + 5 },
            };
        }).ToArray();

        Win32Interop.InjectTouchInput((uint)infos.Length, infos);
    }
}
