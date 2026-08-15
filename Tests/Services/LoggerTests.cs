using ECAssistant.Services;
using ECAssistant.Interfaces;
using ECAssistant.UI;
using Moq;

namespace ECAssistant.Tests.Services;

public class LoggerTests : IDisposable
{
    private readonly string _tempLogPath;

    public LoggerTests()
    {
        _tempLogPath = Path.Combine(Path.GetTempPath(), "ECAssistantTests_Logger_" + Guid.NewGuid().ToString("N")[..8] + ".log");
    }

    public void Dispose()
    {
        try { File.Delete(_tempLogPath); } catch { }
    }

    private EGuiBase CreateMockGui()
    {
        var mock = new Mock<EGuiBase>();
        mock.Setup(g => g.LogInternal(It.IsAny<string>()));
        return mock.Object;
    }

    [Fact]
    public void Constructor_Default_CreatesInstanceNotInitialized()
    {
        var logger = new Logger();
        Assert.NotNull(logger);
    }

    [Fact]
    public void Constructor_WithParams_InitializesLogger()
    {
        var gui = CreateMockGui();
        var logger = new Logger(_tempLogPath, gui, LogLevel.Debug);
        Assert.Equal(Path.GetFullPath(_tempLogPath), logger.LogFilePath);
    }

    [Fact]
    public void Initialize_ValidPath_SetsLogFilePath()
    {
        var logger = new Logger();
        var gui = CreateMockGui();
        logger.Initialize(_tempLogPath, gui, LogLevel.Info);
        Assert.Equal(Path.GetFullPath(_tempLogPath), logger.LogFilePath);
    }

