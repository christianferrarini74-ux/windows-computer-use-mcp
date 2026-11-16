using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WindowsComputerUse.Core.Capture;
using WindowsComputerUse.Core.Dpi;
using WindowsComputerUse.Core.Security;
using WindowsComputerUse.Core.Uia;

DpiAwareness.EnsurePerMonitorV2();

var builder = Host.CreateApplicationBuilder(args);

// MCP stdio transport uses stdout for JSON-RPC; all logs must go to stderr.
builder.Logging.AddConsole(options =>
{
    options.LogToStandardErrorThreshold = LogLevel.Trace;
});

builder.Services.AddSingleton<ScreenCaptureService>();
builder.Services.AddSingleton<UiaService>();
builder.Services.AddSingleton(_ => new PolicyEngine());
builder.Services.AddSingleton(sp =>
{
    var lifetime = sp.GetRequiredService<IHostApplicationLifetime>();
    return new KillSwitchService(onKilled: () =>
    {
        lifetime.StopApplication();
        Thread.Sleep(250);
        Environment.Exit(0);
    });
});
builder.Services.AddSingleton(_ => new AuditLogger());

builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInstructions = """
            For interactive desktop work, use observe_window as the default inspection tool; it returns a screenshot and a compact UI Automation tree together. After one action when the next step depends on the result, use act_and_observe to get the updated state in the same call. Use run_sequence when multiple next actions are already clear from a recent observation. Use launch_app to start desktop apps in the user session. Use the lower-level screenshot, get_uia_tree, mouse, keyboard, and uia_action tools only when their specialized behavior is needed.
            """;
        options.ServerInfo = new()
        {
            Name = "windows-computer-use-mcp",
            Version = "0.1.0"
        };
    })
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();
