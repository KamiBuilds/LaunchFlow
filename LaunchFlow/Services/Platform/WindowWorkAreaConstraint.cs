using System.Runtime.InteropServices;

namespace LaunchFlow.Services;

/// <summary>
/// Constrains WPF maximize to the monitor work area. Native MINMAXINFO is in
/// physical pixels; without this, a custom title bar can cover the taskbar on
/// secondary or DPI-scaled displays.
/// </summary>
public static class WindowWorkAreaConstraint
{
    private const uint MonitorDefaultToNearest = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public NativePoint Reserved;
        public NativePoint MaxSize;
        public NativePoint MaxPosition;
        public NativePoint MinTrackSize;
        public NativePoint MaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    public static void Apply(IntPtr hwnd, IntPtr minMaxInfo)
    {
        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero) return;
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info)) return;
        var values = Marshal.PtrToStructure<MinMaxInfo>(minMaxInfo);
        values.MaxPosition.X = info.Work.Left - info.Monitor.Left;
        values.MaxPosition.Y = info.Work.Top - info.Monitor.Top;
        values.MaxSize.X = info.Work.Right - info.Work.Left;
        values.MaxSize.Y = info.Work.Bottom - info.Work.Top;
        Marshal.StructureToPtr(values, minMaxInfo, false);
    }
}
