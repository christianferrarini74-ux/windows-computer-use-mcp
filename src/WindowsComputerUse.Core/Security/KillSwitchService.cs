using System.Runtime.InteropServices;
using WindowsComputerUse.Core.Dpi;

namespace WindowsComputerUse.Core.Security;

/// <summary>
/// On-demand global kill-switch triggered by pressing F10.
/// Lazily shows a compact top-center always-on-top overlay banner when Computer Use tools are invoked.
/// </summary>
public sealed class KillSwitchService : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WmKeyDown = 0x0100;
    private const int WmSysKeyDown = 0x0104;
    private const int VkF10 = 0x79;

    private const uint WmPaint = 0x000F;
    private const uint WmQuit = 0x0012;
    private const uint WmUserShutdownOverlay = 0x0401;
    private const int WmNcHitTest = 0x0084;
    private const int HtTransparent = -1;

    private const uint WsPopup = 0x80000000;
    private const uint WsVisible = 0x10000000;
    private const uint WsExTopMost = 0x00000008;
    private const uint WsExToolWindow = 0x00000080;
    private const uint WsExNoActivate = 0x08000000;
    private const uint WsExTransparent = 0x00000020;
    private const uint WsExLayered = 0x00080000;
    private const uint LwaColorKey = 0x00000001;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private const uint SwpAsyncWindowPos = 0x4000;
    private const int SwHide = 0;
    private const uint PointerTransparencyKey = 0x00FF00FF;
    private const int PsSolid = 0;
    private const int HollowBrush = 5;

    private const int SwShowNoActivate = 4;
    private const int TransparentBkMode = 1;
    private const uint DtCenter = 0x00000001;
    private const uint DtVCenter = 0x00000004;
    private const uint DtSingleLine = 0x00000020;

    private readonly Action? _onKilled;
    private readonly object _lock = new();
    private Thread? _thread;
    private ManualResetEventSlim? _ready;
    private uint _threadId;
    private IntPtr _overlayHwnd;
    private IntPtr _pointerHwnd;
    private IntPtr _hookHandle;
    private LowLevelKeyboardProc? _hookDelegate;
    private WndProcDelegate? _wndProcDelegate;
    private WndProcDelegate? _pointerWndProcDelegate;

    private volatile bool _isActive;
    private volatile bool _isTripped;
    private CancellationTokenSource _cts = new();
    private bool _disposed;

    public KillSwitchService(Action? onKilled = null)
    {
        _onKilled = onKilled;
    }

    public bool IsActive => _isActive;
    public bool IsTripped => _isTripped;
    public string HotkeyDescription => "F10";
    public bool IsHotkeyRegistered => _hookHandle != IntPtr.Zero;

    public CancellationToken Token
    {
        get
        {
            lock (_lock)
            {
                return _cts.Token;
            }
        }
    }

    /// <summary>
    /// Lazily starts the F10 keyboard hook, the banner, and the mouse pointer overlay.
    /// </summary>
    public void EnsureActive()
    {
        ThrowIfTripped();
        bool startedNow = false;

        lock (_lock)
        {
            if (_disposed || _isActive)
                return;

            _isActive = true;
            startedNow = true;
            _ready = new ManualResetEventSlim(false);
            _thread = new Thread(UiMessageLoop)
            {
                IsBackground = true,
                Name = "WCU-KillSwitch-Overlay"
            };
            _thread.Start();
        }

        if (_ready?.Wait(TimeSpan.FromSeconds(2)) == true && startedNow && GetCursorPos(out NativePoint point))
            ShowMousePointerAt(point.X, point.Y);
    }

    public void Trip(string? reason = null)
    {
        bool invokeKillCallback = false;

        lock (_lock)
        {
            if (!_isTripped)
            {
                _isTripped = true;
                invokeKillCallback = true;
                if (!_cts.IsCancellationRequested)
                    _cts.Cancel();
            }
        }

        if (_overlayHwnd != IntPtr.Zero)
        {
            PostMessage(_overlayHwnd, WmUserShutdownOverlay, IntPtr.Zero, IntPtr.Zero);
        }
        else if (_threadId != 0)
        {
            PostThreadMessage(_threadId, WmQuit, IntPtr.Zero, IntPtr.Zero);
        }

        if (invokeKillCallback && _onKilled is not null)
        {
            _ = Task.Run(() =>
            {
                Thread.Sleep(50);
                _onKilled();
            });
        }
    }

    public void Reset()
    {
        lock (_lock)
        {
            _isTripped = false;
            _cts.Dispose();
            _cts = new CancellationTokenSource();
        }
    }

    public void ThrowIfTripped()
    {
        if (_isTripped)
            throw new KillSwitchTrippedException($"Emergency kill-switch ({HotkeyDescription}) triggered. Computer Use stopped.");
    }

    private void UiMessageLoop()
    {
        _threadId = GetCurrentThreadId();
        IntPtr hInstance = GetModuleHandle(null);

        _overlayHwnd = CreateOverlayWindow(hInstance);
        _pointerHwnd = CreatePointerWindow(hInstance);

        _hookDelegate = HookCallback;
        _hookHandle = SetWindowsHookEx(WhKeyboardLl, _hookDelegate, hInstance, 0);

        _ready?.Set();

        try
        {
            while (GetMessage(out NativeMsg msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
        }
        finally
        {
            if (_hookHandle != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_hookHandle);
                _hookHandle = IntPtr.Zero;
            }

            if (_overlayHwnd != IntPtr.Zero)
            {
                DestroyWindow(_overlayHwnd);
                _overlayHwnd = IntPtr.Zero;
            }

            if (_pointerHwnd != IntPtr.Zero)
            {
                DestroyWindow(_pointerHwnd);
                _pointerHwnd = IntPtr.Zero;
            }

            _isActive = false;
        }
    }

    private IntPtr CreateOverlayWindow(IntPtr hInstance)
    {
        _wndProcDelegate = OverlayWndProc;
        string className = $"WcuKillSwitchBanner_{Environment.ProcessId}_{Environment.TickCount64}";

        var wc = new WndClassEx
        {
            cbSize = Marshal.SizeOf<WndClassEx>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate),
            hInstance = hInstance,
            lpszClassName = className
        };

        RegisterClassEx(ref wc);

        var monitors = DpiAwareness.GetMonitors();
        var primary = monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors.FirstOrDefault();
        double scale = primary?.ScaleFactor ?? 1.0;

        int width = (int)Math.Round(330 * scale);
        int height = (int)Math.Round(32 * scale);

        RectI bounds = primary?.Bounds ?? DpiAwareness.GetVirtualScreenBounds();
        int x = bounds.X + (bounds.Width - width) / 2;
        int y = bounds.Y + (int)Math.Round(10 * scale);

        IntPtr hwnd = CreateWindowEx(
            WsExTopMost | WsExToolWindow | WsExNoActivate,
            className,
            "Computer Use",
            WsPopup | WsVisible,
            x, y, width, height,
            IntPtr.Zero,
            IntPtr.Zero,
            hInstance,
            IntPtr.Zero);

        if (hwnd != IntPtr.Zero)
        {
            ShowWindow(hwnd, SwShowNoActivate);
            UpdateWindow(hwnd);
        }

        return hwnd;
    }

    private IntPtr CreatePointerWindow(IntPtr hInstance)
    {
        _pointerWndProcDelegate = PointerWndProc;
        string className = $"WcuMousePointer_{Environment.ProcessId}_{Environment.TickCount64}";
        var wc = new WndClassEx
        {
            cbSize = Marshal.SizeOf<WndClassEx>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_pointerWndProcDelegate),
            hInstance = hInstance,
            lpszClassName = className
        };
        RegisterClassEx(ref wc);

        IntPtr hwnd = CreateWindowEx(
            WsExTopMost | WsExToolWindow | WsExNoActivate | WsExTransparent | WsExLayered,
            className,
            "Computer Use mouse pointer",
            WsPopup,
            0, 0, 44, 44,
            IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);

        if (hwnd != IntPtr.Zero)
        {
            SetLayeredWindowAttributes(hwnd, PointerTransparencyKey, 0, LwaColorKey);
            ShowWindow(hwnd, SwHide);
        }

        return hwnd;
    }

    /// <summary>Shows a persistent colored, click-through halo at the agent-controlled mouse position.</summary>
    public void ShowMousePointerAt(int screenX, int screenY)
    {
        IntPtr hwnd = _pointerHwnd;
        if (hwnd == IntPtr.Zero || _isTripped || _disposed)
            return;

        SetWindowPos(hwnd, IntPtr.Zero, screenX - 22, screenY - 22, 44, 44,
            SwpNoActivate | SwpShowWindow | SwpAsyncWindowPos);
        InvalidateRect(hwnd, IntPtr.Zero, true);
    }

    private IntPtr OverlayWndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WmUserShutdownOverlay:
                DestroyWindow(hwnd);
                _overlayHwnd = IntPtr.Zero;
                PostQuitMessage(0);
                return IntPtr.Zero;

            case WmPaint:
                PaintBanner(hwnd);
                return IntPtr.Zero;
        }

        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private static void PaintBanner(IntPtr hwnd)
    {
        IntPtr hdc = BeginPaint(hwnd, out PaintStruct ps);
        try
        {
            GetClientRect(hwnd, out NativeRect rect);

            // BGR color format for Win32 COLORREF
            uint bgColor = 0x0028201Eu;
            uint borderColor = 0x00F6823Bu;
            uint textColor = 0x00FFFFFFu;

            IntPtr bgBrush = CreateSolidBrush(bgColor);
            IntPtr borderBrush = CreateSolidBrush(borderColor);
            try
            {
                FillRect(hdc, ref rect, bgBrush);
                FrameRect(hdc, ref rect, borderBrush);
            }
            finally
            {
                DeleteObject(bgBrush);
                DeleteObject(borderBrush);
            }

            int height = rect.Bottom - rect.Top;
            int fontSize = -(int)Math.Round(height * 0.44);
            IntPtr font = CreateFont(
                fontSize, 0, 0, 0, 600, 0, 0, 0,
                1, 0, 0, 5, 0, "Segoe UI");

            IntPtr oldFont = font != IntPtr.Zero ? SelectObject(hdc, font) : IntPtr.Zero;
            try
            {
                SetBkMode(hdc, TransparentBkMode);
                SetTextColor(hdc, textColor);

            const string text = "Computer Use — F10 to quit";
                DrawText(hdc, text, text.Length, ref rect, DtCenter | DtVCenter | DtSingleLine);
            }
            finally
            {
                if (oldFont != IntPtr.Zero)
                    SelectObject(hdc, oldFont);
                if (font != IntPtr.Zero)
                    DeleteObject(font);
            }
        }
        finally
        {
            EndPaint(hwnd, ref ps);
        }
    }

    private IntPtr PointerWndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WmNcHitTest)
            return (IntPtr)HtTransparent;

        if (msg == WmPaint)
        {
            PaintPointer(hwnd);
            return IntPtr.Zero;
        }

        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private static void PaintPointer(IntPtr hwnd)
    {
        IntPtr hdc = BeginPaint(hwnd, out PaintStruct ps);
        try
        {
            GetClientRect(hwnd, out NativeRect rect);
            IntPtr background = CreateSolidBrush(PointerTransparencyKey);
            IntPtr pen = CreatePen(PsSolid, 4, 0x00FFFF00u);
            IntPtr innerPen = CreatePen(PsSolid, 2, 0x00FFFFFFu);
            IntPtr hollow = GetStockObject(HollowBrush);
            try
            {
                FillRect(hdc, ref rect, background);
                IntPtr oldPen = SelectObject(hdc, pen);
                IntPtr oldBrush = SelectObject(hdc, hollow);
                Ellipse(hdc, 4, 4, 40, 40);
                SelectObject(hdc, innerPen);
                Ellipse(hdc, 14, 14, 30, 30);
                SelectObject(hdc, oldPen);
                SelectObject(hdc, oldBrush);
            }
            finally
            {
                DeleteObject(background);
                DeleteObject(pen);
                DeleteObject(innerPen);
            }
        }
        finally
        {
            EndPaint(hwnd, ref ps);
        }
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && (wParam == (IntPtr)WmKeyDown || wParam == (IntPtr)WmSysKeyDown))
        {
            int vkCode = Marshal.ReadInt32(lParam);
            if (vkCode == VkF10)
            {
                Trip("F10 pressed");
            }
        }

        return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        if (_overlayHwnd != IntPtr.Zero)
        {
            PostMessage(_overlayHwnd, WmUserShutdownOverlay, IntPtr.Zero, IntPtr.Zero);
        }
        else if (_threadId != 0)
        {
            PostThreadMessage(_threadId, WmQuit, IntPtr.Zero, IntPtr.Zero);
        }

        _thread?.Join(TimeSpan.FromSeconds(1));

        lock (_lock)
        {
            _cts.Dispose();
        }
        _ready?.Dispose();
    }

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);
    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out NativeMsg lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref NativeMsg lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref NativeMsg lpMsg);

    [DllImport("user32.dll")]
    private static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int nExitCode);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassEx(ref WndClassEx lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool UpdateWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool InvalidateRect(IntPtr hWnd, IntPtr lpRect, bool bErase);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint lpPoint);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr BeginPaint(IntPtr hwnd, out PaintStruct lpPaint);

    [DllImport("user32.dll")]
    private static extern bool EndPaint(IntPtr hWnd, ref PaintStruct lpPaint);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hWnd, out NativeRect lpRect);

    [DllImport("user32.dll")]
    private static extern int FillRect(IntPtr hDC, ref NativeRect lprc, IntPtr hbr);

    [DllImport("user32.dll")]
    private static extern int FrameRect(IntPtr hDC, ref NativeRect lprc, IntPtr hbr);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int DrawText(IntPtr hdc, string lpchText, int cchText, ref NativeRect lprc, uint format);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateSolidBrush(uint crColor);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreatePen(int fnPenStyle, int nWidth, uint crColor);

    [DllImport("gdi32.dll")]
    private static extern IntPtr GetStockObject(int i); 

    [DllImport("gdi32.dll")]
    private static extern bool Ellipse(IntPtr hdc, int left, int top, int right, int bottom);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFont(
        int cHeight, int cWidth, int cEscapement, int cOrientation, int cWeight,
        uint bItalic, uint bUnderline, uint bStrikeOut, uint iCharSet,
        uint iOutPrecision, uint iClipPrecision, uint iQuality, uint iPitchAndFamily, string pszFaceName);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);

    [DllImport("gdi32.dll")]
    private static extern int SetBkMode(IntPtr hdc, int mode);

    [DllImport("gdi32.dll")]
    private static extern uint SetTextColor(IntPtr hdc, uint color);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMsg
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

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

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassEx
    {
        public int cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PaintStruct
    {
        public IntPtr hdc;
        public bool fErase;
        public NativeRect rcPaint;
        public bool fRestore;
        public bool fIncUpdate;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public byte[] rgbReserved;
    }
}

public sealed class KillSwitchTrippedException(string message) : Exception(message);
