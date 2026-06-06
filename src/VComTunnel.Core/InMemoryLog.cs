using System.Collections.Concurrent;

namespace VComTunnel.Core;

public sealed class InMemoryLog
{
    private readonly ConcurrentQueue<LogEntry> _entries = new();

    public void Info(string source, string message) => Add("info", source, message);
    public void Warn(string source, string message) => Add("warn", source, message);
    public void Error(string source, string message) => Add("error", source, message);

    public IReadOnlyList<LogEntry> Snapshot(int max = 500)
    {
        return _entries.Reverse().Take(max).Reverse().ToArray();
    }

    public void Clear()
    {
        while (_entries.TryDequeue(out _))
        {
        }
    }

    private void Add(string level, string source, string message)
    {
        var entry = new LogEntry(DateTimeOffset.UtcNow, level, source, message);
        _entries.Enqueue(entry);
        AppendToFile(entry);
        while (_entries.Count > 1000 && _entries.TryDequeue(out _))
        {
        }
    }

    private static void AppendToFile(LogEntry entry)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.LogsDirectory);
            var line = $"{entry.Timestamp:O} {entry.Level,-5} {entry.Source}: {entry.Message}{Environment.NewLine}";
            File.AppendAllText(Path.Combine(AppPaths.LogsDirectory, "service.log"), line);
        }
        catch
        {
        }
    }
}
