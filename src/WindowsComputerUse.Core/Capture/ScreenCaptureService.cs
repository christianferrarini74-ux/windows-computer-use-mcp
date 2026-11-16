using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using WindowsComputerUse.Core.Dpi;
using WindowsComputerUse.Core.Win32;

namespace WindowsComputerUse.Core.Capture;

public enum CaptureTarget
{
    Screen,
    Monitor,
    Window,
    Region
}

public enum CaptureFormat
{
    Png,
    Jpeg
}

public sealed record ScreenshotResult(
    byte[] ImageBytes,
    string MimeType,
    CoordinateTransform Transform,
    IReadOnlyList<MonitorInfo> Monitors)
{
    public ScreenshotDiagnostics? Diagnostics { get; init; }
}

public sealed record ScreenshotDiagnostics(
    double TargetSetupMs,
    double ScreenCopyMs,
    double ResizeMs,
    double EncodeMs,
    double TotalMs,
    int SourceWidth,
    int SourceHeight,
    int ImageWidth,
    int ImageHeight,
    int ImageBytes);

/// <summary>
/// Captures DPI-aware screenshots of the virtual screen, a specific monitor, or a target window,
/// and tracks recent CoordinateTransform instances so mouse actions can map image coordinates accurately.
/// </summary>
public sealed class ScreenCaptureService
{
    private const int MaxCachedTransforms = 16;
    private readonly ConcurrentDictionary<string, CoordinateTransform> _transforms = new();
    private readonly ConcurrentQueue<string> _order = new();
    private volatile CoordinateTransform? _latestTransform;

    public ScreenshotResult Capture(
        CaptureTarget target = CaptureTarget.Screen,
        int? monitorIndex = null,
        long? hwnd = null,
        int maxWidth = 1280,
        CaptureFormat format = CaptureFormat.Jpeg,
        RectI? region = null)
    {
        var totalTimer = Stopwatch.StartNew();
        var setupTimer = Stopwatch.StartNew();
        DpiAwareness.EnsurePerMonitorV2();

        RectI virtualBounds = DpiAwareness.GetVirtualScreenBounds();
        IReadOnlyList<MonitorInfo> monitors = DpiAwareness.GetMonitors();

        RectI captureRect;
        IReadOnlyList<MonitorInfo> involvedMonitors;

        switch (target)
        {
            case CaptureTarget.Monitor:
                int idx = monitorIndex ?? 0;
                if (idx < 0 || idx >= monitors.Count)
                    throw new ArgumentOutOfRangeException(nameof(monitorIndex), $"Monitor index {idx} out of range (0..{monitors.Count - 1}).");

                var mon = monitors[idx];
                captureRect = mon.Bounds;
                involvedMonitors = [mon];
                break;

            case CaptureTarget.Window:
                if (hwnd is null)
                    throw new ArgumentException("hwnd is required when target is 'window'.", nameof(hwnd));

                var win = WindowService.GetWindowInfo(hwnd.Value)
                    ?? throw new InvalidOperationException($"Window handle {hwnd.Value} not found.");

                if (win.State == WindowVisualState.Minimized || win.BoundingRect.IsEmpty)
                    throw new InvalidOperationException($"Window '{win.Title}' (HWND {hwnd.Value}) is minimized or has empty bounds.");

                captureRect = win.BoundingRect;
                involvedMonitors = monitors.Where(m => Intersects(m.Bounds, captureRect)).ToList();
                if (involvedMonitors.Count == 0)
                    involvedMonitors = monitors;
                break;

            case CaptureTarget.Region:
                if (region is null || region.Value.IsEmpty)
                    throw new ArgumentException("A non-empty region (x, y, width, height) is required when target is 'region'.", nameof(region));

                captureRect = region.Value;
                involvedMonitors = monitors.Where(m => Intersects(m.Bounds, captureRect)).ToList();
                if (involvedMonitors.Count == 0)
                    involvedMonitors = monitors;
                break;

            case CaptureTarget.Screen:
            default:
                captureRect = virtualBounds;
                involvedMonitors = monitors;
                break;
        }

        double targetSetupMs = setupTimer.Elapsed.TotalMilliseconds;
        var copyTimer = Stopwatch.StartNew();
        using var rawBitmap = new Bitmap(captureRect.Width, captureRect.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(rawBitmap))
        {
            g.CopyFromScreen(captureRect.X, captureRect.Y, 0, 0, new Size(captureRect.Width, captureRect.Height), CopyPixelOperation.SourceCopy);
            DrawCursor(g, captureRect);
        }

        double screenCopyMs = copyTimer.Elapsed.TotalMilliseconds;
        return ProcessBitmapCore(rawBitmap, captureRect, virtualBounds, involvedMonitors, maxWidth, format,
            totalTimer, targetSetupMs, screenCopyMs);
    }

