namespace WindowsComputerUse.Core.Dpi;

/// <summary>
/// Axis-aligned integer rectangle in physical desktop pixels.
/// </summary>
public readonly record struct RectI(int X, int Y, int Width, int Height)
{
    public int Left => X;
    public int Top => Y;
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public bool IsEmpty => Width <= 0 || Height <= 0;

    public bool Contains(int px, int py) =>
        px >= X && px < Right && py >= Y && py < Bottom;
}

/// <summary>
/// Physical display monitor metadata including DPI scaling.
/// </summary>
public sealed record MonitorInfo(
    int Index,
    string DeviceName,
    bool IsPrimary,
    RectI Bounds,
    RectI WorkingArea,
    uint DpiX,
    uint DpiY)
{
    public double ScaleFactor => DpiX / 96.0;
    public int ScalePercent => (int)Math.Round(ScaleFactor * 100.0);
}
