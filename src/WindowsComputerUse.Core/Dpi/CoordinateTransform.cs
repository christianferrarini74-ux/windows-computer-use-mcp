namespace WindowsComputerUse.Core.Dpi;

/// <summary>
/// Maps coordinates between a returned (possibly downscaled) screenshot image,
/// physical virtual-screen pixels, and normalized SendInput virtual-desktop coordinates.
/// </summary>
public sealed record CoordinateTransform(
    string CaptureId,
    int OriginX,
    int OriginY,
    int CaptureWidth,
    int CaptureHeight,
    int ImageWidth,
    int ImageHeight,
    RectI VirtualScreenBounds)
{
    public double ScaleX => ImageWidth > 0 ? CaptureWidth / (double)ImageWidth : 1.0;
    public double ScaleY => ImageHeight > 0 ? CaptureHeight / (double)ImageHeight : 1.0;

    /// <summary>
    /// Converts image-space pixel coordinates (ix, iy) into physical virtual-screen coordinates (sx, sy).
    /// </summary>
    public (int ScreenX, int ScreenY) ImageToScreen(double imageX, double imageY)
    {
        int sx = OriginX + (int)Math.Round(imageX * ScaleX);
        int sy = OriginY + (int)Math.Round(imageY * ScaleY);

        int maxX = OriginX + Math.Max(0, CaptureWidth - 1);
        int maxY = OriginY + Math.Max(0, CaptureHeight - 1);

        sx = Math.Clamp(sx, OriginX, maxX);
        sy = Math.Clamp(sy, OriginY, maxY);
        return (sx, sy);
    }

    /// <summary>
    /// Converts physical virtual-screen coordinates (sx, sy) into image-space coordinates.
    /// </summary>
    public (double ImageX, double ImageY) ScreenToImage(int screenX, int screenY)
    {
        double ix = (screenX - OriginX) / ScaleX;
        double iy = (screenY - OriginY) / ScaleY;
        return (ix, iy);
    }

    /// <summary>
    /// Converts physical virtual-screen coordinates (sx, sy) to normalized [0..65535]
    /// coordinates for SendInput with MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK.
    /// </summary>
    public static (int NormalizedX, int NormalizedY) ScreenToSendInputNormalized(int screenX, int screenY, RectI virtualScreen)
    {
        double widthSpan = Math.Max(1, virtualScreen.Width - 1);
        double heightSpan = Math.Max(1, virtualScreen.Height - 1);

        int nx = (int)Math.Round((screenX - virtualScreen.Left) * 65535.0 / widthSpan);
        int ny = (int)Math.Round((screenY - virtualScreen.Top) * 65535.0 / heightSpan);

        return (Math.Clamp(nx, 0, 65535), Math.Clamp(ny, 0, 65535));
    }
}
