using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using WindowsComputerUse.Core.Win32;

namespace WindowsComputerUse.Core.Security;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PolicyMode
{
    Allowlist,
    Denylist,
    Unrestricted
}

public sealed record PolicyRule(
    string? ProcessName = null,
    string? TitlePattern = null);

public sealed record PolicyConfig(
    PolicyMode Mode = PolicyMode.Allowlist,
    IReadOnlyList<PolicyRule>? Rules = null);

public sealed record PolicyDecision(
    bool Allowed,
    string Code,
    string Reason);

/// <summary>
/// Evaluates whether an action against a target window is permitted by the active
/// allowlist/denylist policy and UIPI integrity rules.
/// </summary>
public sealed class PolicyEngine
{
    private readonly PolicyConfig _config;
    private readonly IReadOnlyList<(string? Process, Regex? TitleRegex)> _compiledRules;

    public PolicyEngine(PolicyConfig? config = null)
    {
        _config = config ?? LoadFromEnvironmentOrFile();
        _compiledRules = (_config.Rules ?? [])
            .Select(r => (
                NormalizeProcess(r.ProcessName),
                string.IsNullOrWhiteSpace(r.TitlePattern)
                    ? null
                    : new Regex(r.TitlePattern, RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromMilliseconds(200))))
            .ToList();
    }

    public PolicyMode Mode => _config.Mode;

    public PolicyDecision Evaluate(WindowInfo? target)
    {
        if (target is null)
        {
            return _config.Mode == PolicyMode.Unrestricted
                ? new PolicyDecision(true, "ALLOWED", "Unrestricted mode.")
                : new PolicyDecision(false, "NO_TARGET_WINDOW", "No target window identified at the target location or foreground.");
        }

        if (WindowService.IsBlockedByUipi(target))
        {
            return new PolicyDecision(
                false,
                "UIPI_ELEVATION_REQUIRED",
                $"Target window '{target.Title}' ({target.ProcessName}, PID {target.Pid}) is running elevated, while the MCP server is not. Restart the MCP server elevated if interaction is required.");
        }

        if (_config.Mode == PolicyMode.Unrestricted)
        {
            return new PolicyDecision(true, "ALLOWED", "Unrestricted mode.");
        }

        bool matched = MatchesAnyRule(target);

        if (_config.Mode == PolicyMode.Allowlist)
        {
            return matched
                ? new PolicyDecision(true, "ALLOWED", $"Matched allowlist rule for {target.ProcessName}.")
                : new PolicyDecision(false, "POLICY_BLOCKED", $"Window '{target.Title}' ({target.ProcessName}) is not in the configured allowlist.");
        }

        // Denylist
        return matched
            ? new PolicyDecision(false, "POLICY_BLOCKED", $"Window '{target.Title}' ({target.ProcessName}) matched a denylist rule.")
            : new PolicyDecision(true, "ALLOWED", $"Not in denylist ({target.ProcessName}).");
    }

    private bool MatchesAnyRule(WindowInfo target)
    {
        string? targetProc = NormalizeProcess(target.ProcessName);
        foreach (var (proc, titleRegex) in _compiledRules)
        {
            if (proc is not null && !string.Equals(proc, targetProc, StringComparison.OrdinalIgnoreCase))
                continue;

            if (titleRegex is not null && !titleRegex.IsMatch(target.Title))
                continue;

            if (proc is not null || titleRegex is not null)
                return true;
        }

        return false;
    }

    private static string? NormalizeProcess(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

        string trimmed = name.Trim();
        return trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? trimmed
            : trimmed + ".exe";
    }

    public static PolicyConfig LoadFromEnvironmentOrFile()
    {
        string? envMode = Environment.GetEnvironmentVariable("WCU_POLICY_MODE");
        string? envAllow = Environment.GetEnvironmentVariable("WCU_ALLOW_PROCESSES");
        string? policyPath = Environment.GetEnvironmentVariable("WCU_POLICY_FILE")
            ?? FindExistingPolicyFile();

        PolicyConfig config = new(PolicyMode.Allowlist, []);

        if (!string.IsNullOrWhiteSpace(policyPath) && File.Exists(policyPath))
        {
            string json = File.ReadAllText(policyPath);
            var loaded = JsonSerializer.Deserialize<PolicyConfig>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            if (loaded is not null)
                config = loaded;
        }

        if (!string.IsNullOrWhiteSpace(envMode) && Enum.TryParse<PolicyMode>(envMode, ignoreCase: true, out var parsedMode))
        {
            config = config with { Mode = parsedMode };
        }

        if (!string.IsNullOrWhiteSpace(envAllow))
        {
            var envRules = envAllow
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(p => new PolicyRule(ProcessName: p))
                .ToList();

            var merged = (config.Rules ?? []).Concat(envRules).ToList();
            config = config with { Rules = merged };
        }

        return config;
    }

    private static string? FindExistingPolicyFile()
    {
        string cwdPath = Path.Combine(Environment.CurrentDirectory, "computer-use.policy.json");
        if (File.Exists(cwdPath))
            return cwdPath;

        string basePath = Path.Combine(AppContext.BaseDirectory, "computer-use.policy.json");
        return File.Exists(basePath) ? basePath : null;
    }
}
