// Hand-written (not generated). P/Invoke declarations only — no C++ toolchain
// involved. Both the SendInput (cursor) and InjectTouchInput (touch) families
// are exposed by user32.dll and callable straight from C#; see
// docs/RESEARCH-v1-windows-touch-gesture-mechanism.md §4.3.
using System;
using System.Runtime.InteropServices;

namespace DisplayBridge.Core.Input;

/// <summary>
/// Raw Win32 P/Invoke surface used by <see cref="CursorInjector"/> and
/// <see cref="TouchInjector"/>. Kept as a thin, dumb layer — no business
/// logic here, only struct layouts + DllImport signatures so the rest of
/// the Input/ code can be unit-tested behind interfaces without touching
/// a real desktop.
/// </summary>
public static class Win32Interop
{
    // ---- SendInput (cursor / mouse) -----------------------------------

    public const int InputMouse = 0;

    [Flags]
    public enum MouseEventFlags : uint
    {
        Move = 0x0001,
        LeftDown = 0x0002,
        LeftUp = 0x0004,
        RightDown = 0x0008,
        RightUp = 0x0010,
        Absolute = 0x8000,
        VirtualDesk = 0x4000,
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MouseInput
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint DwFlags;
        public uint Time;
        public IntPtr DwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Input
    {
        public int Type;
        public MouseInput Mi;
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint SendInput(uint nInputs, Input[] pInputs, int cbSize);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetCursorPos(int x, int y);

    [StructLayout(LayoutKind.Sequential)]
    public struct Point
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetCursorPos(out Point lpPoint);

    [DllImport("user32.dll")]
    public static extern int GetSystemMetrics(int nIndex);

    public const int SmCxScreen = 0;
    public const int SmCyScreen = 1;

    // ---- InjectTouchInput / InitializeTouchInjection (touch) ----------

    public enum PointerInputType : uint
    {
        Touch = 2,
    }

    [Flags]
    public enum PointerFlags : uint
    {
        None = 0x00000000,
        New = 0x00000001,
        InRange = 0x00000002,
        InContact = 0x00000004,
        FirstButton = 0x00000010,
        Primary = 0x00002000,
        Confidence = 0x00004000,
        Canceled = 0x00008000,
        Down = 0x00010000,
        Update = 0x00020000,
        Up = 0x00040000,
    }

    [Flags]
    public enum TouchFlags : uint
    {
        None = 0x00000000,
    }

    [Flags]
    public enum TouchMask : uint
    {
        None = 0x00000000,
        ContactArea = 0x00000001,
        Orientation = 0x00000002,
        Pressure = 0x00000004,
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PointerInfo
    {
        public PointerInputType PointerType;
        public uint PointerId;
        public uint FrameId;
        public PointerFlags PointerFlags;
        public IntPtr SourceDevice;
        public IntPtr HwndTarget;
        public int PtPixelLocationX;
        public int PtPixelLocationY;
        public int PtHimetricLocationX;
        public int PtHimetricLocationY;
        public int PtPixelLocationRawX;
        public int PtPixelLocationRawY;
        public int PtHimetricLocationRawX;
        public int PtHimetricLocationRawY;
        public uint Time;
        public uint HistoryCount;
        public int InputData;
        public uint KeyStates;
        public ulong PerformanceCount;
        public int ButtonChangeType;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PointerTouchInfo
    {
        public PointerInfo PointerInfo;
        public TouchFlags TouchFlags;
        public TouchMask TouchMask;
        public Rect ContactArea;
        public Rect ContactAreaRaw;
        public uint Orientation;
        public uint Pressure;
    }

    public enum TouchFeedback : uint
    {
        Default = 0x1,
        Indirect = 0x2,
        None = 0x3,
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool InitializeTouchInjection(uint maxCount, TouchFeedback feedbackMode);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool InjectTouchInput(uint count, [In] PointerTouchInfo[] contacts);

    // ---- EnumDisplayMonitors / GetMonitorInfo / EnumDisplayDevices -----
    // Used by VirtualMonitorLocator to find the REAL virtual-desktop RECT of
    // "VDD by MTT" (SetCursorPos/InjectTouchInput both use real pixel
    // coordinates on the virtual desktop, NOT normalized-per-monitor
    // coordinates -- see docs/TASK-v1-tablet-display-tracker.md session 10).

    public const uint MonitorInfoFPrimary = 0x00000001;

    public delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref Rect lprcMonitor, IntPtr dwData);

    [DllImport("user32.dll")]
    public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MonitorInfoEx
    {
        public int CbSize;
        public Rect RcMonitor;
        public Rect RcWork;
        public uint DwFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string SzDevice;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMonitorInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfoEx lpmi);

    public const uint DisplayDeviceAttachedToDesktop = 0x00000001;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DisplayDevice
    {
        public int Cb;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceString;

        public uint StateFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceID;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceKey;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "EnumDisplayDevicesW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumDisplayDevices(string? lpDevice, uint iDevNum, ref DisplayDevice lpDisplayDevice, uint dwFlags);
}
