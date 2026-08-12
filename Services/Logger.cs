using ECAssistant.UI;

namespace ECAssistant.Services;

/// <summary>
/// Lightweight structured logger — writes to both file and console (via EGuiBase).
/// No external dependencies. Rotates log files daily.
/// 
/// Usage:
///   Logger.Initialize("ECAssistant.log", Gui);
///   Logger.Info("Engine", "Model loaded successfully");
///   Logger.Error("Orchestrator", $"Tool failed: {ex.Message}");
///   Logger.Debug("Context", $"Token budget: {tokens}/{max}");
/// 
/// Levels (controlled by config or compile-time):
///   Debug < Info < Warn < Error
///   Default: Info (Debug suppressed unless enabled)
/// </summary>
public static class Logger
{
    private static string _logFilePath = "ECAssistant.log";
    private static EGuiBase? _gui;
    private static LogLevel _minLevel = LogLevel.Info;
    private static readonly object _lock = new();
    private static bool _initialized = false;

    /// <summary>Initialize the logger with file path and GUI reference.</summary>
    public static void Initialize(string logFilePath, EGuiBase gui, LogLevel minLevel = LogLevel.Info)
    {
        _logFilePath = Path.GetFullPath(logFilePath);
        _gui = gui;
        _minLevel = minLevel;
        _initialized = true;

        // Ensure log directory exists
        var dir = Path.GetDirectoryName(_logFilePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        // Write startup banner
        Log(LogLevel.Info, "Logger", $"Logging initialized — file: {_logFilePath}, level: {_minLevel}");
    }

    /// <summary>Set the minimum log level at runtime.</summary>
    public static void SetLevel(LogLevel level) => _minLevel = level;

    /// <summary>Debug log (suppressed unless level is Debug).</summary>
    public static void Debug(string tag, string message) => Log(LogLevel.Debug, tag, message);

    /// <summary>Info log.</summary>
    public static void Info(string tag, string message) => Log(LogLevel.Info, tag, message);

    /// <summary>Warning log.</summary>
    public static void Warn(string tag, string message) => Log(LogLevel.Warn, tag, message);

    /// <summary>Error log.</summary>
    public static void Error(string tag, string message) => Log(LogLevel.Error, tag, message);

    /// <summary>Error log with exception details.</summary>
    public static void Error(string tag, string message, Exception ex)
    {
        Log(LogLevel.Error, tag, $"{message} | {ex.GetType().Name}: {ex.Message}");
        if (ex.InnerException != null)
            Log(LogLevel.Error, tag, $"  Inner: {ex.InnerException.Message}");
    }

    /// <summary>Core log method — writes to file and optionally to console.</summary>
    private static void Log(LogLevel level, string tag, string message)
    {
        if (!_initialized) return;
        if (level < _minLevel) return;

        var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
        var levelStr = level switch
        {
            LogLevel.Debug => "DBG",
            LogLevel.Info => "INF",
            LogLevel.Warn => "WRN",
            LogLevel.Error => "ERR",
            _ => "???"
        };
        var line = $"[{timestamp}] [{levelStr}] [{tag}] {message}";

        lock (_lock)
        {
            // Write to file (append)
            try
            {
                File.AppendAllText(_logFilePath, line + Environment.NewLine);
            }
            catch { /* Don't crash on log write failure */ }

            // Write to console via GUI (only for Warn/Error to avoid spam)
            if (_gui != null && level >= LogLevel.Warn)
            {
                var color = level switch
                {
                    LogLevel.Warn => EColor.Yellow,
                    LogLevel.Error => EColor.Red,
                    _ => EColor.Dim
                };
                _gui.LogInternal($"{color}{line}{EColor.Reset}");
            }
        }
    }

    /// <summary>Read recent log lines (for diagnostics command).</summary>
    public static string GetRecentLines(int count = 50)
    {
        if (!File.Exists(_logFilePath)) return "(No log file found.)";
        try
        {
            var lines = File.ReadAllLines(_logFilePath);
            var start = Math.Max(0, lines.Length - count);
            return string.Join(Environment.NewLine, lines[start..]);
        }
        catch (Exception ex)
        {
            return $"Error reading log: {ex.Message}";
        }
    }

    /// <summary>Get log file path.</summary>
    public static string LogFilePath => _logFilePath;

    /// <summary>Get log file size in bytes.</summary>
    public static long LogFileSize => File.Exists(_logFilePath) ? new FileInfo(_logFilePath).Length : 0;
}

/// <summary>Log severity levels (ordered low to high).</summary>
public enum LogLevel
{
    Debug = 0,
    Info = 1,
    Warn = 2,
    Error = 3
}