using ECAssistant.Services;
using ECAssistant.UI;

namespace ECAssistant.Interfaces;

/// <summary>
/// Structured logging interface — writes to both file and console (via EGuiBase).
/// Implementations should be thread-safe and support daily log rotation.
/// </summary>
public interface ILogger
{
    /// <summary>Initialize the logger with file path and GUI reference.</summary>
    void Initialize(string logFilePath, EGuiBase gui, LogLevel minLevel);

    /// <summary>Set the minimum log level at runtime.</summary>
    void SetLevel(LogLevel level);

    /// <summary>Check if debug logging is currently enabled.</summary>
    bool IsDebugEnabled { get; }

    /// <summary>Debug log (suppressed unless level is Debug).</summary>
    void Debug(string tag, string message);

    /// <summary>Info log.</summary>
    void Info(string tag, string message);

    /// <summary>Warning log.</summary>
    void Warn(string tag, string message);

    /// <summary>Error log.</summary>
    void Error(string tag, string message);

    /// <summary>Error log with exception details.</summary>
    void Error(string tag, string message, Exception ex);

    /// <summary>Read recent log lines (for diagnostics command).</summary>
    string GetRecentLines(int count);

    /// <summary>Get log file path.</summary>
    string LogFilePath { get; }

    /// <summary>Get log file size in bytes.</summary>
    long LogFileSize { get; }
}