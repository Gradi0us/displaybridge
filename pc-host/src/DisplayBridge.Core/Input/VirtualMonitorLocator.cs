// VirtualMonitorLocator.cs — fix for the "touch on tablet -> cursor lands on
// laptop screen" bug (docs/TASK-v1-tablet-display-tracker.md session 10).
//
// Root cause (confirmed by reading code, not guessed): CursorInjector and
// TouchInjector both mapped the 0..65535 normalized wire coordinate to
// screen pixels using GetSystemMetrics(SM_CXSCREEN/SM_CYSCREEN) -- which
// ALWAYS returns the PRIMARY monitor's size, regardless of how many other
// monitors exist. Since SetCursorPos/InjectTouchInput both consume REAL
// pixel coordinates on the virtual desktop (not per-monitor-relative
// percentages -- confirmed via RESEARCH-v1 §4.3), that formula always
// produces a coordinate inside [0, primaryWidth) x [0, primaryHeight),
// which is always inside the PRIMARY monitor's rect (primary sits at
// virtual-desktop origin (0,0) by Windows convention) -- no matter where on
// the tablet the user actually touched.
//
// Fix: resolve the REAL virtual-desktop RECT of "VDD by MTT" via
// EnumDisplayMonitors + GetMonitorInfo + EnumDisplayDevices (matching the
// SAME hardware-id/device-string markers DesktopDuplicationCapture.cpp uses
// for video capture -- see that file's FindVirtualDisplayAdapterDeviceName
// -- so C++ (video) and C# (input) never disagree about which monitor is
// "the tablet"), then map normalized coordinates into THAT rect instead of
// GetSystemMetrics' primary-only size.
using System;
using System.Collections.Generic;

namespace DisplayBridge.Core.Input;

/// <summary>
/// Testable seam over "find the real virtual-desktop RECT of VDD by MTT".
/// The real implementation walks live Win32 monitor enumeration; unit tests
/// use a fake to assert the pixel-mapping math without a real desktop.
/// </summary>
public interface IVirtualMonitorLocator
{
    /// <summary>
    /// Real virtual-desktop RECT (Left/Top/Right/Bottom, actual pixel
    /// coordinates, NOT 0..65535 normalized) of "VDD by MTT". Falls back to
    /// the primary monitor's RECT (logging clearly, never silently) if the
    /// virtual display can't be found (driver disabled/uninstalled).
    /// </summary>
    Win32Interop.Rect GetVirtualDisplayRect();

    /// <summary>
    /// Drops the cached rect so the next <see cref="GetVirtualDisplayRect"/>
    /// call re-enumerates. Call after a topology-changing operation (e.g.
    /// VirtualDisplayConfigurator.EnsureExtendTopology() / driver restart)
    /// so stale coordinates from before the change aren't reused.
    /// </summary>
    void Invalidate();
}

/// <summary>
/// Finds the real virtual-desktop RECT of "VDD by MTT" using
/// EnumDisplayMonitors/GetMonitorInfo (per-monitor RECT + adapter device
/// name) plus EnumDisplayDevices (adapter device name -> attached monitor's
/// hardware DeviceID/DeviceString), matching the same markers used by
/// DesktopDuplicationCapture.cpp's FindVirtualDisplayAdapterDeviceName so
/// video-capture and input-injection never disagree on which monitor is the
/// tablet. Caches the result indefinitely (enumeration is comparatively
/// expensive and touch events arrive at tens-to-hundreds per second) --
/// call <see cref="Invalidate"/> when display topology may have changed.
/// </summary>
public sealed class VirtualMonitorLocator : IVirtualMonitorLocator
{
    // Same markers as DesktopDuplicationCapture.cpp::FindVirtualDisplayAdapterDeviceName
    // (DeviceID is hardware-based / locale-independent, DeviceString is the
    // localized friendly name -- kept as a fallback exactly like the C++ side).
    private static readonly string[] DeviceIdMarkers = { "MTT1337", "MttVDD" };
    private static readonly string[] DeviceStringMarkers = { "MTT", "VDD" };

    private readonly object _lock = new();
    private Win32Interop.Rect? _cachedRect;

