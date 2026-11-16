# Windows Computer Use MCP

> *Documentazione in italiano: [README.it.md](README.it.md)*

Local Windows MCP server for controlling the desktop from any compatible MCP client. Built in C# / .NET 10 over the `stdio` transport, it combines two complementary layers: DPI-aware visual capture + physical Win32 input, and semantic UI Automation (UIA3) inspection and control.

## Features

- Visible top-level window enumeration and activation with `HWND`, `PID`, process name, title, visual state, elevation status, and physical bounding rectangle.
- Per-Monitor v2 DPI-aware screenshots of the virtual screen, a specific monitor, a target window, or a physical screen region, returning exact `image <-> screen` coordinate transform metadata.
- Physical mouse and keyboard control via Win32 `SendInput` (smooth cursor movement, click, double-click, right-click, drag, scroll, Unicode text, key press, and hotkeys).
- Compact, filterable UIA3 tree inspection with supported patterns, `LiveSetting`, automatic retry on transient COM errors (`0x80040201`), and explicit `STALE_ELEMENT` errors for expired `elementId`s.
- Semantic UIA actions (`focus`, `invoke`, `set_value`, `select`, `toggle`, `expand`, `collapse`), serialized internally to prevent WinUI UI-thread race conditions.
- `launch_app` to start desktop applications in the interactive user session; `wait` for delays, windows, or UIA elements; `act_and_observe` for single-step feedback; `run_sequence` for multi-step batching.
- JSONL audit logging, configurable `Allowlist` / `Denylist` / `Unrestricted` policy engine, UIPI elevation checks, and global **F10** kill-switch.
- On-demand top-center overlay banner (`Computer Use — F10 to quit`) and persistent click-through cyan pointer halo while active.

## MCP Tools

| Tool | Purpose |
|---|---|
| `list_windows` | List visible windows and obtain `HWND`/`PID`. Supports process and title filters; minimized windows are excluded by default. |
| `screenshot` | Capture `screen`, `monitor`, `window`, or `region`; supports JPEG (default) and PNG, returning coordinate transform metadata. |
| `observe_window` | Recommended default inspection: captures a target window screenshot and compact UIA tree in a single call. |
| `get_uia_tree` | Dedicated UIA3 inspection: depth/node limits, subtree via `rootElementId`, focused element, offscreen toggle, and `interactiveOnly` pruning. Returns `STALE_ELEMENT` if `rootElementId` is no longer valid. |
| `launch_app` | Launch an executable, document, or shell URI in the interactive user session (routing through `explorer.exe` if elevated) and optionally wait for its window. |
| `focus_window` | Restore (if minimized) and bring a target window to the foreground by `HWND` or `PID`. |
| `mouse` | Physical mouse action (`move`, `click`, `double_click`, `right_click`, `drag`, `scroll`) in `image` (default) or `screen` coordinates. |
| `keyboard` | Send Unicode text (`type_text`), single keys (`key_press`), or combinations (`hotkey`); `sensitive: true` redacts text in the audit log. |
| `uia_action` | Execute a UIA pattern (`focus`, `invoke`, `set_value`, `select`, `toggle`, `expand`, `collapse`) on an `elementId`. |
| `wait` | Wait for a fixed `delay`, a `window` (by process/title), or a `uia_element` (by `HWND` and `automationId`/`name`). |
| `act_and_observe` | Perform one action and immediately return an updated observation of the affected window. |
| `run_sequence` | Execute an ordered list of steps (`focus_window`, `mouse`, `keyboard`, `uia_action`, `wait`) in one call with optional final screenshot, enforcing policy and F10 kill-switch on every step. |

Typical exploration workflow: `list_windows` → `observe_window` → action(s). Use `run_sequence` when multiple next steps are already known, and `act_and_observe` when the next step depends on the immediate UI outcome.

## Requirements & Build

- Windows 10/11 and .NET 10 SDK.
- Runs in the same interactive Windows desktop session as the target applications.

```powershell
dotnet build .\src\WindowsComputerUse.McpServer\WindowsComputerUse.McpServer.csproj
dotnet test .\tests\WindowsComputerUse.Core.Tests\WindowsComputerUse.Core.Tests.csproj
```

To publish a self-contained/distributable folder:

```powershell
dotnet publish .\src\WindowsComputerUse.McpServer\WindowsComputerUse.McpServer.csproj -c Release -o .\out\ReleaseInteractive
```

Register the full path to `WindowsComputerUse.McpServer.exe` in your MCP client.

## Configuration

By default, if no configuration is provided, the server starts with an empty **`Allowlist`** policy. It looks for `computer-use.policy.json` in the current working directory, then next to the executable, or at `WCU_POLICY_FILE`. See [`computer-use.policy.example.json`](computer-use.policy.example.json) for a starter template.

Rule format:

```json
{
  "mode": "Allowlist",
  "rules": [
    { "processName": "notepad.exe" },
    { "titlePattern": ".*Test Bench.*" }
  ]
}
```

Modes: `Allowlist` permits only matching processes/titles; `Denylist` blocks matching rules; `Unrestricted` allows all non-elevated windows. Process names without `.exe` are normalized automatically. UIPI still blocks interaction with elevated windows unless the MCP server itself is elevated.

Environment variables:

| Variable | Description |
|---|---|
| `WCU_POLICY_MODE` | Overrides policy mode: `Allowlist`, `Denylist`, or `Unrestricted`. |
| `WCU_POLICY_FILE` | Explicit path to the JSON policy file. |
| `WCU_ALLOW_PROCESSES` | Comma-separated process names appended to the loaded rules. |
| `WCU_AUDIT_DIR` | Directory for `audit-YYYYMMDD.jsonl` logs (defaults to `logs/` next to the executable). |

### MCP Client Registration Example

Example TOML entry for clients using `config.toml` (adjust paths as needed):

```toml
[mcp_servers.windows-computer-use]
command = 'D:\\Coding\\C#\\Computer_Use\\out\\ReleaseInteractive\\WindowsComputerUse.McpServer.exe'
args = []

[mcp_servers.windows-computer-use.env]
WCU_POLICY_FILE = 'D:\\Coding\\C#\\Computer_Use\\computer-use.policy.json'
WCU_POLICY_MODE = 'Allowlist'
```

Reload or restart your MCP client after updating the config and verify that all 12 tools appear.

## Kill-Switch, Banner & Pointer

Press **F10** at any time to immediately abort Computer Use. The kill-switch closes the overlay windows and terminates the MCP server process; reload/restart the server in your MCP client to resume. While active, the top-center banner displays the hotkey and a click-through cyan halo highlights the agent-controlled pointer position.

## Test Bench

A disposable WPF test application and manual test guide are available in [`test/README.md`](test/README.md).

## Repository Structure

- `src/WindowsComputerUse.Core`: DPI awareness, screen capture, Win32 input/windows, UIA3 service, policy engine, kill-switch, and audit logger.
- `src/WindowsComputerUse.McpServer`: `stdio` MCP host, tool definitions, and PerMonitorV2 application manifest.
- `tests/WindowsComputerUse.Core.Tests`: xUnit test suite for Core.
- `test`: disposable WPF Test Bench application and guide.
- `computer-use.policy.example.json`: sample allowlist configuration.
