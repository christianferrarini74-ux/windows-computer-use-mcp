using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using WindowsComputerUse.Core.Capture;
using WindowsComputerUse.Core.Dpi;
using WindowsComputerUse.Core.Security;
using WindowsComputerUse.Core.Uia;
using WindowsComputerUse.Core.Win32;

namespace WindowsComputerUse.McpServer.Tools;

[McpServerToolType]
public sealed class ComputerUseTools(
    ScreenCaptureService captureService,
    UiaService uiaService,
    PolicyEngine policyEngine,
    KillSwitchService killSwitch,
    AuditLogger auditLogger)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    [McpServerTool(Name = "list_windows")]
    [Description("Lists visible top-level Windows windows with HWND, PID, process name, title, visual state, elevation status, and physical bounding rectangle.")]
    public CallToolResult ListWindows(
        [Description("Include minimized windows in the result.")] bool includeMinimized = false,
        [Description("Optional case-insensitive substring filter on process name (e.g. 'notepad').")] string? processFilter = null,
        [Description("Optional case-insensitive substring filter on window title.")] string? titleFilter = null)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var windows = WindowService.ListWindows(includeMinimized, processFilter, titleFilter);
            auditLogger.Log(new AuditEntry(DateTimeOffset.UtcNow, "list_windows", "Allowed", null, null,
                new { includeMinimized, processFilter, titleFilter, count = windows.Count }, sw.ElapsedMilliseconds));

            return JsonSuccess(new
            {
                policyMode = policyEngine.Mode.ToString(),
                killSwitchTripped = killSwitch.IsTripped,
                windows
            });
        }
        catch (Exception ex)
        {
            auditLogger.Log(new AuditEntry(DateTimeOffset.UtcNow, "list_windows", "Error", null, null,
                new { includeMinimized, processFilter, titleFilter }, sw.ElapsedMilliseconds, ex.Message));
            return ErrorResult("INTERNAL_ERROR", ex.Message);
        }
    }

    [McpServerTool(Name = "screenshot")]
    [Description("Captures a DPI-aware screenshot of the virtual screen, a specific monitor, a target window, or a screen region. Returns the image plus exact coordinate transform metadata for mapping image pixels to physical screen coordinates.")]
    public CallToolResult Screenshot(
        [Description("Capture target: 'screen', 'monitor', 'window', or 'region'.")] string target = "screen",
        [Description("Window handle (HWND) when target is 'window'.")] long? hwnd = null,
        [Description("Zero-based monitor index when target is 'monitor'.")] int? monitorIndex = null,
        [Description("Maximum image width in pixels (0 disables downscaling). Default is 1280.")] int maxWidth = 1280,
        [Description("Image format: 'jpeg' (default, fast) or 'png' (lossless).")] string format = "jpeg",
        [Description("Region X in physical screen pixels (when target is 'region').")] int? regionX = null,
        [Description("Region Y in physical screen pixels (when target is 'region').")] int? regionY = null,
        [Description("Region width in physical screen pixels (when target is 'region').")] int? regionWidth = null,
        [Description("Region height in physical screen pixels (when target is 'region').")] int? regionHeight = null)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            killSwitch.EnsureActive();

            if (!Enum.TryParse<CaptureTarget>(target, ignoreCase: true, out var captureTarget))
                return ErrorResult("INVALID_ARGUMENT", $"Invalid target '{target}'. Expected 'screen', 'monitor', 'window', or 'region'.");

            if (!Enum.TryParse<CaptureFormat>(format, ignoreCase: true, out var captureFormat))
                return ErrorResult("INVALID_ARGUMENT", $"Invalid format '{format}'. Expected 'jpeg' or 'png'.");

            RectI? regionRect = null;
            if (captureTarget == CaptureTarget.Region)
            {
                if (!regionX.HasValue || !regionY.HasValue || !regionWidth.HasValue || !regionHeight.HasValue)
                    return ErrorResult("INVALID_ARGUMENT", "regionX, regionY, regionWidth, and regionHeight are required when target is 'region'.");
                regionRect = new RectI(regionX.Value, regionY.Value, regionWidth.Value, regionHeight.Value);
            }

            WindowInfo? targetWin = captureTarget == CaptureTarget.Window && hwnd.HasValue
                ? WindowService.GetWindowInfo(hwnd.Value)
                : null;

            var result = captureService.Capture(captureTarget, monitorIndex, hwnd, maxWidth, captureFormat, regionRect);

            var auditWrite = auditLogger.Log(new AuditEntry(DateTimeOffset.UtcNow, "screenshot", "Allowed",
                targetWin?.ProcessName, hwnd,
                new { target, hwnd, monitorIndex, maxWidth, format, result.Transform.CaptureId, performance = result.Diagnostics }, sw.ElapsedMilliseconds));

            string metadataJson = JsonSerializer.Serialize(new
            {
                captureId = result.Transform.CaptureId,
                originX = result.Transform.OriginX,
                originY = result.Transform.OriginY,
                captureWidth = result.Transform.CaptureWidth,
                captureHeight = result.Transform.CaptureHeight,
                imageWidth = result.Transform.ImageWidth,
                imageHeight = result.Transform.ImageHeight,
                scaleX = result.Transform.ScaleX,
                scaleY = result.Transform.ScaleY,
                virtualScreen = result.Transform.VirtualScreenBounds,
                monitors = result.Monitors,
                performance = result.Diagnostics,
                auditWrite
            }, JsonOptions);

            return new CallToolResult
            {
                Content =
                [
                    ImageContentBlock.FromBytes(result.ImageBytes, result.MimeType),
                    new TextContentBlock { Text = metadataJson }
                ]
            };
        }
        catch (KillSwitchTrippedException ex)
        {
            return ErrorResult("KILL_SWITCH_ACTIVE", ex.Message);
        }
        catch (Exception ex)
        {
            auditLogger.Log(new AuditEntry(DateTimeOffset.UtcNow, "screenshot", "Error", null, hwnd,
                new { target, hwnd, monitorIndex, maxWidth, format }, sw.ElapsedMilliseconds, ex.Message));
            return ErrorResult("CAPTURE_FAILED", ex.Message);
        }
    }

    [McpServerTool(Name = "get_uia_tree")]
    [Description("Returns a compact, filterable UI Automation (UIA3) tree for a target window, including elementId, name, controlType, automationId, boundingRect, isEnabled, hasKeyboardFocus, liveSetting, and supported patterns.")]
    public CallToolResult GetUiaTree(
        [Description("Target window handle (HWND).")] long hwnd,
        [Description("Maximum tree depth to traverse (0-16). Default is 4.")] int maxDepth = 4,
        [Description("Maximum number of nodes to return (1-2000). Default is 250.")] int maxNodes = 250,
        [Description("Optional elementId from a previous get_uia_tree call to inspect a subtree.")] string? rootElementId = null,
        [Description("If true, starts from the currently focused UIA element instead of the window root.")] bool focusedOnly = false,
        [Description("Include offscreen elements in the tree.")] bool includeOffscreen = false,
        [Description("If true, prunes non-interactive container/text nodes without patterns or focus.")] bool interactiveOnly = false)
    {
        var sw = Stopwatch.StartNew();
        WindowInfo? win = WindowService.GetWindowInfo(hwnd);
        try
        {
            killSwitch.EnsureActive();

            var tree = uiaService.GetTree(hwnd, maxDepth, maxNodes, rootElementId, focusedOnly, includeOffscreen, interactiveOnly);
            var auditWrite = auditLogger.Log(new AuditEntry(DateTimeOffset.UtcNow, "get_uia_tree", "Allowed",
                win?.ProcessName, hwnd, new { maxDepth, maxNodes, rootElementId, focusedOnly, interactiveOnly, tree.TotalNodes, tree.Truncated, performance = tree.Diagnostics }, sw.ElapsedMilliseconds));
            if (tree.Diagnostics is not null)
                tree = tree with { Diagnostics = tree.Diagnostics with { AuditWrite = auditWrite } };

            return JsonSuccess(tree);
        }
        catch (KillSwitchTrippedException ex)
        {
            return ErrorResult("KILL_SWITCH_ACTIVE", ex.Message);
        }
        catch (StaleElementException ex)
        {
            auditLogger.Log(new AuditEntry(DateTimeOffset.UtcNow, "get_uia_tree", "StaleElement",
                win?.ProcessName, hwnd, new { maxDepth, maxNodes, rootElementId, focusedOnly }, sw.ElapsedMilliseconds, ex.Message));
            return ErrorResult("STALE_ELEMENT", ex.Message);
        }
        catch (Exception ex)
        {
            auditLogger.Log(new AuditEntry(DateTimeOffset.UtcNow, "get_uia_tree", "Error",
                win?.ProcessName, hwnd, new { maxDepth, maxNodes, rootElementId, focusedOnly }, sw.ElapsedMilliseconds, ex.Message));
            return ErrorResult("UIA_ERROR", ex.Message);
        }
    }

    [McpServerTool(Name = "observe_window")]
    [Description("Default inspection tool for desktop work. Captures a target window and returns its screenshot and compact UI Automation tree together in one call.")]
    public CallToolResult ObserveWindow(
        [Description("Target window handle (HWND), usually from list_windows.")] long hwnd,
        [Description("Include a screenshot in the result. Default is true.")] bool includeScreenshot = true,
        [Description("Include a compact UI Automation tree in the result. Default is true.")] bool includeUia = true,
        [Description("Maximum screenshot width in pixels. Default is 1280.")] int maxWidth = 1280,
        [Description("Maximum UIA tree depth (0-16). Default is 3.")] int maxDepth = 3,
        [Description("Maximum UIA nodes (1-2000). Default is 150.")] int maxNodes = 150,
        [Description("Include offscreen UIA elements.")] bool includeOffscreen = false,
        [Description("Prune non-interactive UIA container/text nodes.")] bool interactiveOnly = true)
    {
        var sw = Stopwatch.StartNew();
        WindowInfo? win = null;
        try
        {
            killSwitch.EnsureActive();
            if (!includeScreenshot && !includeUia)
                return ErrorResult("INVALID_ARGUMENT", "At least one of includeScreenshot or includeUia must be true.");

            win = WindowService.GetWindowInfo(hwnd);
            if (win is null)
                return ErrorResult("WINDOW_NOT_FOUND", $"Window handle {hwnd} was not found.");

            var observationStartedUtc = DateTimeOffset.UtcNow;
            var tree = includeUia
                ? uiaService.GetTree(hwnd, maxDepth, maxNodes, includeOffscreen: includeOffscreen, interactiveOnly: interactiveOnly)
                : null;
            var shot = includeScreenshot
                ? captureService.Capture(CaptureTarget.Window, null, hwnd, maxWidth, CaptureFormat.Jpeg)
                : null;

            var auditWrite = auditLogger.Log(new AuditEntry(DateTimeOffset.UtcNow, "observe_window", "Allowed",
                win.ProcessName, hwnd,
                new { includeScreenshot, includeUia, maxWidth, maxDepth, maxNodes, nodeCount = tree?.TotalNodes, screenshotBytes = shot?.ImageBytes.Length },
                sw.ElapsedMilliseconds));

            string metadataJson = JsonSerializer.Serialize(new
            {
                window = win,
                observationStartedUtc,
                observationDurationMs = sw.Elapsed.TotalMilliseconds,
                uiaTree = tree,
                screenshot = shot is null ? null : new
                {
                    captureId = shot.Transform.CaptureId,
                    originX = shot.Transform.OriginX,
                    originY = shot.Transform.OriginY,
                    captureWidth = shot.Transform.CaptureWidth,
                    captureHeight = shot.Transform.CaptureHeight,
                    imageWidth = shot.Transform.ImageWidth,
                    imageHeight = shot.Transform.ImageHeight,
                    scaleX = shot.Transform.ScaleX,
                    scaleY = shot.Transform.ScaleY,
                    performance = shot.Diagnostics
                },
                auditWrite
            }, JsonOptions);

            return new CallToolResult
            {
                Content = shot is null
                    ? [new TextContentBlock { Text = metadataJson }]
                    : [ImageContentBlock.FromBytes(shot.ImageBytes, shot.MimeType), new TextContentBlock { Text = metadataJson }]
            };
        }
        catch (KillSwitchTrippedException ex)
        {
            return ErrorResult("KILL_SWITCH_ACTIVE", ex.Message);
        }
        catch (Exception ex)
        {
            auditLogger.Log(new AuditEntry(DateTimeOffset.UtcNow, "observe_window", "Error",
                win?.ProcessName, hwnd, new { includeScreenshot, includeUia, maxWidth, maxDepth, maxNodes }, sw.ElapsedMilliseconds, ex.Message));
            return ErrorResult("OBSERVATION_FAILED", ex.Message);
        }
    }

    [McpServerTool(Name = "launch_app")]
    [Description("Launches a desktop application, executable, file, or shell URI in the interactive user session (non-elevated when possible) and optionally waits for its window to appear.")]
    public async Task<CallToolResult> LaunchApp(
        [Description("Path to executable, document, or shell URI (e.g. 'notepad.exe' or 'D:\\App\\MyApp.exe').")] string target,
        [Description("Optional command-line arguments.")] string? arguments = null,
        [Description("Optional working directory.")] string? workingDirectory = null,
        [Description("If true (default), waits for a matching window to appear after launch.")] bool waitForWindow = true,
        [Description("Optional process name substring to wait for (defaults to target executable name).")] string? processName = null,
        [Description("Maximum wait timeout in milliseconds when waitForWindow is true. Default is 10000.")] int timeoutMs = 10000,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, killSwitch.Token);
        try
        {
            killSwitch.EnsureActive();

            if (string.IsNullOrWhiteSpace(target))
                return ErrorResult("INVALID_ARGUMENT", "'target' is required.");

            string inferredProc = !string.IsNullOrWhiteSpace(processName)
                ? processName.Trim()
                : Path.GetFileNameWithoutExtension(target.Trim());

            if (policyEngine.Mode == PolicyMode.Allowlist && !string.IsNullOrWhiteSpace(inferredProc))
            {
                var synthetic = new WindowInfo(0, 0, inferredProc + ".exe", target, string.Empty, WindowVisualState.Normal, false, false, default);
                var decision = policyEngine.Evaluate(synthetic);
                if (!decision.Allowed)
                {
                    auditLogger.Log(new AuditEntry(DateTimeOffset.UtcNow, "launch_app", decision.Code,
                        synthetic.ProcessName, null, new { target, arguments }, sw.ElapsedMilliseconds, decision.Reason));
                    return ErrorResult(decision.Code, decision.Reason);
                }
            }

            int? pid = WindowService.LaunchApp(target, arguments, workingDirectory);
            WindowInfo? launchedWindow = null;

            if (waitForWindow && !string.IsNullOrWhiteSpace(inferredProc))
            {
                var deadline = Environment.TickCount64 + Math.Clamp(timeoutMs, 100, 60000);
                int poll = 0;
                while (Environment.TickCount64 < deadline)
                {
                    linked.Token.ThrowIfCancellationRequested();
                    launchedWindow = WindowService.ListWindows(includeMinimized: false, processFilter: inferredProc)
                        .FirstOrDefault(w => !pid.HasValue || w.Pid == pid.Value)
                        ?? WindowService.ListWindows(includeMinimized: false, processFilter: inferredProc).FirstOrDefault();
                    if (launchedWindow is not null)
                        break;
                    await Task.Delay(++poll <= 10 ? 40 : 120, linked.Token);
                }
            }

            auditLogger.Log(new AuditEntry(DateTimeOffset.UtcNow, "launch_app", "Allowed",
                launchedWindow?.ProcessName ?? inferredProc, launchedWindow?.Hwnd,
                new { target, arguments, pid, waitForWindow }, sw.ElapsedMilliseconds));

            return JsonSuccess(new
            {
                ok = true,
                target,
                pid = launchedWindow?.Pid ?? pid,
                window = launchedWindow,
                elapsedMs = sw.ElapsedMilliseconds
            });
        }
        catch (OperationCanceledException) when (killSwitch.IsTripped)
        {
            return ErrorResult("KILL_SWITCH_ACTIVE", $"Launch aborted by kill-switch ({killSwitch.HotkeyDescription}).");
        }
        catch (KillSwitchTrippedException ex)
        {
            return ErrorResult("KILL_SWITCH_ACTIVE", ex.Message);
        }
        catch (Exception ex)
        {
            auditLogger.Log(new AuditEntry(DateTimeOffset.UtcNow, "launch_app", "Error", null, null,
                new { target, arguments }, sw.ElapsedMilliseconds, ex.Message));
            return ErrorResult("LAUNCH_FAILED", ex.Message);
        }
    }

    [McpServerTool(Name = "focus_window")]
    [Description("Restores (if minimized) and brings a target window to the foreground by HWND or PID, subject to policy and UIPI checks.")]
    public CallToolResult FocusWindow(
        [Description("Target window handle (HWND).")] long? hwnd = null,
        [Description("Target process ID (PID) if HWND is not provided.")] int? pid = null,
        [Description("Restore window if currently minimized. Default is true.")] bool restoreIfMinimized = true)
    {
        var sw = Stopwatch.StartNew();
        WindowInfo? target = null;
        try
        {
            killSwitch.EnsureActive();

            if (hwnd.HasValue)
            {
                target = WindowService.GetWindowInfo(hwnd.Value);
            }
            else if (pid.HasValue)
            {
                target = WindowService.ListWindows(includeMinimized: true)
                    .FirstOrDefault(w => w.Pid == pid.Value);
            }
            else
            {
                return ErrorResult("INVALID_ARGUMENT", "Either 'hwnd' or 'pid' must be provided.");
            }

            if (target is null)
                return ErrorResult("WINDOW_NOT_FOUND", "Target window was not found.");

            var decision = policyEngine.Evaluate(target);
            if (!decision.Allowed)
            {
                auditLogger.Log(new AuditEntry(DateTimeOffset.UtcNow, "focus_window", decision.Code,
                    target.ProcessName, target.Hwnd, new { hwnd, pid }, sw.ElapsedMilliseconds, decision.Reason));
                return ErrorResult(decision.Code, decision.Reason);
            }

            bool focused = WindowService.FocusWindow(target.Hwnd, restoreIfMinimized);
            auditLogger.Log(new AuditEntry(DateTimeOffset.UtcNow, "focus_window", focused ? "Allowed" : "Failed",
                target.ProcessName, target.Hwnd, new { hwnd = target.Hwnd, restoreIfMinimized, focused }, sw.ElapsedMilliseconds));

            return JsonSuccess(new { focused, window = WindowService.GetWindowInfo(target.Hwnd) ?? target });
        }
        catch (KillSwitchTrippedException ex)
        {
            return ErrorResult("KILL_SWITCH_ACTIVE", ex.Message);
        }
        catch (Exception ex)
        {
            auditLogger.Log(new AuditEntry(DateTimeOffset.UtcNow, "focus_window", "Error",
                target?.ProcessName, target?.Hwnd, new { hwnd, pid }, sw.ElapsedMilliseconds, ex.Message));
            return ErrorResult("FOCUS_FAILED", ex.Message);
        }
    }

    [McpServerTool(Name = "mouse")]
    [Description("Performs physical mouse actions (move, click, double_click, right_click, drag, scroll) via Win32 SendInput, mapping image coordinates from a screenshot to physical screen coordinates. Keeps a colored pointer halo visible while the Computer Use server is active.")]
    public CallToolResult Mouse(
        [Description("Action: 'move', 'click', 'double_click', 'right_click', 'drag', or 'scroll'.")] string action,
        [Description("Target X coordinate.")] double x,
        [Description("Target Y coordinate.")] double y,
        [Description("End X coordinate (required for 'drag').")] double? endX = null,
        [Description("End Y coordinate (required for 'drag').")] double? endY = null,
        [Description("Scroll wheel delta (e.g. -120 for down, 120 for up; required for 'scroll').")] int delta = 0,
        [Description("Horizontal scroll when true (for 'scroll').")] bool horizontal = false,
        [Description("Coordinate space: 'image' (default, uses screenshot transform) or 'screen' (physical virtual-screen pixels).")] string coordinateSpace = "image",
        [Description("Optional captureId from screenshot; defaults to the latest screenshot transform when coordinateSpace is 'image'.")] string? captureId = null)
    {
        var sw = Stopwatch.StartNew();
        WindowInfo? targetWin = null;
        try
        {
            killSwitch.EnsureActive();

            if (!TryParseMouseAction(action, out var kind))
                return ErrorResult("INVALID_ARGUMENT", $"Invalid mouse action '{action}'.");

            var (sx, sy) = ResolveCoordinates(x, y, coordinateSpace, captureId);
            targetWin = WindowService.GetWindowAtPoint(sx, sy);

            var decision = policyEngine.Evaluate(targetWin);
            if (!decision.Allowed)
            {
                auditLogger.Log(new AuditEntry(DateTimeOffset.UtcNow, "mouse", decision.Code,
                    targetWin?.ProcessName, targetWin?.Hwnd,
                    new { action, x, y, sx, sy, coordinateSpace }, sw.ElapsedMilliseconds, decision.Reason));
                return ErrorResult(decision.Code, decision.Reason);
            }

            switch (kind)
            {
                case MouseActionKind.Move:
                    InputService.MoveMouse(sx, sy, killSwitch.Token, killSwitch.ShowMousePointerAt);
                    break;
                case MouseActionKind.Click:
                    InputService.Click(sx, sy, rightButton: false, ct: killSwitch.Token, onMove: killSwitch.ShowMousePointerAt);
                    break;
                case MouseActionKind.DoubleClick:
                    InputService.DoubleClick(sx, sy, killSwitch.Token, killSwitch.ShowMousePointerAt);
                    break;
                case MouseActionKind.RightClick:
                    InputService.Click(sx, sy, rightButton: true, ct: killSwitch.Token, onMove: killSwitch.ShowMousePointerAt);
                    break;
                case MouseActionKind.Drag:
                    if (!endX.HasValue || !endY.HasValue)
                        return ErrorResult("INVALID_ARGUMENT", "endX and endY are required for 'drag'.");
                    var (esx, esy) = ResolveCoordinates(endX.Value, endY.Value, coordinateSpace, captureId);
                    var endWin = WindowService.GetWindowAtPoint(esx, esy);
                    var endDecision = policyEngine.Evaluate(endWin);
                    if (!endDecision.Allowed)
                        return ErrorResult(endDecision.Code, endDecision.Reason);
                    InputService.Drag(sx, sy, esx, esy, ct: killSwitch.Token, onMove: killSwitch.ShowMousePointerAt);
                    break;
                case MouseActionKind.Scroll:
                    InputService.Scroll(sx, sy, delta, horizontal, killSwitch.Token, killSwitch.ShowMousePointerAt);
                    break;
            }

            auditLogger.Log(new AuditEntry(DateTimeOffset.UtcNow, "mouse", "Allowed",
                targetWin?.ProcessName, targetWin?.Hwnd,
                new { action, sx, sy, endX, endY, delta, horizontal }, sw.ElapsedMilliseconds));

            return JsonSuccess(new { ok = true, action, screenX = sx, screenY = sy, targetWindow = targetWin?.ProcessName });
        }
        catch (KillSwitchTrippedException ex)
        {
            return ErrorResult("KILL_SWITCH_ACTIVE", ex.Message);
        }
        catch (Exception ex)
        {
            auditLogger.Log(new AuditEntry(DateTimeOffset.UtcNow, "mouse", "Error",
                targetWin?.ProcessName, targetWin?.Hwnd, new { action, x, y }, sw.ElapsedMilliseconds, ex.Message));
            return ErrorResult("MOUSE_FAILED", ex.Message);
        }
    }

    [McpServerTool(Name = "keyboard")]
    [Description("Sends physical keyboard input (type_text, key_press, hotkey) via Win32 SendInput to the current foreground window, subject to policy and UIPI checks.")]
    public CallToolResult Keyboard(
        [Description("Action: 'type_text', 'key_press', or 'hotkey'.")] string action,
        [Description("Text to type (for 'type_text').")] string? text = null,
        [Description("Single key name (for 'key_press', e.g. 'Enter', 'Tab', 'Escape', 'F5').")] string? key = null,
        [Description("Ordered list of keys for 'hotkey' (e.g. ['Ctrl','S'] or ['Shift','Tab']).")] string[]? keys = null,
        [Description("If true, masks typed text in the audit log.")] bool sensitive = false)
    {
        var sw = Stopwatch.StartNew();
        WindowInfo? fg = WindowService.GetForegroundWindowInfo();
        try
        {
            killSwitch.EnsureActive();

            var decision = policyEngine.Evaluate(fg);
            if (!decision.Allowed)
            {
                auditLogger.Log(new AuditEntry(DateTimeOffset.UtcNow, "keyboard", decision.Code,
                    fg?.ProcessName, fg?.Hwnd, new { action, key, keys }, sw.ElapsedMilliseconds, decision.Reason));
                return ErrorResult(decision.Code, decision.Reason);
            }

            switch (action.Trim().ToLowerInvariant())
            {
                case "type_text":
                    if (text is null)
                        return ErrorResult("INVALID_ARGUMENT", "'text' is required for 'type_text'.");
                    InputService.TypeText(text, killSwitch.Token);
                    break;

                case "key_press":
                    if (string.IsNullOrWhiteSpace(key))
                        return ErrorResult("INVALID_ARGUMENT", "'key' is required for 'key_press'.");
                    InputService.KeyPress(key);
                    break;

                case "hotkey":
                    if (keys is null || keys.Length == 0)
                        return ErrorResult("INVALID_ARGUMENT", "'keys' is required for 'hotkey'.");
                    InputService.Hotkey(keys);
                    break;

                default:
                    return ErrorResult("INVALID_ARGUMENT", $"Invalid keyboard action '{action}'.");
            }

            object loggedParams = sensitive
                ? new { action, text = (string?)"***REDACTED***", key, keys }
                : new { action, text, key, keys };

            auditLogger.Log(new AuditEntry(DateTimeOffset.UtcNow, "keyboard", "Allowed",
                fg?.ProcessName, fg?.Hwnd, loggedParams, sw.ElapsedMilliseconds));

            return JsonSuccess(new { ok = true, action, foregroundWindow = fg?.ProcessName });
        }
        catch (KillSwitchTrippedException ex)
        {
            return ErrorResult("KILL_SWITCH_ACTIVE", ex.Message);
        }
        catch (Exception ex)
        {
            auditLogger.Log(new AuditEntry(DateTimeOffset.UtcNow, "keyboard", "Error",
                fg?.ProcessName, fg?.Hwnd, new { action, key, keys }, sw.ElapsedMilliseconds, ex.Message));
            return ErrorResult("KEYBOARD_FAILED", ex.Message);
        }
    }

    [McpServerTool(Name = "uia_action")]
    [Description("Executes a semantic UI Automation action (focus, invoke, set_value, select, toggle, expand, collapse) on an elementId returned by get_uia_tree.")]
    public CallToolResult UiaAction(
        [Description("Element ID returned by get_uia_tree.")] string elementId,
        [Description("Action: 'focus', 'invoke', 'set_value', 'select', 'toggle', 'expand', or 'collapse'.")] string action,
        [Description("Value to set when action is 'set_value'.")] string? value = null)
    {
        var sw = Stopwatch.StartNew();
        WindowInfo? win = null;
        try
        {
            killSwitch.EnsureActive();

            if (!TryParseUiaAction(action, out var kind))
                return ErrorResult("INVALID_ARGUMENT", $"Invalid uia_action '{action}'.");

            long hwnd = uiaService.GetOwnerWindowHwnd(elementId);
            win = WindowService.GetWindowInfo(hwnd);

            var decision = policyEngine.Evaluate(win);
            if (!decision.Allowed)
            {
                auditLogger.Log(new AuditEntry(DateTimeOffset.UtcNow, "uia_action", decision.Code,
                    win?.ProcessName, hwnd, new { elementId, action }, sw.ElapsedMilliseconds, decision.Reason));
                return ErrorResult(decision.Code, decision.Reason);
            }

            uiaService.PerformAction(elementId, kind, value);
            auditLogger.Log(new AuditEntry(DateTimeOffset.UtcNow, "uia_action", "Allowed",
                win?.ProcessName, hwnd, new { elementId, action }, sw.ElapsedMilliseconds));

            return JsonSuccess(new { ok = true, elementId, action });
        }
        catch (KillSwitchTrippedException ex)
        {
            return ErrorResult("KILL_SWITCH_ACTIVE", ex.Message);
        }
        catch (StaleElementException ex)
        {
            auditLogger.Log(new AuditEntry(DateTimeOffset.UtcNow, "uia_action", "StaleElement",
                win?.ProcessName, win?.Hwnd, new { elementId, action }, sw.ElapsedMilliseconds, ex.Message));
            return ErrorResult("STALE_ELEMENT", ex.Message);
        }
        catch (Exception ex)
        {
            auditLogger.Log(new AuditEntry(DateTimeOffset.UtcNow, "uia_action", "Error",
                win?.ProcessName, win?.Hwnd, new { elementId, action }, sw.ElapsedMilliseconds, ex.Message));
            return ErrorResult("UIA_ACTION_FAILED", ex.Message);
        }
    }

    [McpServerTool(Name = "wait")]
    [Description("Waits for a fixed delay, or polls until a matching window or UIA element appears, respecting the global kill-switch.")]
    public async Task<CallToolResult> Wait(
        [Description("Wait mode: 'delay', 'window', or 'uia_element'.")] string mode = "delay",
        [Description("Delay in milliseconds (for 'delay' mode).")] int ms = 500,
        [Description("Maximum wait timeout in milliseconds (for 'window' and 'uia_element' modes). Default is 10000.")] int timeoutMs = 10000,
        [Description("Target process name substring (for 'window' mode).")] string? processName = null,
        [Description("Target window title regex pattern (for 'window' mode).")] string? titlePattern = null,
        [Description("Target window HWND (for 'uia_element' mode).")] long? hwnd = null,
        [Description("Target UIA AutomationId (for 'uia_element' mode).")] string? automationId = null,
        [Description("Target UIA Name (for 'uia_element' mode).")] string? name = null,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, killSwitch.Token);
        try
        {
            killSwitch.EnsureActive();

            switch (mode.Trim().ToLowerInvariant())
            {
                case "delay":
                    await Task.Delay(Math.Clamp(ms, 0, 60000), linked.Token);
                    return JsonSuccess(new { ok = true, mode = "delay", elapsedMs = sw.ElapsedMilliseconds });

                case "window":
                    Regex? titleRegex = string.IsNullOrWhiteSpace(titlePattern)
                        ? null
                        : new Regex(titlePattern, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(200));

                    var deadline = Environment.TickCount64 + Math.Clamp(timeoutMs, 100, 120000);
                    int pollCount = 0;
                    while (Environment.TickCount64 < deadline)
                    {
                        linked.Token.ThrowIfCancellationRequested();
                        var match = WindowService.ListWindows(includeMinimized: false, processFilter: processName)
                            .FirstOrDefault(w => titleRegex is null || titleRegex.IsMatch(w.Title));
                        if (match is not null)
                        {
                            return JsonSuccess(new { ok = true, mode = "window", elapsedMs = sw.ElapsedMilliseconds, window = match });
                        }
                        await Task.Delay(++pollCount <= 10 ? 40 : 120, linked.Token);
                    }
                    return ErrorResult("WAIT_TIMEOUT", "Timed out waiting for matching window.");

                case "uia_element":
                    if (!hwnd.HasValue)
                        return ErrorResult("INVALID_ARGUMENT", "'hwnd' is required for 'uia_element' mode.");

                    var uiaDeadline = Environment.TickCount64 + Math.Clamp(timeoutMs, 100, 120000);
                    int uiaPollCount = 0;
                    while (Environment.TickCount64 < uiaDeadline)
                    {
                        linked.Token.ThrowIfCancellationRequested();
                        if (uiaService.ElementExists(hwnd.Value, automationId, name))
                        {
                            return JsonSuccess(new { ok = true, mode = "uia_element", elapsedMs = sw.ElapsedMilliseconds, hwnd, automationId, name });
                        }
                        await Task.Delay(++uiaPollCount <= 10 ? 40 : 120, linked.Token);
                    }
                    return ErrorResult("WAIT_TIMEOUT", "Timed out waiting for matching UIA element.");

                default:
                    return ErrorResult("INVALID_ARGUMENT", $"Invalid wait mode '{mode}'.");
            }
        }
        catch (OperationCanceledException) when (killSwitch.IsTripped)
        {
            return ErrorResult("KILL_SWITCH_ACTIVE", $"Wait aborted by kill-switch ({killSwitch.HotkeyDescription}).");
        }
        catch (KillSwitchTrippedException ex)
        {
            return ErrorResult("KILL_SWITCH_ACTIVE", ex.Message);
        }
        catch (Exception ex)
        {
            return ErrorResult("WAIT_FAILED", ex.Message);
        }
    }

    public sealed record SequenceStep(
        [Description("Step type: 'focus_window', 'mouse', 'keyboard', 'uia_action', or 'wait'.")] string Type,
        [Description("Mouse action ('move','click','double_click','right_click','drag','scroll'), keyboard action ('type_text','key_press','hotkey'), uia_action ('focus','invoke','set_value','select','toggle','expand','collapse'), or wait mode ('delay','window','uia_element').")] string? Action = null,
        [Description("Target X coordinate (for 'mouse').")] double? X = null,
        [Description("Target Y coordinate (for 'mouse').")] double? Y = null,
        [Description("End X coordinate (for 'mouse' drag).")] double? EndX = null,
        [Description("End Y coordinate (for 'mouse' drag).")] double? EndY = null,
        [Description("Scroll delta (for 'mouse' scroll).")] int Delta = 0,
        [Description("Horizontal scroll (for 'mouse' scroll).")] bool Horizontal = false,
        [Description("Coordinate space: 'image' (default) or 'screen'.")] string CoordinateSpace = "image",
        [Description("Optional captureId for 'image' coordinates.")] string? CaptureId = null,
        [Description("Text for keyboard 'type_text' or value for uia_action 'set_value'.")] string? Text = null,
        [Description("Single key name for keyboard 'key_press'.")] string? Key = null,
        [Description("Key list for keyboard 'hotkey'.")] string[]? Keys = null,
        [Description("Mask text in audit log when true.")] bool Sensitive = false,
        [Description("Target window HWND (for 'focus_window' or 'wait' uia_element).")] long? Hwnd = null,
        [Description("Target PID (for 'focus_window').")] int? Pid = null,
        [Description("Target UIA elementId (for 'uia_action').")] string? ElementId = null,
        [Description("Delay in ms (for 'wait' delay).")] int Ms = 250,
        [Description("Timeout in ms (for 'wait' window/uia_element).")] int TimeoutMs = 10000,
        [Description("Process name filter (for 'wait' window).")] string? ProcessName = null,
        [Description("Title regex (for 'wait' window).")] string? TitlePattern = null,
        [Description("UIA AutomationId (for 'wait' uia_element).")] string? AutomationId = null,
        [Description("UIA Name (for 'wait' uia_element).")] string? Name = null);

    [McpServerTool(Name = "act_and_observe")]
    [Description("Performs one action and then returns a fresh observation of the affected window. Use for exploratory work where the next step depends on the result.")]
    public async Task<CallToolResult> ActAndObserve(
        [Description("One action step: type focus_window, mouse, keyboard, or uia_action. Do not use wait here.")] SequenceStep step,
        [Description("Optional window HWND to observe after the action. If omitted, the action target or new foreground window is used.")] long? observationHwnd = null,
        [Description("Include a screenshot after the action. Default is true.")] bool includeScreenshot = true,
        [Description("Include a compact UIA tree after the action. Default is true.")] bool includeUia = true,
        [Description("Maximum screenshot width in pixels. Default is 1280.")] int screenshotMaxWidth = 1280,
        [Description("Maximum UIA tree depth (0-16). Default is 3.")] int maxDepth = 3,
        [Description("Maximum UIA nodes (1-2000). Default is 150.")] int maxNodes = 150,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        if (step is null || string.Equals(step.Type?.Trim(), "wait", StringComparison.OrdinalIgnoreCase))
            return ErrorResult("INVALID_ARGUMENT", "act_and_observe requires one action step (focus_window, mouse, keyboard, or uia_action), not wait.");
        if (!includeScreenshot && !includeUia)
            return ErrorResult("INVALID_ARGUMENT", "At least one of includeScreenshot or includeUia must be true.");

        long? foregroundBefore = WindowService.GetForegroundWindowInfo()?.Hwnd;
        long? targetHwnd = observationHwnd ?? ResolveActionTargetHwnd(step, foregroundBefore);

        try
        {
            killSwitch.EnsureActive();
            var actionResult = await ExecuteSequenceStepAsync(step, 0, cancellationToken);
            string actionText = actionResult.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text ?? "{}";
            if (actionResult.IsError == true)
                return actionResult;

            long? foregroundAfter = WindowService.GetForegroundWindowInfo()?.Hwnd;
            if (!observationHwnd.HasValue && foregroundAfter.HasValue && foregroundAfter != foregroundBefore)
                targetHwnd = foregroundAfter;

            WindowInfo? targetWin = targetHwnd.HasValue ? WindowService.GetWindowInfo(targetHwnd.Value) : null;
            var observationStartedUtc = DateTimeOffset.UtcNow;
            UiaTreeResult? tree = null;
            ScreenshotResult? shot = null;
            string? uiaError = null;
            string? screenshotError = null;

            if (includeUia)
            {
                if (!targetHwnd.HasValue)
                    uiaError = "No target window was identified for the post-action UIA observation.";
                else
                {
                    try
                    {
                        tree = uiaService.GetTree(targetHwnd.Value, maxDepth, maxNodes, interactiveOnly: true);
                    }
                    catch (Exception ex)
                    {
                        uiaError = ex.Message;
                    }
                }
            }

            if (includeScreenshot)
            {
                try
                {
                    shot = targetHwnd.HasValue
                        ? captureService.Capture(CaptureTarget.Window, null, targetHwnd, screenshotMaxWidth, CaptureFormat.Jpeg)
                        : captureService.Capture(CaptureTarget.Screen, null, null, screenshotMaxWidth, CaptureFormat.Jpeg);
                }
                catch (Exception ex)
                {
                    screenshotError = ex.Message;
                }
            }

            JsonElement actionPayload;
            try { actionPayload = JsonSerializer.Deserialize<JsonElement>(actionText); }
            catch { actionPayload = JsonSerializer.SerializeToElement(new { result = actionText }); }

            var auditWrite = auditLogger.Log(new AuditEntry(DateTimeOffset.UtcNow, "act_and_observe", "Allowed",
                targetWin?.ProcessName, targetHwnd,
                new { step.Type, step.Action, includeScreenshot, includeUia, tree?.TotalNodes, screenshotBytes = shot?.ImageBytes.Length },
                sw.ElapsedMilliseconds, uiaError ?? screenshotError));

            string metadataJson = JsonSerializer.Serialize(new
            {
                action = actionPayload,
                window = targetWin,
                observationStartedUtc,
                observationDurationMs = sw.Elapsed.TotalMilliseconds,
                uiaTree = tree,
                uiaError,
                screenshotError,
                screenshot = shot is null ? null : new
                {
                    captureId = shot.Transform.CaptureId,
                    originX = shot.Transform.OriginX,
                    originY = shot.Transform.OriginY,
                    captureWidth = shot.Transform.CaptureWidth,
                    captureHeight = shot.Transform.CaptureHeight,
                    imageWidth = shot.Transform.ImageWidth,
                    imageHeight = shot.Transform.ImageHeight,
                    scaleX = shot.Transform.ScaleX,
                    scaleY = shot.Transform.ScaleY,
                    performance = shot.Diagnostics
                },
                auditWrite
            }, JsonOptions);

            return new CallToolResult
            {
                Content = shot is null
                    ? [new TextContentBlock { Text = metadataJson }]
                    : [ImageContentBlock.FromBytes(shot.ImageBytes, shot.MimeType), new TextContentBlock { Text = metadataJson }]
            };
        }
        catch (KillSwitchTrippedException ex)
        {
            return ErrorResult("KILL_SWITCH_ACTIVE", ex.Message);
        }
        catch (Exception ex)
        {
            return ErrorResult("ACTION_FAILED", ex.Message);
        }
    }

    [McpServerTool(Name = "run_sequence")]
    [Description("Executes multiple computer-use steps (focus_window, mouse, keyboard, uia_action, wait) in a single fast call, enforcing policy, UIPI, and the F10 kill-switch on every step, with an optional final screenshot.")]
    public async Task<CallToolResult> RunSequence(
        [Description("Ordered list of steps to execute.")] SequenceStep[] steps,
        [Description("If true, captures and returns a final screenshot after all steps complete.")] bool captureFinalScreenshot = false,
        [Description("Final screenshot format: 'jpeg' (default) or 'png'.")] string screenshotFormat = "jpeg",
        [Description("Final screenshot maxWidth. Default is 1280.")] int screenshotMaxWidth = 1280,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        if (steps is null || steps.Length == 0)
            return ErrorResult("INVALID_ARGUMENT", "At least one step is required.");

        var stepResults = new List<object>(steps.Length);

        for (int i = 0; i < steps.Length; i++)
        {
            var step = steps[i];
            string stepType = step.Type.Trim().ToLowerInvariant();
            CallToolResult res = await ExecuteSequenceStepAsync(step, i, cancellationToken);

            string payloadText = res.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text ?? "{}";
            if (res.IsError == true)
            {
                auditLogger.Log(new AuditEntry(DateTimeOffset.UtcNow, "run_sequence", "Error", null, null,
                    new { failedStep = i, step.Type, step.Action }, sw.ElapsedMilliseconds, payloadText));

                return ErrorResult("SEQUENCE_STEP_FAILED", $"Sequence stopped at step {i} ({step.Type}): {payloadText}");
            }

            stepResults.Add(new { step = i, type = stepType, result = JsonSerializer.Deserialize<JsonElement>(payloadText) });
        }

        auditLogger.Log(new AuditEntry(DateTimeOffset.UtcNow, "run_sequence", "Allowed", null, null,
            new { count = steps.Length, captureFinalScreenshot }, sw.ElapsedMilliseconds));

        if (!captureFinalScreenshot)
        {
            return JsonSuccess(new { ok = true, executedSteps = steps.Length, elapsedMs = sw.ElapsedMilliseconds, results = stepResults });
        }

        if (!Enum.TryParse<CaptureFormat>(screenshotFormat, ignoreCase: true, out var fmt))
            fmt = CaptureFormat.Jpeg;

        var shot = captureService.Capture(CaptureTarget.Screen, null, null, screenshotMaxWidth, fmt);
        string summaryJson = JsonSerializer.Serialize(new
        {
            ok = true,
            executedSteps = steps.Length,
            elapsedMs = sw.ElapsedMilliseconds,
            results = stepResults,
            captureId = shot.Transform.CaptureId,
            originX = shot.Transform.OriginX,
            originY = shot.Transform.OriginY,
            captureWidth = shot.Transform.CaptureWidth,
            captureHeight = shot.Transform.CaptureHeight,
            imageWidth = shot.Transform.ImageWidth,
            imageHeight = shot.Transform.ImageHeight,
            scaleX = shot.Transform.ScaleX,
            scaleY = shot.Transform.ScaleY
        }, JsonOptions);

        return new CallToolResult
        {
            Content =
            [
                ImageContentBlock.FromBytes(shot.ImageBytes, shot.MimeType),
                new TextContentBlock { Text = summaryJson }
            ]
        };
    }

    private (int ScreenX, int ScreenY) ResolveCoordinates(double x, double y, string coordinateSpace, string? captureId)
    {
        if (string.Equals(coordinateSpace, "screen", StringComparison.OrdinalIgnoreCase))
            return ((int)Math.Round(x), (int)Math.Round(y));

        if (!string.Equals(coordinateSpace, "image", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"Invalid coordinateSpace '{coordinateSpace}'. Expected 'image' or 'screen'.", nameof(coordinateSpace));

        var transform = captureService.GetTransform(captureId)
            ?? throw new InvalidOperationException("No screenshot transform available. Call 'screenshot' first or use coordinateSpace='screen'.");

        return transform.ImageToScreen(x, y);
    }

    private async Task<CallToolResult> ExecuteSequenceStepAsync(SequenceStep step, int index, CancellationToken cancellationToken)
    {
        string stepType = step.Type.Trim().ToLowerInvariant();
        return stepType switch
        {
            "focus_window" => FocusWindow(step.Hwnd, step.Pid, restoreIfMinimized: true),
            "mouse" => step.X.HasValue && step.Y.HasValue && !string.IsNullOrWhiteSpace(step.Action)
                ? Mouse(step.Action, step.X.Value, step.Y.Value, step.EndX, step.EndY, step.Delta, step.Horizontal, step.CoordinateSpace, step.CaptureId)
                : ErrorResult("INVALID_ARGUMENT", $"Step {index}: mouse requires action, x, and y."),
            "keyboard" => !string.IsNullOrWhiteSpace(step.Action)
                ? Keyboard(step.Action, step.Text, step.Key, step.Keys, step.Sensitive)
                : ErrorResult("INVALID_ARGUMENT", $"Step {index}: keyboard requires action."),
            "uia_action" => !string.IsNullOrWhiteSpace(step.ElementId) && !string.IsNullOrWhiteSpace(step.Action)
                ? UiaAction(step.ElementId, step.Action, step.Text)
                : ErrorResult("INVALID_ARGUMENT", $"Step {index}: uia_action requires elementId and action."),
            "wait" => await Wait(step.Action ?? "delay", step.Ms, step.TimeoutMs, step.ProcessName, step.TitlePattern, step.Hwnd, step.AutomationId, step.Name, cancellationToken),
            _ => ErrorResult("INVALID_ARGUMENT", $"Step {index}: unknown type '{step.Type}'.")
        };
    }

    private long? ResolveActionTargetHwnd(SequenceStep step, long? foregroundHwnd)
    {
        string stepType = step.Type.Trim().ToLowerInvariant();
        switch (stepType)
        {
            case "focus_window":
                if (step.Hwnd.HasValue)
                    return step.Hwnd;
                return step.Pid.HasValue
                    ? WindowService.ListWindows(includeMinimized: true).FirstOrDefault(w => w.Pid == step.Pid.Value)?.Hwnd
                    : null;
            case "uia_action":
                if (!string.IsNullOrWhiteSpace(step.ElementId))
                {
                    try { return uiaService.GetOwnerWindowHwnd(step.ElementId); }
                    catch { return step.Hwnd; }
                }
                return step.Hwnd;
            case "mouse":
                if (step.X.HasValue && step.Y.HasValue)
                {
                    try
                    {
                        var (screenX, screenY) = ResolveCoordinates(step.X.Value, step.Y.Value, step.CoordinateSpace, step.CaptureId);
                        return WindowService.GetWindowAtPoint(screenX, screenY)?.Hwnd ?? step.Hwnd;
                    }
                    catch { return step.Hwnd; }
                }
                return step.Hwnd;
            case "keyboard":
                return foregroundHwnd ?? step.Hwnd;
            default:
                return step.Hwnd ?? foregroundHwnd;
        }
    }

    private static bool TryParseMouseAction(string action, out MouseActionKind kind)
    {
        switch (action.Trim().ToLowerInvariant())
        {
            case "move": kind = MouseActionKind.Move; return true;
            case "click": kind = MouseActionKind.Click; return true;
            case "double_click": kind = MouseActionKind.DoubleClick; return true;
            case "right_click": kind = MouseActionKind.RightClick; return true;
            case "drag": kind = MouseActionKind.Drag; return true;
            case "scroll": kind = MouseActionKind.Scroll; return true;
            default: kind = default; return false;
        }
    }

    private static bool TryParseUiaAction(string action, out UiaActionKind kind)
    {
        switch (action.Trim().ToLowerInvariant())
        {
            case "focus": kind = UiaActionKind.Focus; return true;
            case "invoke": kind = UiaActionKind.Invoke; return true;
            case "set_value": kind = UiaActionKind.SetValue; return true;
            case "select": kind = UiaActionKind.Select; return true;
            case "toggle": kind = UiaActionKind.Toggle; return true;
            case "expand": kind = UiaActionKind.Expand; return true;
            case "collapse": kind = UiaActionKind.Collapse; return true;
            default: kind = default; return false;
        }
    }

    private static CallToolResult JsonSuccess(object payload) => new()
    {
        Content = [new TextContentBlock { Text = JsonSerializer.Serialize(payload, JsonOptions) }]
    };

    private static CallToolResult ErrorResult(string code, string message) => new()
    {
        IsError = true,
        Content = [new TextContentBlock { Text = JsonSerializer.Serialize(new { error = code, message }, JsonOptions) }]
    };
}
