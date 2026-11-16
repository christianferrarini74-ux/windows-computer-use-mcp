using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Interop.UIAutomationClient;
using WindowsComputerUse.Core.Dpi;

namespace WindowsComputerUse.Core.Uia;

public sealed class StaleElementException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Provides compact UI Automation (UIA3) tree inspection and pattern-based control interaction.
/// </summary>
public sealed class UiaService
{
    private const int UiaInvokePatternId = 10000;
    private const int UiaSelectionItemPatternId = 10010;
    private const int UiaValuePatternId = 10002;
    private const int UiaExpandCollapsePatternId = 10005;
    private const int UiaTogglePatternId = 10015;
    private const int MaxCachedElements = 2000;
    private const int HResultTransientUia = unchecked((int)0x80040201);

    private readonly CUIAutomation8 _automation = new();
    private readonly ConcurrentDictionary<string, (IUIAutomationElement Element, long WindowHwnd)> _elementCache = new();
    private readonly ConcurrentQueue<string> _cacheOrder = new();
    private readonly Lock _actionLock = new();
    private long _nextId;

    public UiaTreeResult GetTree(
        long hwnd,
        int maxDepth = 4,
        int maxNodes = 250,
        string? rootElementId = null,
        bool focusedOnly = false,
        bool includeOffscreen = false,
        bool interactiveOnly = false)
    {
        const int maxAttempts = 3;
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return GetTreeCore(hwnd, maxDepth, maxNodes, rootElementId, focusedOnly, includeOffscreen, interactiveOnly);
            }
            catch (COMException ex) when (attempt < maxAttempts && IsTransientUiaError(ex))
            {
                Thread.Sleep(attempt * 35);
            }
        }
    }

    private UiaTreeResult GetTreeCore(
        long hwnd,
        int maxDepth,
        int maxNodes,
        string? rootElementId,
        bool focusedOnly,
        bool includeOffscreen,
        bool interactiveOnly)
    {
        var totalTimer = Stopwatch.StartNew();
        var rootTimer = Stopwatch.StartNew();
        IUIAutomationElement? root;
        if (!string.IsNullOrWhiteSpace(rootElementId))
        {
            if (!_elementCache.TryGetValue(rootElementId, out var cached))
                throw new StaleElementException($"UIA element '{rootElementId}' was not found in cache. Re-run get_uia_tree or observe_window.");
            root = cached.Element;
            EnsureElementAlive(root, rootElementId);
        }
        else if (focusedOnly)
        {
            root = _automation.GetFocusedElement();
        }
        else
        {
            root = _automation.ElementFromHandle(new IntPtr(hwnd));
        }

        double rootLookupMs = rootTimer.Elapsed.TotalMilliseconds;
        if (root is null)
        {
            return new UiaTreeResult(hwnd, 0, false, null)
            {
                Diagnostics = new UiaTreeDiagnostics(rootLookupMs, 0, totalTimer.Elapsed.TotalMilliseconds, 0, false)
            };
        }

        var walker = _automation.ControlViewWalker;
        int count = 0;
        bool truncated = false;

        var buildTimer = Stopwatch.StartNew();
        UiaNodeInfo? rootNode = BuildNode(root, hwnd, walker, 0, Math.Clamp(maxDepth, 0, 16), Math.Clamp(maxNodes, 1, 2000), includeOffscreen, interactiveOnly, ref count, ref truncated);
        double treeBuildMs = buildTimer.Elapsed.TotalMilliseconds;
        return new UiaTreeResult(hwnd, count, truncated, rootNode)
        {
            Diagnostics = new UiaTreeDiagnostics(rootLookupMs, treeBuildMs, totalTimer.Elapsed.TotalMilliseconds, count, truncated)
        };
    }

    public long GetOwnerWindowHwnd(string elementId)
    {
        if (!_elementCache.TryGetValue(elementId, out var cached))
            throw new StaleElementException($"UIA element '{elementId}' was not found in cache. Re-run get_uia_tree or observe_window.");

        return cached.WindowHwnd;
    }

    public void PerformAction(string elementId, UiaActionKind action, string? value = null)
    {
        if (!_elementCache.TryGetValue(elementId, out var cached))
            throw new StaleElementException($"UIA element '{elementId}' was not found in cache. Re-run get_uia_tree or observe_window.");

        lock (_actionLock)
        {
            IUIAutomationElement element = cached.Element;
            EnsureElementAlive(element, elementId);

            try
            {
                switch (action)
                {
                    case UiaActionKind.Focus:
                        element.SetFocus();
                        break;

                    case UiaActionKind.Invoke:
                        var invoke = element.GetCurrentPattern(UiaInvokePatternId) as IUIAutomationInvokePattern
                            ?? throw new InvalidOperationException($"Element '{elementId}' does not support InvokePattern.");
                        invoke.Invoke();
                        break;

                    case UiaActionKind.SetValue:
                        var val = element.GetCurrentPattern(UiaValuePatternId) as IUIAutomationValuePattern
                            ?? throw new InvalidOperationException($"Element '{elementId}' does not support ValuePattern.");
                        val.SetValue(value ?? string.Empty);
                        break;

                    case UiaActionKind.Select:
                        var sel = element.GetCurrentPattern(UiaSelectionItemPatternId) as IUIAutomationSelectionItemPattern
                            ?? throw new InvalidOperationException($"Element '{elementId}' does not support SelectionItemPattern.");
                        sel.Select();
                        break;

                    case UiaActionKind.Toggle:
                        var toggle = element.GetCurrentPattern(UiaTogglePatternId) as IUIAutomationTogglePattern
                            ?? throw new InvalidOperationException($"Element '{elementId}' does not support TogglePattern.");
                        toggle.Toggle();
                        break;

                    case UiaActionKind.Expand:
                        var expand = element.GetCurrentPattern(UiaExpandCollapsePatternId) as IUIAutomationExpandCollapsePattern
                            ?? throw new InvalidOperationException($"Element '{elementId}' does not support ExpandCollapsePattern.");
                        expand.Expand();
                        break;

                    case UiaActionKind.Collapse:
                        var collapse = element.GetCurrentPattern(UiaExpandCollapsePatternId) as IUIAutomationExpandCollapsePattern
                            ?? throw new InvalidOperationException($"Element '{elementId}' does not support ExpandCollapsePattern.");
                        collapse.Collapse();
                        break;

                    default:
                        throw new ArgumentOutOfRangeException(nameof(action), action, null);
                }
            }
            catch (COMException ex) when (IsTransientUiaError(ex))
            {
                throw new StaleElementException($"UIA element '{elementId}' became unavailable during '{action}'. Re-run get_uia_tree or observe_window.", ex);
            }
        }
    }

    private static void EnsureElementAlive(IUIAutomationElement element, string elementId)
    {
        try
        {
            int ct = element.CurrentControlType;
            if (ct == 0)
                throw new StaleElementException($"UIA element '{elementId}' is stale (ControlType 0). Re-run get_uia_tree or observe_window.");
        }
        catch (COMException ex)
        {
            throw new StaleElementException($"UIA element '{elementId}' is no longer available (stale). Re-run get_uia_tree or observe_window.", ex);
        }
    }

    private static bool IsTransientUiaError(COMException ex) =>
        ex.HResult == HResultTransientUia || ex.HResult == unchecked((int)0x80040200);

    public bool ElementExists(long hwnd, string? automationId, string? name)
    {
        IUIAutomationElement? root = _automation.ElementFromHandle(new IntPtr(hwnd));
        if (root is null)
            return false;

        IUIAutomationCondition? condition = null;
        if (!string.IsNullOrWhiteSpace(automationId) && !string.IsNullOrWhiteSpace(name))
        {
            var c1 = _automation.CreatePropertyCondition(30011 /* UIA_AutomationIdPropertyId */, automationId);
            var c2 = _automation.CreatePropertyCondition(30005 /* UIA_NamePropertyId */, name);
            condition = _automation.CreateAndCondition(c1, c2);
        }
        else if (!string.IsNullOrWhiteSpace(automationId))
        {
            condition = _automation.CreatePropertyCondition(30011 /* UIA_AutomationIdPropertyId */, automationId);
        }
        else if (!string.IsNullOrWhiteSpace(name))
        {
            condition = _automation.CreatePropertyCondition(30005 /* UIA_NamePropertyId */, name);
        }

        if (condition is null)
            return true;

        return root.FindFirst(TreeScope.TreeScope_Subtree, condition) is not null;
    }

    private UiaNodeInfo? BuildNode(
        IUIAutomationElement element,
        long ownerHwnd,
        IUIAutomationTreeWalker walker,
        int depth,
        int maxDepth,
        int maxNodes,
        bool includeOffscreen,
        bool interactiveOnly,
        ref int count,
        ref bool truncated)
    {
        if (count >= maxNodes)
        {
            truncated = true;
            return null;
        }

        bool isOffscreen = SafeGet(() => element.CurrentIsOffscreen != 0, false);
        if (isOffscreen && !includeOffscreen && depth > 0)
            return null;

        count++;
        string elementId = CacheElement(element, ownerHwnd);

        string name = SafeGet(() => element.CurrentName, string.Empty) ?? string.Empty;
        int controlTypeId = SafeGet(() => element.CurrentControlType, 0);
        string automationId = SafeGet(() => element.CurrentAutomationId, string.Empty) ?? string.Empty;
        string className = SafeGet(() => element.CurrentClassName, string.Empty) ?? string.Empty;
        bool isEnabled = SafeGet(() => element.CurrentIsEnabled != 0, true);
        bool hasFocus = SafeGet(() => element.CurrentHasKeyboardFocus != 0, false);

        RectI rect = SafeGet(() =>
        {
            var r = element.CurrentBoundingRectangle;
            return new RectI(r.left, r.top, Math.Max(0, r.right - r.left), Math.Max(0, r.bottom - r.top));
        }, default);

        string? liveSetting = SafeGet(() =>
        {
            if (element is IUIAutomationElement3 el3)
            {
                return el3.CurrentLiveSetting switch
                {
                    LiveSetting.Off => "Off",
                    LiveSetting.Polite => "Polite",
                    LiveSetting.Assertive => "Assertive",
                    _ => null
                };
            }
            return null;
        }, null);

        var patterns = new List<string>(5);
        string? value = null;

        if (SafeHasPattern(element, UiaInvokePatternId))
            patterns.Add("Invoke");

        if (SafeGet(() => element.GetCurrentPattern(UiaValuePatternId) as IUIAutomationValuePattern, null) is { } valPattern)
        {
            patterns.Add("Value");
            value = SafeGet(() =>
            {
                string v = valPattern.CurrentValue ?? string.Empty;
                return v.Length > 120 ? v[..120] + "..." : v;
            }, null);
        }

        if (SafeHasPattern(element, UiaSelectionItemPatternId))
            patterns.Add("SelectionItem");
        if (SafeHasPattern(element, UiaTogglePatternId))
            patterns.Add("Toggle");
        if (SafeHasPattern(element, UiaExpandCollapsePatternId))
            patterns.Add("ExpandCollapse");

        var children = new List<UiaNodeInfo>();
        if (depth < maxDepth && count < maxNodes)
        {
            IUIAutomationElement? child = SafeGet(() => walker.GetFirstChildElement(element), null);
            while (child is not null)
            {
                if (count >= maxNodes)
                {
                    truncated = true;
                    break;
                }

                var childNode = BuildNode(child, ownerHwnd, walker, depth + 1, maxDepth, maxNodes, includeOffscreen, interactiveOnly, ref count, ref truncated);
                if (childNode is not null)
                    children.Add(childNode);

                child = SafeGet(() => walker.GetNextSiblingElement(child), null);
            }
        }

        if (interactiveOnly && depth > 0 && patterns.Count == 0 && !hasFocus && children.Count == 0)
            return null;

        return new UiaNodeInfo(
            ElementId: elementId,
            Name: name,
            ControlType: MapControlType(controlTypeId),
            AutomationId: automationId,
            ClassName: className,
            BoundingRect: rect,
            IsEnabled: isEnabled,
            IsOffscreen: isOffscreen,
            HasKeyboardFocus: hasFocus,
            LiveSetting: liveSetting,
            Value: value,
            Patterns: patterns,
            Children: children);
    }

    private string CacheElement(IUIAutomationElement element, long ownerHwnd)
    {
        long id = Interlocked.Increment(ref _nextId);
        string key = $"uia_{id}";
        _elementCache[key] = (element, ownerHwnd);
        _cacheOrder.Enqueue(key);

        while (_cacheOrder.Count > MaxCachedElements && _cacheOrder.TryDequeue(out string? oldKey))
        {
            _elementCache.TryRemove(oldKey, out _);
        }

        return key;
    }

    private static bool SafeHasPattern(IUIAutomationElement element, int patternId) =>
        SafeGet(() => element.GetCurrentPattern(patternId) is not null, false);

    private static T SafeGet<T>(Func<T> getter, T fallback)
    {
        try
        {
            return getter();
        }
        catch
        {
            return fallback;
        }
    }

    private static string MapControlType(int id) => id switch
    {
        50000 => "Button",
        50001 => "Calendar",
        50002 => "CheckBox",
        50003 => "ComboBox",
        50004 => "Edit",
        50005 => "Hyperlink",
        50006 => "Image",
        50007 => "ListItem",
        50008 => "List",
        50009 => "Menu",
        50010 => "MenuBar",
        50011 => "MenuItem",
        50012 => "ProgressBar",
        50013 => "RadioButton",
        50014 => "ScrollBar",
        50015 => "Slider",
        50016 => "Spinner",
        50017 => "StatusBar",
        50018 => "Tab",
        50019 => "TabItem",
        50020 => "Text",
        50021 => "ToolBar",
        50022 => "ToolTip",
        50023 => "Tree",
        50024 => "TreeItem",
        50025 => "Custom",
        50026 => "Group",
        50027 => "Thumb",
        50028 => "DataGrid",
        50029 => "DataItem",
        50030 => "Document",
        50031 => "SplitButton",
        50032 => "Window",
        50033 => "Pane",
        50034 => "Header",
        50035 => "HeaderItem",
        50036 => "Table",
        50037 => "TitleBar",
        50038 => "Separator",
        50039 => "SemanticZoom",
        50040 => "AppBar",
        _ => $"ControlType({id})"
    };
}
