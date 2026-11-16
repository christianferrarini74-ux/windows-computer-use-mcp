using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace WindowsComputerUse.Core.Security;

public sealed record AuditEntry(
    DateTimeOffset TimestampUtc,
    string Tool,
    string Decision,
    string? TargetProcess,
    long? TargetHwnd,
    object? Parameters,
    long DurationMs,
    string? Error = null);

public sealed record AuditWriteDiagnostics(
    double TotalMs,
    double SerializeMs,
    double AppendMs,
    int LineBytes,
    bool Succeeded);

/// <summary>
/// Writes structured JSONL audit records for all computer-use actions.
/// </summary>
public sealed class AuditLogger
{
    private readonly string _logDirectory;
    private readonly Lock _writeLock = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public AuditLogger(string? logDirectory = null)
    {
        _logDirectory = logDirectory
            ?? Environment.GetEnvironmentVariable("WCU_AUDIT_DIR")
            ?? Path.Combine(AppContext.BaseDirectory, "logs");
    }

    public string LogDirectory => _logDirectory;

    public AuditWriteDiagnostics Log(AuditEntry entry)
    {
        var totalTimer = Stopwatch.StartNew();
        double serializeMs = 0;
        double appendMs = 0;
        int lineBytes = 0;
        try
        {
            Directory.CreateDirectory(_logDirectory);
            string filePath = Path.Combine(_logDirectory, $"audit-{entry.TimestampUtc:yyyyMMdd}.jsonl");
            var serializeTimer = Stopwatch.StartNew();
            string line = JsonSerializer.Serialize(entry, JsonOptions) + Environment.NewLine;
            serializeMs = serializeTimer.Elapsed.TotalMilliseconds;
            lineBytes = Encoding.UTF8.GetByteCount(line);

            lock (_writeLock)
            {
                var appendTimer = Stopwatch.StartNew();
                File.AppendAllText(filePath, line);
                appendMs = appendTimer.Elapsed.TotalMilliseconds;
            }

            return new AuditWriteDiagnostics(totalTimer.Elapsed.TotalMilliseconds, serializeMs, appendMs, lineBytes, true);
        }
        catch
        {
            // Never crash tool execution if disk logging fails, e.g. read-only install dir.
            return new AuditWriteDiagnostics(totalTimer.Elapsed.TotalMilliseconds, serializeMs, appendMs, lineBytes, false);
        }
    }
}
