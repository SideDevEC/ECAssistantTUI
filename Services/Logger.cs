namespace ECAssistant.Services;

using ECAssistant.Interfaces;

/// <summary>
/// Lightweight structured logger — writes to file only.
/// No console, no GUI, no color dependency. Headless.
/// </summary>
public class Logger : ILogger
{
    private string _logFilePath = "ECAssistant.log";
    private LogLevel _minLevel = LogLevel.Info;
    private readonly object _lock = new();
    private bool _initialized = false;

    public Logger() { }

    public Logger(string logFilePath, LogLevel minLevel = LogLevel.Info)
    {
        Initialize(logFilePath, minLevel);
    }

    public void Initialize(string logFilePath, LogLevel minLevel)
    {
        _logFilePath = Path.GetFullPath(logFilePath);
        _minLevel = minLevel;
        _initialized = true;

        var dir = Path.GetDirectoryName(_logFilePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        Log(LogLevel.Info, "Logger", $"Logging initialized — file: {_logFilePath}, level: {_minLevel}");
    }

    public void SetLevel(LogLevel level) => _minLevel = level;
    public bool IsDebugEnabled => _minLevel <= LogLevel.Debug;

    public void Debug(string tag, string message) => Log(LogLevel.Debug, tag, message);
    public void Info(string tag, string message) => Log(LogLevel.Info, tag, message);
    public void Warn(string tag, string message) => Log(LogLevel.Warn, tag, message);
    public void Error(string tag, string message) => Log(LogLevel.Error, tag, message);

    public void Error(string tag, string message, Exception ex)
    {
        Log(LogLevel.Error, tag, $"{message} | {ex.GetType().Name}: {ex.Message}");
        if (ex.InnerException != null)
            Log(LogLevel.Error, tag, $"  Inner: {ex.InnerException.Message}");
    }

    private void Log(LogLevel level, string tag, string message)
    {
        if (!_initialized) return;
        if (level < _minLevel) return;

        var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
        var levelStr = level switch
        {
            LogLevel.Debug => "DBG",
            LogLevel.Info  => "INF",
            LogLevel.Warn  => "WRN",
            LogLevel.Error => "ERR",
            _ => "???"
        };
        var line = $"[{timestamp}] [{levelStr}] [{tag}] {message}";

        lock (_lock)
        {
            try { File.AppendAllText(_logFilePath, line + Environment.NewLine); }
            catch { }
        }
    }

    public string GetRecentLines(int count = 50)
    {
        if (!File.Exists(_logFilePath)) return "(No log file found.)";
        try
        {
            var lines = File.ReadAllLines(_logFilePath);
            var start = Math.Max(0, lines.Length - count);
            return string.Join(Environment.NewLine, lines[start..]);
        }
        catch (Exception ex) { return $"Error reading log: {ex.Message}"; }
    }

    public string LogFilePath => _logFilePath;
    public long LogFileSize => File.Exists(_logFilePath) ? new FileInfo(_logFilePath).Length : 0;
}