    public Win32Interop.Rect GetVirtualDisplayRect()
    {
        lock (_lock)
        {
            if (_cachedRect.HasValue)
            {
                return _cachedRect.Value;
            }

            var rect = Locate(out var found);
            _cachedRect = rect;
            if (!found)
            {
                Console.Error.WriteLine(
                    "[VirtualMonitorLocator] khong tim thay 'VDD by MTT' -- fallback ve man chinh (primary). " +
                    "Neu day khong phai hanh vi mong muon, kiem tra driver VDD by MTT co dang bat khong.");
            }

            return rect;
        }
    }

    public void Invalidate()
    {
        lock (_lock)
        {
            _cachedRect = null;
        }
    }

    private static Win32Interop.Rect Locate(out bool found)
    {
        Win32Interop.Rect? primaryRect = null;

        foreach (var monitor in EnumerateMonitorInfos())
        {
            if ((monitor.DwFlags & Win32Interop.MonitorInfoFPrimary) != 0)
            {
                primaryRect = monitor.RcMonitor;
            }

            if (AdapterHasVirtualMonitor(monitor.SzDevice))
            {
                found = true;
                return monitor.RcMonitor;
            }
        }

        found = false;
        return primaryRect ?? default;
    }

    private static List<Win32Interop.MonitorInfoEx> EnumerateMonitorInfos()
    {
        var handles = new List<IntPtr>();

        bool Callback(IntPtr hMonitor, IntPtr hdcMonitor, ref Win32Interop.Rect lprcMonitor, IntPtr dwData)
        {
            handles.Add(hMonitor);
            return true; // keep enumerating
        }

        // Keep the delegate alive for the duration of the P/Invoke call (it
        // is passed as an unmanaged function pointer; letting it be GC'd
        // mid-enumeration would be undefined behavior).
        Win32Interop.MonitorEnumProc callback = Callback;
        Win32Interop.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);

        var infos = new List<Win32Interop.MonitorInfoEx>(handles.Count);
        foreach (var handle in handles)
        {
            var info = new Win32Interop.MonitorInfoEx
            {
                CbSize = System.Runtime.InteropServices.Marshal.SizeOf<Win32Interop.MonitorInfoEx>(),
            };
            if (Win32Interop.GetMonitorInfo(handle, ref info))
            {
                infos.Add(info);
            }
        }

        return infos;
    }

    /// <summary>
    /// Mirrors DesktopDuplicationCapture.cpp's inner loop: walk every
    /// monitor EnumDisplayDevicesW reports as attached to this adapter
    /// device name (e.g. "\\.\DISPLAY5"), and check DeviceID/DeviceString
    /// for the VDD by MTT markers.
    /// </summary>
    private static bool AdapterHasVirtualMonitor(string adapterDeviceName)
    {
        for (uint monitorIndex = 0; ; monitorIndex++)
        {
            var device = new Win32Interop.DisplayDevice
            {
                Cb = System.Runtime.InteropServices.Marshal.SizeOf<Win32Interop.DisplayDevice>(),
            };

            if (!Win32Interop.EnumDisplayDevices(adapterDeviceName, monitorIndex, ref device, 0))
            {
                break; // no more monitors on this adapter
            }

            if (ContainsAny(device.DeviceID, DeviceIdMarkers) || ContainsAny(device.DeviceString, DeviceStringMarkers))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsAny(string? haystack, string[] needles)
    {
        if (string.IsNullOrEmpty(haystack))
        {
            return false;
        }

        foreach (var needle in needles)
        {
            if (haystack.Contains(needle, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>Shared normalized(0..65535) -> real virtual-desktop-pixel mapping, used by both CursorInjector and TouchInjector so the formula lives in exactly one place.</summary>
public static class CoordinateMapper
{
    public static (int X, int Y) ToScreenPixels(ushort normalizedX, ushort normalizedY, Win32Interop.Rect rect)
    {
        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        var x = rect.Left + (int)(normalizedX / 65535.0 * width);
        var y = rect.Top + (int)(normalizedY / 65535.0 * height);
        return (x, y);
    }
}
