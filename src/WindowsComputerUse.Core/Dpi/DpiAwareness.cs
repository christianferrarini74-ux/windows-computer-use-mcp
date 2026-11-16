using System.Runtime.InteropServices;

namespace WindowsComputerUse.Core.Dpi;

/// <summary>
/// Manages Per-Monitor v2 DPI awareness and enumerates physical displays.
/// </summary>
public static partial class DpiAwareness
{
    private static readonly IntPtr DpiAwarenessContextPerMonitorAwareV2 = new(-4);
    private const int MdtEffectiveDpi = 0;

    private const int SmXVirtualScreen = 76;
    private const int SmYVirtualScreen = 77;
    private const int SmCxVirtualScreen = 78;
    private const int SmCyVirtualScreen = 79;

    private const uint MonitorInfoPrimary = 1;

    /// <summary>
    /// Ensures the current process is Per-Monitor v2 DPI aware.
    /// Safe to call multiple times or after an app.manifest already set awareness.
    /// </summary>
    public static bool EnsurePerMonitorV2()
    {
        return SetProcessDpiAwarenessContext(DpiAwarenessContextPerMonitorAwareV2);
    }

    /// <summary>
    /// Returns the bounding rectangle of the entire virtual desktop in physical pixels.
    /// </summary>
    public static RectI GetVirtualScreenBounds()
    {
        int x = GetSystemMetrics(SmXVirtualScreen);
        int y = GetSystemMetrics(SmYVirtualScreen);
        int w = GetSystemMetrics(SmCxVirtualScreen);
        int h = GetSystemMetrics(SmCyVirtualScreen);
        return new RectI(x, y, w, h);
    }

    /// <summary>
    /// Enumerates all active display monitors with physical bounds and effective DPI.
    /// </summary>
    public static IReadOnlyList<MonitorInfo> GetMonitors()
    {
        var monitors = new List<MonitorInfo>();
        int index = 0;

        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMonitor, IntPtr _, ref NativeRect _, IntPtr _) =>
        {
            var mi = new NativeMonitorInfoEx { cbSize = Marshal.SizeOf<NativeMonitorInfoEx>() };
            if (GetMonitorInfo(hMonitor, ref mi))
            {
                uint dpiX = 96;
                uint dpiY = 96;
                _ = GetDpiForMonitor(hMonitor, MdtEffectiveDpi, out dpiX, out dpiY);

                var bounds = new RectI(
                    mi.rcMonitor.Left,
                    mi.rcMonitor.Top,
                    mi.rcMonitor.Right - mi.rcMonitor.Left,
                    mi.rcMonitor.Bottom - mi.rcMonitor.Top);

                var work = new RectI(
                    mi.rcWork.Left,
                    mi.rcWork.Top,
                    mi.rcWork.Right - mi.rcWork.Left,
                    mi.rcWork.Bottom - mi.rcWork.Top);

                monitors.Add(new MonitorInfo(
                    index++,
                    mi.szDevice,
                    (mi.dwFlags & MonitorInfoPrimary) != 0,
                    bounds,
                    work,
                    dpiX,
                    dpiY));
            }
            return true;
        }, IntPtr.Zero);

        return monitors;
    }

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref NativeRect lprcMonitor, IntPtr dwData);

    [DllImport("user32.dll")]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref NativeMonitorInfoEx lpmi);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hMonitor, int dpiType, out uint dpiX, out uint dpiY);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeMonitorInfoEx
    {
        public int cbSize;
        public NativeRect rcMonitor;
        public NativeRect rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }
}
