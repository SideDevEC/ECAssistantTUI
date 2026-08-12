using System.IO;

namespace ECAssistant.Services;

/// <summary>
/// File Watcher — monitors the workspace directory for changes and raises events.
/// Lets the agent react to file changes (e.g., build output, file modifications).
/// 
/// Usage:
///   var watcher = new FileWatcherService(workingDir);
///   watcher.OnChanged += (path, changeType) => { ... };
///   watcher.Start();
///   watcher.Stop();
/// </summary>
public class FileWatcherService : IDisposable
{
    private readonly FileSystemWatcher? _watcher;
    private readonly string _watchPath;
    private bool _running = false;

    /// <summary>Event raised when a file changes. Returns (path, changeType).</summary>
    public event Action<string, FileChangeType>? OnFileChanged;

    /// <summary>Recent changes (for polling instead of events).</summary>
    private readonly Queue<FileChange> _recentChanges = new();
    private readonly object _changeLock = new();
    private const int MaxQueueSize = 50;

    public FileWatcherService(string watchPath, string filter = "*.*")
    {
        _watchPath = watchPath;

        if (!Directory.Exists(watchPath))
        {
            Logger.Warn("FileWatcher", $"Directory not found: {watchPath}");
            return;
        }

        _watcher = new FileSystemWatcher(watchPath, filter)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            EnableRaisingEvents = false
        };

        _watcher.Changed += (_, e) => EnqueueChange(e.FullPath, FileChangeType.Modified);
        _watcher.Created += (_, e) => EnqueueChange(e.FullPath, FileChangeType.Created);
        _watcher.Deleted += (_, e) => EnqueueChange(e.FullPath, FileChangeType.Deleted);
        _watcher.Renamed += (_, e) => EnqueueChange(e.FullPath, FileChangeType.Renamed);
    }

    /// <summary>Start watching for changes.</summary>
    public void Start()
    {
        if (_watcher == null) return;
        _watcher.EnableRaisingEvents = true;
        _running = true;
        Logger.Info("FileWatcher", $"Watching: {_watchPath}");
    }

    /// <summary>Stop watching.</summary>
    public void Stop()
    {
        if (_watcher == null) return;
        _watcher.EnableRaisingEvents = false;
        _running = false;
        Logger.Info("FileWatcher", "Stopped.");
    }

    /// <summary>Get and clear recent changes (for polling).</summary>
    public List<FileChange> DrainChanges()
    {
        lock (_changeLock)
        {
            var changes = _recentChanges.ToList();
            _recentChanges.Clear();
            return changes;
        }
    }

    /// <summary>Check if there are any pending changes.</summary>
    public bool HasChanges
    {
        get
        {
            lock (_changeLock) { return _recentChanges.Count > 0; }
        }
    }

    /// <summary>Get a summary of recent changes (for LLM consumption).</summary>
    public string GetChangeSummary()
    {
        var changes = DrainChanges();
        if (changes.Count == 0) return "(No file changes detected.)";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"File changes ({changes.Count}):");
        foreach (var c in changes.Take(20))
        {
            var relPath = Path.GetRelativePath(_watchPath, c.Path);
            sb.AppendLine($"  [{c.Type}] {relPath}");
        }
        if (changes.Count > 20)
            sb.AppendLine($"  ... and {changes.Count - 20} more");
        return sb.ToString();
    }

    public bool IsRunning => _running;
    public string WatchPath => _watchPath;

    private void EnqueueChange(string path, FileChangeType type)
    {
        // Skip common noise: bin, obj, .git, temp files
        var ignore = new[] { Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar,
                            Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar,
                            Path.DirectorySeparatorChar + ".git" + Path.DirectorySeparatorChar };
        foreach (var skip in ignore)
        {
            if (path.Contains(skip, StringComparison.OrdinalIgnoreCase)) return;
        }

        lock (_changeLock)
        {
            _recentChanges.Enqueue(new FileChange { Path = path, Type = type, Timestamp = DateTime.UtcNow });
            if (_recentChanges.Count > MaxQueueSize)
                _recentChanges.Dequeue();
        }

        OnFileChanged?.Invoke(path, type);
    }

    public void Dispose()
    {
        Stop();
        _watcher?.Dispose();
    }
}

/// <summary>Type of file change.</summary>
public enum FileChangeType
{
    Created,
    Modified,
    Deleted,
    Renamed
}

/// <summary>A single file change event.</summary>
public class FileChange
{
    public string Path { get; set; } = "";
    public FileChangeType Type { get; set; }
    public DateTime Timestamp { get; set; }
}