    /// <summary>
    /// Resizes, encodes, and records a CoordinateTransform for an already-captured bitmap.
    /// </summary>
    public ScreenshotResult ProcessBitmap(
        Bitmap rawBitmap,
        RectI captureRect,
        RectI virtualBounds,
        IReadOnlyList<MonitorInfo> involvedMonitors,
        int maxWidth = 1280,
        CaptureFormat format = CaptureFormat.Png)
    {
        return ProcessBitmapCore(rawBitmap, captureRect, virtualBounds, involvedMonitors, maxWidth, format,
            Stopwatch.StartNew(), 0, 0);
    }

    private ScreenshotResult ProcessBitmapCore(
        Bitmap rawBitmap,
        RectI captureRect,
        RectI virtualBounds,
        IReadOnlyList<MonitorInfo> involvedMonitors,
        int maxWidth,
        CaptureFormat format,
        Stopwatch totalTimer,
        double targetSetupMs,
        double screenCopyMs)
    {
        int imageWidth = captureRect.Width;
        int imageHeight = captureRect.Height;
        Bitmap outputBitmap = rawBitmap;
        bool ownsOutput = false;
        double resizeMs = 0;

        if (maxWidth > 0 && captureRect.Width > maxWidth)
        {
            imageWidth = maxWidth;
            imageHeight = Math.Max(1, (int)Math.Round(captureRect.Height * (maxWidth / (double)captureRect.Width)));
            var resizeTimer = Stopwatch.StartNew();
            outputBitmap = ResizeBitmap(rawBitmap, imageWidth, imageHeight);
            resizeMs = resizeTimer.Elapsed.TotalMilliseconds;
            ownsOutput = true;
        }

        try
        {
            var encodeTimer = Stopwatch.StartNew();
            byte[] bytes = EncodeBitmap(outputBitmap, format, out string mimeType);
            double encodeMs = encodeTimer.Elapsed.TotalMilliseconds;
            string captureId = $"cap_{Guid.NewGuid():N}"[..16];

            var transform = new CoordinateTransform(
                CaptureId: captureId,
                OriginX: captureRect.X,
                OriginY: captureRect.Y,
                CaptureWidth: captureRect.Width,
                CaptureHeight: captureRect.Height,
                ImageWidth: imageWidth,
                ImageHeight: imageHeight,
                VirtualScreenBounds: virtualBounds);

            CacheTransform(transform);
            return new ScreenshotResult(bytes, mimeType, transform, involvedMonitors)
            {
                Diagnostics = new ScreenshotDiagnostics(
                    targetSetupMs,
                    screenCopyMs,
                    resizeMs,
                    encodeMs,
                    totalTimer.Elapsed.TotalMilliseconds,
                    captureRect.Width,
                    captureRect.Height,
                    imageWidth,
                    imageHeight,
                    bytes.Length)
            };
        }
        finally
        {
            if (ownsOutput)
                outputBitmap.Dispose();
        }
    }

