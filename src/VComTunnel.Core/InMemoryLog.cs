using System.Collections.Concurrent;
using System.Text;

namespace VComTunnel.Core;

public sealed class InMemoryLog : IDisposable
{
    private const int MaxEntries = 1000;
    private static readonly object FileLock = new();

    private readonly ConcurrentQueue<LogEntry> _entries = new();
    private readonly ConcurrentQueue<LogEntry> _pendingFileEntries = new();
    private int _entryCount;
    private int _fileFlushScheduled;
    private int _disposed;

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
            Interlocked.Decrement(ref _entryCount);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        FlushPendingFileEntries();
    }

    private void Add(string level, string source, string message)
    {
        var entry = new LogEntry(DateTimeOffset.UtcNow, level, source, message);
        _entries.Enqueue(entry);
        Interlocked.Increment(ref _entryCount);
        _pendingFileEntries.Enqueue(entry);
        ScheduleFileFlush();
        while (Volatile.Read(ref _entryCount) > MaxEntries && _entries.TryDequeue(out _))
        {
            Interlocked.Decrement(ref _entryCount);
        }
    }

    private void ScheduleFileFlush()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        if (Interlocked.Exchange(ref _fileFlushScheduled, 1) == 0)
        {
            ThreadPool.QueueUserWorkItem(_ => FlushFileQueue());
        }
    }

    private void FlushFileQueue()
    {
        do
        {
            FlushPendingFileEntries();
            Interlocked.Exchange(ref _fileFlushScheduled, 0);
        }
        while (!_pendingFileEntries.IsEmpty && Interlocked.Exchange(ref _fileFlushScheduled, 1) == 0);
    }

    private void FlushPendingFileEntries()
    {
        try
        {
            var builder = new StringBuilder();
            while (_pendingFileEntries.TryDequeue(out var entry))
            {
                builder.Append(entry.Timestamp.ToString("O"));
                builder.Append(' ');
                builder.Append(entry.Level.PadRight(5));
                builder.Append(' ');
                builder.Append(entry.Source);
                builder.Append(": ");
                builder.Append(entry.Message);
                builder.AppendLine();
            }

            if (builder.Length == 0)
            {
                return;
            }

            Directory.CreateDirectory(AppPaths.LogsDirectory);
            lock (FileLock)
            {
                File.AppendAllText(Path.Combine(AppPaths.LogsDirectory, "service.log"), builder.ToString());
            }
        }
        catch
        {
        }
    }
}
