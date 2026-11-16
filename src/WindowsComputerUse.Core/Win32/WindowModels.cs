using WindowsComputerUse.Core.Dpi;

namespace WindowsComputerUse.Core.Win32;

public enum WindowVisualState
{
    Normal,
    Minimized,
    Maximized
}

/// <summary>
/// Metadata for a visible top-level window on the Windows desktop.
/// </summary>
public sealed record WindowInfo(
    long Hwnd,
    int Pid,
    string ProcessName,
    string Title,
    string ClassName,
    WindowVisualState State,
    bool IsElevated,
    bool IsForeground,
    RectI BoundingRect);