    [Fact]
    public void Initialize_CreatesLogDirectoryIfNotExists()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ECAssistantTests_LoggerDir_" + Guid.NewGuid().ToString("N")[..8]);
        var logPath = Path.Combine(dir, "test.log");
        var logger = new Logger();
        var gui = CreateMockGui();
        try
        {
            logger.Initialize(logPath, gui, LogLevel.Info);
            Assert.True(Directory.Exists(dir));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void Info_AfterInitialize_WritesToFile()
    {
        var gui = CreateMockGui();
        var logger = new Logger(_tempLogPath, gui, LogLevel.Debug);
        logger.Info("TestTag", "test info message");
        var content = File.ReadAllText(_tempLogPath);
        Assert.Contains("test info message", content);
        Assert.Contains("[INF]", content);
        Assert.Contains("[TestTag]", content);
    }

    [Fact]
    public void Debug_WhenDebugEnabled_WritesToFile()
    {
        var gui = CreateMockGui();
        var logger = new Logger(_tempLogPath, gui, LogLevel.Debug);
        logger.Debug("TestTag", "debug message");
        var content = File.ReadAllText(_tempLogPath);
        Assert.Contains("debug message", content);
        Assert.Contains("[DBG]", content);
    }

    [Fact]
    public void Debug_WhenDebugDisabled_DoesNotWriteToFile()
    {
        var gui = CreateMockGui();
        var logger = new Logger(_tempLogPath, gui, LogLevel.Info);
        // Clear the initial banner
        File.WriteAllText(_tempLogPath, "");
        logger.Debug("TestTag", "should not appear");
        var content = File.ReadAllText(_tempLogPath);
        Assert.DoesNotContain("should not appear", content);
    }

    [Fact]
    public void Warn_AfterInitialize_WritesToFile()
    {
        var gui = CreateMockGui();
        var logger = new Logger(_tempLogPath, gui, LogLevel.Info);
        File.WriteAllText(_tempLogPath, "");
        logger.Warn("TestTag", "warning message");
        var content = File.ReadAllText(_tempLogPath);
        Assert.Contains("warning message", content);
        Assert.Contains("[WRN]", content);
    }

    [Fact]
    public void Error_AfterInitialize_WritesToFile()
    {
        var gui = CreateMockGui();
        var logger = new Logger(_tempLogPath, gui, LogLevel.Info);
        File.WriteAllText(_tempLogPath, "");
        logger.Error("TestTag", "error message");
        var content = File.ReadAllText(_tempLogPath);
        Assert.Contains("error message", content);
        Assert.Contains("[ERR]", content);
    }

    [Fact]
    public void Error_WithException_LogsExceptionDetails()
    {
        var gui = CreateMockGui();
        var logger = new Logger(_tempLogPath, gui, LogLevel.Info);
        File.WriteAllText(_tempLogPath, "");
        var ex = new InvalidOperationException("something broke");
        logger.Error("TestTag", "operation failed", ex);
        var content = File.ReadAllText(_tempLogPath);
        Assert.Contains("operation failed", content);
        Assert.Contains("InvalidOperationException", content);
        Assert.Contains("something broke", content);
    }

    [Fact]
    public void Error_WithInnerException_LogsInnerException()
    {
        var gui = CreateMockGui();
        var logger = new Logger(_tempLogPath, gui, LogLevel.Info);
        File.WriteAllText(_tempLogPath, "");
        var inner = new ArgumentException("inner problem");
        var ex = new InvalidOperationException("outer problem", inner);
        logger.Error("TestTag", "failed", ex);
        var content = File.ReadAllText(_tempLogPath);
        Assert.Contains("Inner:", content);
        Assert.Contains("inner problem", content);
    }

    [Fact]
    public void SetLevel_ChangesMinimumLevel()
    {
        var gui = CreateMockGui();
        var logger = new Logger(_tempLogPath, gui, LogLevel.Debug);
        Assert.True(logger.IsDebugEnabled);
        logger.SetLevel(LogLevel.Info);
        Assert.False(logger.IsDebugEnabled);
    }

    [Fact]
    public void IsDebugEnabled_WhenLevelDebug_ReturnsTrue()
    {
        var gui = CreateMockGui();
        var logger = new Logger(_tempLogPath, gui, LogLevel.Debug);
        Assert.True(logger.IsDebugEnabled);
    }

    [Fact]
    public void IsDebugEnabled_WhenLevelInfo_ReturnsFalse()
    {
        var gui = CreateMockGui();
        var logger = new Logger(_tempLogPath, gui, LogLevel.Info);
        Assert.False(logger.IsDebugEnabled);
    }

    [Fact]
    public void GetRecentLines_ExistingLogFile_ReturnsRecentLines()
    {
        var gui = CreateMockGui();
        var logger = new Logger(_tempLogPath, gui, LogLevel.Info);
        File.WriteAllText(_tempLogPath, "line1\nline2\nline3\nline4\nline5\n");
        var recent = logger.GetRecentLines(3);
        Assert.Contains("line3", recent);
        Assert.Contains("line4", recent);
        Assert.Contains("line5", recent);
        Assert.DoesNotContain("line1", recent);
    }

    [Fact]
    public void GetRecentLines_NonExistentLogFile_ReturnsNoLogFileMessage()
    {
        var logger = new Logger();
        var result = logger.GetRecentLines(10);
        Assert.Contains("No log file", result);
    }

    [Fact]
    public void GetRecentLines_DefaultCount_ReturnsUpTo50Lines()
    {
        var gui = CreateMockGui();
        var logger = new Logger(_tempLogPath, gui, LogLevel.Info);
        var lines = Enumerable.Range(1, 60).Select(i => $"line{i}").ToArray();
        File.WriteAllText(_tempLogPath, string.Join("\n", lines) + "\n");
        var recent = logger.GetRecentLines();
        var lineCount = recent.Split(Environment.NewLine).Where(l => !string.IsNullOrEmpty(l)).Count();
        Assert.True(lineCount <= 50);
    }

    [Fact]
    public void LogFileSize_ExistingLogFile_ReturnsFileSize()
    {
        var gui = CreateMockGui();
        var logger = new Logger(_tempLogPath, gui, LogLevel.Info);
        logger.Info("Test", "some content");
        Assert.True(logger.LogFileSize > 0);
    }

    [Fact]
    public void LogFileSize_NonExistentLogFile_ReturnsZero()
    {
        var logger = new Logger();
        Assert.Equal(0, logger.LogFileSize);
    }

    [Fact]
    public void Log_WithoutInitialize_DoesNotWrite()
    {
        var logger = new Logger();
        // Should not throw
        logger.Info("Test", "message");
        logger.Error("Test", "error");
    }
}