    public CoordinateTransform? GetTransform(string? captureId)
    {
        if (!string.IsNullOrWhiteSpace(captureId) && _transforms.TryGetValue(captureId, out var t))
            return t;

        return _latestTransform;
    }

    private void CacheTransform(CoordinateTransform transform)
    {
        _transforms[transform.CaptureId] = transform;
        _latestTransform = transform;
        _order.Enqueue(transform.CaptureId);

        while (_order.Count > MaxCachedTransforms && _order.TryDequeue(out string? oldest))
        {
            _transforms.TryRemove(oldest, out _);
        }
    }

    private static Bitmap ResizeBitmap(Bitmap source, int width, int height)
    {
        var dest = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(dest);
        g.CompositingMode = CompositingMode.SourceCopy;
        g.CompositingQuality = CompositingQuality.HighSpeed;
        g.InterpolationMode = InterpolationMode.Bilinear;
        g.SmoothingMode = SmoothingMode.HighSpeed;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.DrawImage(source, 0, 0, width, height);
        return dest;
    }

    private static byte[] EncodeBitmap(Bitmap bitmap, CaptureFormat format, out string mimeType)
    {
        using var ms = new MemoryStream();
        if (format == CaptureFormat.Jpeg)
        {
            mimeType = "image/jpeg";
            var jpegEncoder = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
            using var encoderParams = new EncoderParameters(1);
            encoderParams.Param[0] = new EncoderParameter(Encoder.Quality, 88L);
            bitmap.Save(ms, jpegEncoder, encoderParams);
        }
        else
        {
            mimeType = "image/png";
            bitmap.Save(ms, ImageFormat.Png);
        }

        return ms.ToArray();
    }

    private static bool Intersects(RectI a, RectI b) =>
        a.Left < b.Right && a.Right > b.Left && a.Top < b.Bottom && a.Bottom > b.Top;

    private static void DrawCursor(Graphics g, RectI captureRect)
    {
        var ci = new CursorInfo { cbSize = Marshal.SizeOf<CursorInfo>() };
        if (!GetCursorInfo(ref ci) || (ci.flags & 0x00000001 /* CURSOR_SHOWING */) == 0 || ci.hCursor == IntPtr.Zero)
            return;

        if (!captureRect.Contains(ci.ptScreenPos.X, ci.ptScreenPos.Y))
            return;

        IntPtr hIcon = CopyIcon(ci.hCursor);
        if (hIcon == IntPtr.Zero)
            return;

        try
        {
            int drawX = ci.ptScreenPos.X - captureRect.X;
            int drawY = ci.ptScreenPos.Y - captureRect.Y;

            if (GetIconInfo(hIcon, out IconInfo ii))
            {
                drawX -= ii.xHotspot;
                drawY -= ii.yHotspot;
                if (ii.hbmMask != IntPtr.Zero) DeleteObject(ii.hbmMask);
                if (ii.hbmColor != IntPtr.Zero) DeleteObject(ii.hbmColor);
            }

            IntPtr hdc = g.GetHdc();
            try
            {
                DrawIconEx(hdc, drawX, drawY, hIcon, 0, 0, 0, IntPtr.Zero, 0x0003 /* DI_NORMAL */);
            }
            finally
            {
                g.ReleaseHdc(hdc);
            }
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorInfo(ref CursorInfo pci);

    [DllImport("user32.dll")]
    private static extern IntPtr CopyIcon(IntPtr hIcon);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll")]
    private static extern bool GetIconInfo(IntPtr hIcon, out IconInfo piconinfo);

    [DllImport("user32.dll")]
    private static extern bool DrawIconEx(IntPtr hdc, int xLeft, int yTop, IntPtr hIcon, int cxWidth, int cyWidth, uint istepIfAniCur, IntPtr hbrFlickerFreeDraw, uint diFlags);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CursorInfo
    {
        public int cbSize;
        public int flags;
        public IntPtr hCursor;
        public NativePoint ptScreenPos;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IconInfo
    {
        public bool fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }
}
