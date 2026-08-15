using ECAssistant.Interfaces;
using ECAssistant.UI;

namespace ECAssistant.Services;

/// <summary>
/// Lightweight structured logger — writes to both file and console (via EGuiBase).
/// No external dependencies. Rotates log files daily.
/// 
/// Usage:
///   var logger = new Logger();
///   logger.Initialize("ECAssistant.log", Gui);
///   logger.Info("Engine", "Model loaded successfully");
///   logger.Error("Orchestrator", $"Tool failed: {ex.Message}");
///   logger.Debug("Context", $"Token budget: {tokens}/{max}");
/// 
/// Levels (controlled by config or compile-time):
///   Debug < Info < Warn < Error
///   Default: Info (Debug suppressed unless enabled)
/// </summary>
public class Logger : ILogger
{
    // ANSI color codes (avoids dependency on EColor)
    private const string AnsiYellow = "\x1b[33m";
    private const string AnsiRed = "\x1b[31m";
    private const string AnsiDim = "\x1b[2m";
    private const string AnsiReset = "\x1b[0m";

    private string _logFilePath = "ECAssistant.log";
    private EGuiBase? _gui;
    private LogLevel _minLevel = LogLevel.Info;
    private readonly object _lock = new();
    private bool _initialized = false;

    /// <summary>Create a new logger instance. Call Initialize() before use.</summary>
    public Logger() { }

    /// <summary>Create and initialize a logger in one step.</summary>
    public Logger(string logFilePath, EGuiBase gui, LogLevel minLevel = LogLevel.Info)
    {
        Initialize(logFilePath, gui, minLevel);
    }

    /// <summary>Initialize the logger with file path and GUI reference.</summary>
    public void Initialize(string logFilePath, EGuiBase gui, LogLevel minLevel)
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
    public void SetLevel(LogLevel level) => _minLevel = level;

    /// <summary>Check if debug logging is currently enabled.</summary>
    public bool IsDebugEnabled => _minLevel <= LogLevel.Debug;

    /// <summary>Debug log (suppressed unless level is Debug).</summary>
    public void Debug(string tag, string message) => Log(LogLevel.Debug, tag, message);

    /// <summary>Info log.</summary>
    public void Info(string tag, string message) => Log(LogLevel.Info, tag, message);

    /// <summary>Warning log.</summary>
    public void Warn(string tag, string message) => Log(LogLevel.Warn, tag, message);

    /// <summary>Error log.</summary>
    public void Error(string tag, string message) => Log(LogLevel.Error, tag, message);

    /// <summary>Error log with exception details.</summary>
    public void Error(string tag, string message, Exception ex)
    {
        Log(LogLevel.Error, tag, $"{message} | {ex.GetType().Name}: {ex.Message}");
        if (ex.InnerException != null)
            Log(LogLevel.Error, tag, $"  Inner: {ex.InnerException.Message}");
    }

    /// <summary>Core log method — writes to file and optionally to console.</summary>
    private void Log(LogLevel level, string tag, string message)
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
            // Skip console output for LLAMA native logs (they go to file only)
            if (_gui != null && level >= LogLevel.Warn && !tag.Equals("LLAMA", StringComparison.OrdinalIgnoreCase))
            {
                var color = level switch
                {
                    LogLevel.Warn => AnsiYellow,
                    LogLevel.Error => AnsiRed,
                    _ => AnsiDim
                };
                _gui.LogInternal($"{color}{line}{AnsiReset}");
            }
        }
    }

    /// <summary>Read recent log lines (for diagnostics command).</summary>
    public string GetRecentLines(int count = 50)
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
    public string LogFilePath => _logFilePath;

    /// <summary>Get log file size in bytes.</summary>
    public long LogFileSize => File.Exists(_logFilePath) ? new FileInfo(_logFilePath).Length : 0;
}