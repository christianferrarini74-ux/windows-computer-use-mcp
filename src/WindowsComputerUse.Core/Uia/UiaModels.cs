using WindowsComputerUse.Core.Dpi;

namespace WindowsComputerUse.Core.Uia;

public enum UiaActionKind
{
    Focus,
    Invoke,
    SetValue,
    Select,
    Toggle,
    Expand,
    Collapse
}

public sealed record UiaNodeInfo(
    string ElementId,
    string Name,
    string ControlType,
    string AutomationId,
    string ClassName,
    RectI BoundingRect,
    bool IsEnabled,
    bool IsOffscreen,
    bool HasKeyboardFocus,
    string? LiveSetting,
    string? Value,
    IReadOnlyList<string> Patterns,
    IReadOnlyList<UiaNodeInfo> Children);

public sealed record UiaTreeResult(
    long Hwnd,
    int TotalNodes,
    bool Truncated,
    UiaNodeInfo? Root)
{
    public UiaTreeDiagnostics? Diagnostics { get; init; }
}

public sealed record UiaTreeDiagnostics(
    double RootLookupMs,
    double TreeBuildMs,
    double TotalMs,
    int NodesReturned,
    bool Truncated,
    WindowsComputerUse.Core.Security.AuditWriteDiagnostics? AuditWrite = null);
