using System.Collections.Concurrent;
using System.Text;

namespace MC1.Core.Services;

public enum LogLevel { Debug, Info, Warning, Error }

public sealed record LogEntry(DateTimeOffset Time, LogLevel Level, string Category, string Message)
{
    public override string ToString() => $"{Time.LocalDateTime:yyyy-MM-dd HH:mm:ss.fff} [{Level}] {Category}: {Message}";
}

/// <summary>In-memory ring buffer + rolling daily log file (replaces os.Logger / DebugLogBuffer).</summary>
public sealed class AppLog
{
    private readonly ConcurrentQueue<LogEntry> _entries = new();
    private readonly object _fileLock = new();
    private const int Capacity = 5000;

    public bool DebugEnabled { get; set; }
    public event Action<LogEntry>? EntryAdded;

    public void Debug(string category, string message) { if (DebugEnabled) Add(LogLevel.Debug, category, message); }
    public void Info(string category, string message) => Add(LogLevel.Info, category, message);
    public void Warn(string category, string message) => Add(LogLevel.Warning, category, message);
    public void Error(string category, string message) => Add(LogLevel.Error, category, message);

    private void Add(LogLevel level, string category, string message)
    {
        var e = new LogEntry(DateTimeOffset.Now, level, category, message);
        _entries.Enqueue(e);
        while (_entries.Count > Capacity && _entries.TryDequeue(out _)) { }
        try
        {
            lock (_fileLock)
            {
                Directory.CreateDirectory(AppPaths.Logs);
                File.AppendAllText(Path.Combine(AppPaths.Logs, $"mc1-{DateTime.Now:yyyyMMdd}.log"), e + Environment.NewLine);
            }
        }
        catch { /* logging must never throw */ }
        EntryAdded?.Invoke(e);
    }

    public IReadOnlyList<LogEntry> Snapshot() => _entries.ToArray();

    public string Export()
    {
        var sb = new StringBuilder();
        foreach (var e in _entries) sb.AppendLine(e.ToString());
        return sb.ToString();
    }

    public void PruneOldFiles(int keepDays = 7)
    {
        try
        {
            if (!Directory.Exists(AppPaths.Logs)) return;
            foreach (var f in Directory.GetFiles(AppPaths.Logs, "mc1-*.log"))
                if (File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddDays(-keepDays)) File.Delete(f);
        }
        catch { /* ignore */ }
    }
}

/// <summary>Implemented by the UI layer to surface Windows notifications.</summary>
public interface INotifier
{
    void ShowMessage(string title, string body, string conversationKey, bool allowReply);
    void ShowInfo(string title, string body, string? actionKey = null);
    void UpdateBadge(int unread);
}

public sealed class NullNotifier : INotifier
{
    public void ShowMessage(string title, string body, string conversationKey, bool allowReply) { }
    public void ShowInfo(string title, string body, string? actionKey = null) { }
    public void UpdateBadge(int unread) { }
}
