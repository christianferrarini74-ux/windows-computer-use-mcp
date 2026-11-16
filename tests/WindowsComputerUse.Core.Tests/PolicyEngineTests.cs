using WindowsComputerUse.Core.Dpi;
using WindowsComputerUse.Core.Security;
using WindowsComputerUse.Core.Win32;

namespace WindowsComputerUse.Core.Tests;

public sealed class PolicyEngineTests
{
    private static WindowInfo MakeWindow(string process, string title, bool elevated = false) => new(
        Hwnd: 12345,
        Pid: 999,
        ProcessName: process,
        Title: title,
        ClassName: "TestClass",
        State: WindowVisualState.Normal,
        IsElevated: elevated,
        IsForeground: true,
        BoundingRect: new RectI(0, 0, 800, 600));

    [Fact]
    public void Allowlist_BlocksNullAndUnlisted_AllowsMatchingRule()
    {
        var engine = new PolicyEngine(new PolicyConfig(
            PolicyMode.Allowlist,
            [
                new PolicyRule("notepad", ".*Test.*"),
                new PolicyRule("MyApp.exe")
            ]));

        Assert.False(engine.Evaluate(null).Allowed);
        Assert.False(engine.Evaluate(MakeWindow("chrome.exe", "Google")).Allowed);
        Assert.False(engine.Evaluate(MakeWindow("notepad.exe", "Untitled")).Allowed);
        Assert.True(engine.Evaluate(MakeWindow("notepad.exe", "My Test File")).Allowed);
        Assert.True(engine.Evaluate(MakeWindow("MyApp.exe", "Anything")).Allowed);
    }

    [Fact]
    public void Denylist_BlocksMatching_AllowsOthers()
    {
        var engine = new PolicyEngine(new PolicyConfig(
            PolicyMode.Denylist,
            [new PolicyRule("powershell.exe")]));

        Assert.False(engine.Evaluate(MakeWindow("powershell.exe", "Admin")).Allowed);
        Assert.True(engine.Evaluate(MakeWindow("notepad.exe", "Notes")).Allowed);
    }

    [Fact]
    public void ElevatedTarget_IsBlockedByUipi_WhenServerNotElevated()
    {
        if (WindowService.IsCurrentProcessElevated)
            return;

        var engine = new PolicyEngine(new PolicyConfig(PolicyMode.Unrestricted));
        var decision = engine.Evaluate(MakeWindow("taskmgr.exe", "Task Manager", elevated: true));

        Assert.False(decision.Allowed);
        Assert.Equal("UIPI_ELEVATION_REQUIRED", decision.Code);
    }

    [Fact]
    public void KillSwitch_TripAndReset_ControlsStateAndCancellation()
    {
        bool killed = false;
        using var ks = new KillSwitchService(onKilled: () => killed = true);
        Assert.False(ks.IsActive);
        Assert.False(ks.IsTripped);
        Assert.Equal("F10", ks.HotkeyDescription);

        var token = ks.Token;
        ks.Trip("test");

        Assert.True(ks.IsTripped);
        Assert.True(token.IsCancellationRequested);
        Assert.Throws<KillSwitchTrippedException>(() => ks.ThrowIfTripped());

        SpinWait.SpinUntil(() => killed, TimeSpan.FromSeconds(1));
        Assert.True(killed);

        ks.Reset();
        Assert.False(ks.IsTripped);
        ks.ThrowIfTripped();
    }
}
