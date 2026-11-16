using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using WindowsComputerUse.Core.Dpi;

namespace WindowsComputerUse.Core.Win32;

/// <summary>
/// Enumerates, inspects, and focuses top-level Windows windows, including UIPI elevation checks.
/// </summary>
public static class WindowService
{
    private const int DwmwaExtendedFrameBounds = 9;
    private const int DwmwaCloaked = 14;
    private const uint GaRoot = 2;
    private const int SwRestore = 9;
    private const int SwShow = 5;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;
    private const int TokenElevationClass = 20;

    private static readonly Lazy<bool> SelfElevated = new(() => CheckCurrentProcessElevated());

    public static bool IsCurrentProcessElevated => SelfElevated.Value;

    /// <summary>
    /// Lists visible, non-cloaked top-level windows with physical bounds and elevation metadata.
    /// </summary>
    public static IReadOnlyList<WindowInfo> ListWindows(
        bool includeMinimized = false,
        string? processFilter = null,
        string? titleFilter = null)
    {
        var results = new List<WindowInfo>();
        IntPtr foregroundHwnd = GetForegroundWindow();

        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd))
                return true;

            if (IsWindowCloaked(hwnd))
                return true;

            bool isMinimized = IsIconic(hwnd);
            if (isMinimized && !includeMinimized)
                return true;

            string title = GetWindowTitle(hwnd);
            if (string.IsNullOrWhiteSpace(title))
                return true;

            RectI bounds = GetWindowBounds(hwnd);
            if (!isMinimized && (bounds.Width <= 0 || bounds.Height <= 0))
                return true;

            GetWindowThreadProcessId(hwnd, out uint pid);
            string processName = GetProcessName((int)pid);

            if (!string.IsNullOrWhiteSpace(processFilter) &&
                !processName.Contains(processFilter, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (!string.IsNullOrWhiteSpace(titleFilter) &&
                !title.Contains(titleFilter, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var state = isMinimized
                ? WindowVisualState.Minimized
                : IsZoomed(hwnd)
                    ? WindowVisualState.Maximized
                    : WindowVisualState.Normal;

            results.Add(new WindowInfo(
                Hwnd: hwnd.ToInt64(),
                Pid: (int)pid,
                ProcessName: processName,
                Title: title,
                ClassName: GetClassName(hwnd),
                State: state,
                IsElevated: IsProcessElevated((int)pid),
                IsForeground: hwnd == foregroundHwnd,
                BoundingRect: bounds));

            return true;
        }, IntPtr.Zero);

        return results;
    }

    /// <summary>
    /// Returns metadata for a single window handle, or null if invalid.
    /// </summary>
    public static WindowInfo? GetWindowInfo(long hwndValue)
    {
        var hwnd = new IntPtr(hwndValue);
        if (!IsWindow(hwnd))
            return null;

        _ = GetWindowThreadProcessId(hwnd, out uint pid);
        bool isMinimized = IsIconic(hwnd);
        var state = isMinimized
            ? WindowVisualState.Minimized
            : IsZoomed(hwnd)
                ? WindowVisualState.Maximized
                : WindowVisualState.Normal;

        return new WindowInfo(
            Hwnd: hwndValue,
            Pid: (int)pid,
            ProcessName: GetProcessName((int)pid),
            Title: GetWindowTitle(hwnd),
            ClassName: GetClassName(hwnd),
            State: state,
            IsElevated: IsProcessElevated((int)pid),
            IsForeground: hwnd == GetForegroundWindow(),
            BoundingRect: GetWindowBounds(hwnd));
    }

    /// <summary>
    /// Returns the top-level root window currently under the specified physical screen point.
    /// </summary>
    public static WindowInfo? GetWindowAtPoint(int screenX, int screenY)
    {
        IntPtr hwnd = WindowFromPoint(new NativePoint { X = screenX, Y = screenY });
        if (hwnd == IntPtr.Zero)
            return null;

        IntPtr root = GetAncestor(hwnd, GaRoot);
        if (root != IntPtr.Zero)
            hwnd = root;

        return GetWindowInfo(hwnd.ToInt64());
    }

    /// <summary>
    /// Returns the current foreground top-level window, if any.
    /// </summary>
    public static WindowInfo? GetForegroundWindowInfo()
    {
        IntPtr hwnd = GetForegroundWindow();
        return hwnd == IntPtr.Zero ? null : GetWindowInfo(hwnd.ToInt64());
    }

    /// <summary>
    /// Restores (if minimized) and brings the specified window to the foreground.
    /// </summary>
    public static bool FocusWindow(long hwndValue, bool restoreIfMinimized = true)
    {
        var hwnd = new IntPtr(hwndValue);
        if (!IsWindow(hwnd))
            return false;

        if (restoreIfMinimized && IsIconic(hwnd))
        {
            ShowWindow(hwnd, SwRestore);
        }
        else if (!IsWindowVisible(hwnd))
        {
            ShowWindow(hwnd, SwShow);
        }

        if (SetForegroundWindow(hwnd))
            return true;

        // AttachThreadInput fallback when Windows foreground lock prevents direct switch.
        IntPtr fg = GetForegroundWindow();
        uint fgThread = fg != IntPtr.Zero ? GetWindowThreadProcessId(fg, out _) : 0;
        uint currentThread = GetCurrentThreadId();
        uint targetThread = GetWindowThreadProcessId(hwnd, out _);

        bool attachedFg = false;
        bool attachedTarget = false;
        try
        {
            if (fgThread != 0 && fgThread != currentThread)
                attachedFg = AttachThreadInput(currentThread, fgThread, true);
            if (targetThread != 0 && targetThread != currentThread)
                attachedTarget = AttachThreadInput(currentThread, targetThread, true);

            BringWindowToTop(hwnd);
            return SetForegroundWindow(hwnd) || GetForegroundWindow() == hwnd;
        }
        finally
        {
            if (attachedTarget)
                AttachThreadInput(currentThread, targetThread, false);
            if (attachedFg)
                AttachThreadInput(currentThread, fgThread, false);
        }
    }

    /// <summary>
    /// Checks whether a target process is running elevated (administrator).
    /// Returns true if elevated or if access is denied due to higher integrity level.
    /// </summary>
    public static bool IsProcessElevated(int pid)
    {
        if (pid <= 0)
            return false;

        IntPtr hProcess = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (hProcess == IntPtr.Zero)
        {
            // ERROR_ACCESS_DENIED (5) on a higher-privilege process indicates elevation/PPL.
            return Marshal.GetLastWin32Error() == 5;
        }

        try
        {
            if (!OpenProcessToken(hProcess, TokenQuery, out IntPtr hToken))
                return Marshal.GetLastWin32Error() == 5;

            try
            {
                return IsTokenElevated(hToken);
            }
            finally
            {
                CloseHandle(hToken);
            }
        }
        finally
        {
            CloseHandle(hProcess);
        }
    }

    /// <summary>
    /// Returns true if the target window is elevated and the current MCP process is not (UIPI block).
    /// </summary>
    public static bool IsBlockedByUipi(WindowInfo target) =>
        target.IsElevated && !IsCurrentProcessElevated;

    /// <summary>
    /// Launches a desktop application or shell target in the interactive user session.
    /// If the MCP server is running elevated, launches via explorer.exe when possible to stay non-elevated.
    /// </summary>
    public static int? LaunchApp(string target, string? arguments = null, string? workingDirectory = null)
    {
        if (string.IsNullOrWhiteSpace(target))
            throw new ArgumentException("Target executable, file, or URI is required.", nameof(target));

        if (IsCurrentProcessElevated && string.IsNullOrWhiteSpace(arguments) && string.IsNullOrWhiteSpace(workingDirectory))
        {
            using var explorerProc = Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{target}\"",
                UseShellExecute = true
            });
            return explorerProc?.Id;
        }

        var psi = new ProcessStartInfo
        {
            FileName = target,
            Arguments = arguments ?? string.Empty,
            WorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory)
                ? (Path.IsPathRooted(target) && File.Exists(target) ? Path.GetDirectoryName(target) ?? string.Empty : string.Empty)
                : workingDirectory,
            UseShellExecute = true
        };

        using var proc = Process.Start(psi);
        return proc?.Id;
    }

    private static bool CheckCurrentProcessElevated()
    {
        if (!OpenProcessToken(Process.GetCurrentProcess().Handle, TokenQuery, out IntPtr hToken))
            return false;

        try
        {
            return IsTokenElevated(hToken);
        }
        finally
        {
            CloseHandle(hToken);
        }
    }

    private static bool IsTokenElevated(IntPtr hToken)
    {
        int size = Marshal.SizeOf<TokenElevation>();
        IntPtr ptr = Marshal.AllocHGlobal(size);
        try
        {
            if (GetTokenInformation(hToken, TokenElevationClass, ptr, size, out _))
            {
                var elevation = Marshal.PtrToStructure<TokenElevation>(ptr);
                return elevation.TokenIsElevated != 0;
            }
            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    private static RectI GetWindowBounds(IntPtr hwnd)
    {
        if (DwmGetWindowAttribute(hwnd, DwmwaExtendedFrameBounds, out NativeRect rect, Marshal.SizeOf<NativeRect>()) == 0)
        {
            return new RectI(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
        }

        if (GetWindowRect(hwnd, out rect))
        {
            return new RectI(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
        }

        return default;
    }

    private static bool IsWindowCloaked(IntPtr hwnd)
    {
        return DwmGetWindowAttribute(hwnd, DwmwaCloaked, out int cloaked, sizeof(int)) == 0 && cloaked != 0;
    }

    private static string GetWindowTitle(IntPtr hwnd)
    {
        int length = GetWindowTextLength(hwnd);
        if (length <= 0)
            return string.Empty;

        var sb = new StringBuilder(length + 1);
        _ = GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private static string GetClassName(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        int len = GetClassName(hwnd, sb, sb.Capacity);
        return len > 0 ? sb.ToString() : string.Empty;
    }

    private static string GetProcessName(int pid)
    {
        try
        {
            using var proc = Process.GetProcessById(pid);
            return proc.ProcessName + ".exe";
        }
        catch
        {
            return $"pid:{pid}";
        }
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsZoomed(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect lpRect);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(NativePoint point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out NativeRect pvAttribute, int cbAttribute);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr tokenHandle, int tokenInformationClass, IntPtr tokenInformation, int tokenInformationLength, out int returnLength);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenElevation
    {
        public int TokenIsElevated;
    }
}
