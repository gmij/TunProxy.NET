using Serilog.Core;
using Serilog.Events;
using System.Collections.Concurrent;

namespace TunProxy.CLI;

/// <summary>
/// Serilog 内存 Sink：保留最近 N 条日志，供 Web 控制台实时查看
/// </summary>
public sealed class MemoryLogSink : ILogEventSink
{
    private const int MaxEntries = 500;
    private readonly ConcurrentQueue<LogEntry> _entries = new();
    private long _nextId;

    public static readonly MemoryLogSink Instance = new();

    public void Emit(LogEvent logEvent)
    {
        var id = Interlocked.Increment(ref _nextId);
        _entries.Enqueue(new LogEntry
        {
            Id      = id,
            Time    = logEvent.Timestamp.LocalDateTime.ToString("HH:mm:ss.fff"),
            Level   = MapLevel(logEvent.Level),
            Message = logEvent.RenderMessage(),
            Ex      = logEvent.Exception?.Message
        });
        while (_entries.Count > MaxEntries)
            _entries.TryDequeue(out _);
    }

    /// <summary>返回 Id > afterId 的所有条目（最多 MaxEntries 条）</summary>
    public LogEntry[] GetEntriesAfter(long afterId)
        => _entries.ToArray().Where(e => e.Id > afterId).ToArray();

    private static string MapLevel(LogEventLevel level) => level switch
    {
        LogEventLevel.Debug       => "DBG",
        LogEventLevel.Information => "INF",
        LogEventLevel.Warning     => "WRN",
        LogEventLevel.Error       => "ERR",
        LogEventLevel.Fatal       => "FTL",
        _                         => "VRB"
    };
}

/// <summary>单条日志记录（AOT JSON 兼容）</summary>
public class LogEntry
{
    public long    Id      { get; set; }
    public string  Time    { get; set; } = "";
    public string  Level   { get; set; } = "";
    public string  Message { get; set; } = "";
    public string? Ex      { get; set; }
}
