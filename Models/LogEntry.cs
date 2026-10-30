using System;
using Serilog.Events;

namespace DevDesk.Models;

/// <summary>
/// One log event as the Diagnostics page shows it: already rendered and already
/// redacted, with no reference to the <c>LogEvent</c> it came from.
/// <para>
/// Deliberately a plain snapshot. Holding the Serilog event would keep its
/// argument objects alive — a scan snapshot, a project model — and a 2000-entry
/// ring would then pin the whole application state in memory.
/// </para>
/// </summary>
public sealed record LogEntry(DateTimeOffset Timestamp, LogEventLevel Level, string Message, string? Exception = null)
{
    /// <summary>Three letters, matching the file sink's template so a copied line is greppable.</summary>
    public string LevelText => Level switch
    {
        LogEventLevel.Verbose => "VRB",
        LogEventLevel.Debug => "DBG",
        LogEventLevel.Information => "INF",
        LogEventLevel.Warning => "WRN",
        LogEventLevel.Error => "ERR",
        LogEventLevel.Fatal => "FTL",
        _ => "???",
    };

    /// <summary>The same shape the log file uses, so copy-visible is paste-ready.</summary>
    public string Line => Exception is null
        ? $"[{Timestamp:yyyy-MM-dd HH:mm:ss.fff}] [{LevelText}] {Message}"
        : $"[{Timestamp:yyyy-MM-dd HH:mm:ss.fff}] [{LevelText}] {Message}{Environment.NewLine}{Exception}";

    public bool IsProblem => Level >= LogEventLevel.Warning;
}
